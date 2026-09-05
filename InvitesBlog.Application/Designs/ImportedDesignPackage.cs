using System.IO.Compression;
using System.Text;
using InvitesBlog.Application.Exceptions;

namespace InvitesBlog.Application.Designs;

/// <summary>
/// Turns what a customer uploaded into something the renderer can serve.
///
/// <para>Two shapes arrive here. An <b>image</b> (or a clip) is what almost everyone has, because the
/// design tools people actually use export a picture — Canva's own interface offers no HTML download
/// at all. A <b>zip</b> is the rarer, richer case: one HTML file and its assets, which is the shape
/// of a real template.</para>
///
/// <para><b>Nothing in a zip is trusted.</b> It arrived from the internet, from an account that
/// needed no review to open. Every entry is checked for where it claims to live, how big it claims to
/// be, and whether it is a kind of file we are willing to store at all — before a single byte is
/// written anywhere.</para>
/// </summary>
public static class ImportedDesignPackage
{
    /// <summary>
    /// Total uncompressed bytes we will extract from one upload. The guard is on the OUTPUT, not the
    /// zip's own size: a few hundred kilobytes of zip can claim to be a hundred gigabytes, and it is
    /// the writing that would take the disk down.
    /// </summary>
    public const long MaxExtractedBytes = 80L * 1024 * 1024;

    /// <summary>How many files one design may contain. A card is a handful; a thousand is an attack.</summary>
    public const int MaxEntries = 400;

    /// <summary>
    /// The most one entry may expand to. Bounds a single hostile member even when the total would
    /// have passed.
    /// </summary>
    public const long MaxEntryBytes = 40L * 1024 * 1024;

    /// <summary>
    /// What a design is allowed to be made of. An allowlist rather than a blocklist: the set of
    /// things a picture-and-markup bundle legitimately needs is small and known, and everything
    /// outside it — an executable, a PHP file, a symlink pretending to be a font — has no business
    /// being stored on our behalf.
    /// </summary>
    private static readonly HashSet<string> Allowed = new(StringComparer.OrdinalIgnoreCase)
    {
        ".html", ".htm", ".css", ".js",
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".avif", ".svg",
        ".woff", ".woff2", ".ttf", ".otf",
        ".mp4", ".webm", ".json",
    };

    /// <summary>Image and clip types accepted as a whole design on their own.</summary>
    private static readonly HashSet<string> StandaloneMedia = new(StringComparer.OrdinalIgnoreCase)
    {
        ".png", ".jpg", ".jpeg", ".gif", ".webp", ".avif", ".mp4", ".webm",
    };

    public static bool IsStandaloneMedia(string fileName) =>
        StandaloneMedia.Contains(Path.GetExtension(fileName));

    public static bool IsZip(string fileName) =>
        string.Equals(Path.GetExtension(fileName), ".zip", StringComparison.OrdinalIgnoreCase);

    /// <summary>One file lifted out of an upload, with the path it will be stored under.</summary>
    /// <param name="Path">Normalised, relative, and guaranteed not to escape the design's folder.</param>
    public sealed record Entry(string Path, byte[] Content)
    {
        public bool IsDocument =>
            System.IO.Path.GetExtension(Path) is ".html" or ".htm";
    }

    /// <summary>What came out of a zip: the document, and everything it refers to.</summary>
    /// <param name="Document">The entry point. Never stored where a browser can reach it directly.</param>
    /// <param name="Assets">Images, fonts, stylesheets — inert files the browser loads normally.</param>
    public sealed record Unpacked(Entry Document, IReadOnlyList<Entry> Assets);

