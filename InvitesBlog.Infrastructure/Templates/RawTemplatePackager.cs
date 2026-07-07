using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.TemplateCompiler;

namespace InvitesBlog.Infrastructure.Templates;

public sealed record RawPublishedPackage(string PackageUrl, TemplateManifest Manifest, string ManifestJson);

/// <summary>
/// Publishes an admin-authored raw HTML/CSS template into the standard package layout
/// (<c>templates/{slug}@{version}/</c>) so it renders through the exact same sandboxed pipeline as
/// compiled templates. The author writes HTML + CSS only — never JS: any &lt;script&gt; is stripped and
/// the single trusted <see cref="TemplateInjector"/> is wired in. The manifest is auto-derived by
/// scanning the tags (<c>data-var/href/src</c> → variables, <c>data-block</c> → content blocks).
/// </summary>
public sealed partial class RawTemplatePackager(IStorageService storage)
{
    [GeneratedRegex("""data-(?:var|href|src)\s*=\s*"([^"]+)"|data-(?:var|href|src)\s*=\s*'([^']+)'""", RegexOptions.IgnoreCase)]
    private static partial Regex VarAttrRegex();

    [GeneratedRegex("""data-block\s*=\s*"([^"]+)"|data-block\s*=\s*'([^']+)'""", RegexOptions.IgnoreCase)]
    private static partial Regex BlockAttrRegex();

    [GeneratedRegex("<script\\b[^>]*>.*?</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptTagRegex();

    private static readonly JsonSerializerOptions JsonOut = new() { WriteIndented = false };

    /// <param name="allowScripts">
    /// Admin/first-party templates may ship their own JS for richer animation (kept as-is). Set false
    /// for untrusted (e.g. community-submitted) authors to strip all &lt;script&gt; tags.
    /// </param>
    public async Task<RawPublishedPackage> PublishAsync(
        string slug, string version, string html, string? css,
        bool allowScripts = true, CancellationToken ct = default)
    {
        var variables = Collect(VarAttrRegex(), html);
        var contentBlocks = Collect(BlockAttrRegex(), html);

        var manifest = new TemplateManifest
        {
            Slug = slug,
            Version = version,
            Variables = variables.ToList(),
            ContentBlocks = contentBlocks.ToList(),
            Roles = new(),
            GenderVariants = new(),
            EditableAreas = new()
        };

        var finalHtml = WireInjector(html, hasCss: !string.IsNullOrWhiteSpace(css), allowScripts);
        var basePath = $"templates/{slug}@{version}";

        await storage.PutAsync($"{basePath}/index.html", Bytes(finalHtml), "text/html", ct);
        await storage.PutAsync($"{basePath}/styles.css", Bytes(css ?? ""), "text/css", ct);
        await storage.PutAsync($"{basePath}/template.js", Bytes(TemplateInjector.Js), "application/javascript", ct);
        var manifestJson = JsonSerializer.Serialize(manifest, JsonOut);
        await storage.PutAsync($"{basePath}/manifest.json", Bytes(manifestJson), "application/json", ct);

        return new RawPublishedPackage(storage.PublicUrl($"{basePath}/"), manifest, manifestJson);
    }

    private static SortedSet<string> Collect(Regex regex, string html)
    {
        var set = new SortedSet<string>(StringComparer.Ordinal);
        foreach (Match m in regex.Matches(html))
        {
            var value = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            if (!string.IsNullOrWhiteSpace(value)) set.Add(value.Trim());
        }
        return set;
    }

    /// <summary>
    /// Ensure the stylesheet link and inject the trusted injector. Author &lt;script&gt; tags are kept for
    /// trusted (admin) templates and stripped otherwise.
    /// </summary>
    private static string WireInjector(string html, bool hasCss, bool allowScripts)
    {
        var cleaned = allowScripts ? html : ScriptTagRegex().Replace(html, "");

        if (hasCss && !cleaned.Contains("styles.css", StringComparison.OrdinalIgnoreCase))
        {
            const string link = "<link rel=\"stylesheet\" href=\"styles.css\">";
            cleaned = InsertBefore(cleaned, "</head>", link) is { } withLink
                ? withLink
                : link + cleaned;
        }

        var injection = TemplateInjector.InviteDataScript + "\n<script src=\"template.js\"></script>";
        return InsertBefore(cleaned, "</body>", injection) ?? cleaned + "\n" + injection;
    }

    private static string? InsertBefore(string html, string marker, string insertion)
    {
        var idx = html.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return idx < 0 ? null : html[..idx] + insertion + "\n" + html[idx..];
    }

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);
}