    /// <summary>
    /// Reads a zip into memory, refusing anything that looks like an attack rather than a design.
    /// </summary>
    public static Unpacked Unpack(byte[] zip)
    {
        using var stream = new MemoryStream(zip, writable: false);
        ZipArchive archive;
        try
        {
            archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
        }
        catch (InvalidDataException)
        {
            throw new BusinessRuleException("That file isn't a zip we can read.", "design_not_a_zip");
        }

        using (archive)
        {
            var files = archive.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
            if (files.Count == 0)
                throw new BusinessRuleException("That zip is empty.", "design_empty");
            if (files.Count > MaxEntries)
                throw new BusinessRuleException(
                    $"That design has more than {MaxEntries} files in it.", "design_too_many_files");

            var entries = new List<Entry>(files.Count);
            long total = 0;

            foreach (var raw in files)
            {
                var path = SafePath(raw.FullName);

                if (!Allowed.Contains(Path.GetExtension(path)))
                    throw new BusinessRuleException(
                        $"“{Path.GetFileName(path)}” isn't a kind of file a design can contain.",
                        "design_bad_file_type");

                // The declared length is checked first so an obvious bomb is refused without reading
                // it, and the real length is checked again while copying — a zip header is a claim,
                // not a fact.
                if (raw.Length > MaxEntryBytes)
                    throw new BusinessRuleException(
                        $"“{Path.GetFileName(path)}” is too big.", "design_file_too_big");

                total += raw.Length;
                if (total > MaxExtractedBytes)
                    throw new BusinessRuleException("That design is too big.", "design_too_big");

                entries.Add(new Entry(path, Read(raw)));
            }

            // The entry point: index.html at the root if there is one, else the shallowest document
            // in the zip. Design tools bury their output one folder deep about half the time, and
            // making somebody re-zip their own export to satisfy us is not a real requirement.
            var document =
                entries.FirstOrDefault(e => e.Path.Equals("index.html", StringComparison.OrdinalIgnoreCase))
                ?? entries.Where(e => e.IsDocument)
                    .OrderBy(e => e.Path.Count(c => c == '/'))
                    .ThenBy(e => e.Path.Length)
                    .FirstOrDefault()
                ?? throw new BusinessRuleException(
                    "That zip has no HTML page in it.", "design_no_document");

            return new Unpacked(document, entries.Where(e => e != document).ToList());
        }
    }

    /// <summary>
    /// Where an entry may be written, or a refusal.
    ///
    /// <para>This is the zip-slip guard. A member may call itself <c>../../etc/passwd</c> or
    /// <c>C:\windows\x</c>, and a naive extractor writes exactly there. Backslashes are folded first
    /// because a zip written on Windows uses them and a check that only knows about <c>/</c> reads
    /// <c>..\..\x</c> as an ordinary filename.</para>
    /// </summary>
    private static string SafePath(string declared)
    {
        var path = declared.Replace('\\', '/').TrimStart('/');

        if (path.Length == 0 || Path.IsPathRooted(declared) || declared.Contains(':'))
            throw new BusinessRuleException("That zip contains a file path we can't accept.", "design_bad_path");

        var parts = new List<string>();
        foreach (var segment in path.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
                throw new BusinessRuleException(
                    "That zip contains a file path we can't accept.", "design_bad_path");
            parts.Add(segment);
        }

        if (parts.Count == 0)
            throw new BusinessRuleException("That zip contains a file path we can't accept.", "design_bad_path");

        return string.Join('/', parts);
    }

    private static byte[] Read(ZipArchiveEntry entry)
    {
        using var source = entry.Open();
        using var buffer = new MemoryStream();

        // Copied through a bounded read rather than CopyTo: the entry's declared length was already
        // checked, and this is what makes a lie about it stop at the same ceiling.
        var chunk = new byte[81920];
        long written = 0;
        int read;
        while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
        {
            written += read;
            if (written > MaxEntryBytes)
                throw new BusinessRuleException(
                    $"“{Path.GetFileName(entry.FullName)}” is too big.", "design_file_too_big");
            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Rewrites the document's references to point at where the assets were actually stored.
    ///
    /// <para>A bundle refers to its own files relatively — <c>images/bg.png</c> — and once those are
    /// in object storage under a campaign's own prefix, every one of those references is wrong. They
    /// are replaced textually rather than through a DOM pass because the reference can appear in an
    /// attribute, in a <c>style</c> rule, or inside a <c>url()</c> in a stylesheet, and a parser only
    /// finds the first of those.</para>
    ///
    /// <para>Longest path first: <c>images/bg.png</c> must be replaced before <c>bg.png</c>, or the
    /// shorter match corrupts the longer path it sits inside.</para>
    /// </summary>
    public static string Rewrite(string html, IReadOnlyDictionary<string, string> assetUrls)
    {
        var builder = new StringBuilder(html);

        foreach (var (path, url) in assetUrls.OrderByDescending(p => p.Key.Length))
        {
            builder.Replace($"\"{path}\"", $"\"{url}\"");
            builder.Replace($"'{path}'", $"'{url}'");
            builder.Replace($"\"./{path}\"", $"\"{url}\"");
            builder.Replace($"'./{path}'", $"'{url}'");
            builder.Replace($"({path})", $"({url})");
            builder.Replace($"(./{path})", $"({url})");
        }

        return builder.ToString();
    }
}
