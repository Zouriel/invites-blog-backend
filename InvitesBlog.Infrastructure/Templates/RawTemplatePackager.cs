using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.TemplateCompiler;

namespace InvitesBlog.Infrastructure.Templates;

public sealed record RawPublishedPackage(string PackageUrl, TemplateManifest Manifest, string ManifestJson);

/// <summary>
/// Publishes an admin-authored template — a SINGLE self-contained <c>index.html</c> that inlines its
/// own CSS (<c>&lt;style&gt;</c>) and JS (<c>&lt;script&gt;</c>). External or separate stylesheets/scripts are
/// rejected (§ single-file rule). The trusted <see cref="TemplateInjector"/> is inlined too, so the
/// served file is one document. The manifest is auto-derived by scanning the tags
/// (<c>data-var/href/src</c> → variables, <c>data-block</c> → content blocks).
/// </summary>
public sealed partial class RawTemplatePackager(IStorageService storage)
{
    [GeneratedRegex("""data-(?:var|href|src)\s*=\s*"([^"]+)"|data-(?:var|href|src)\s*=\s*'([^']+)'""", RegexOptions.IgnoreCase)]
    private static partial Regex VarAttrRegex();

    [GeneratedRegex("""data-block\s*=\s*"([^"]+)"|data-block\s*=\s*'([^']+)'""", RegexOptions.IgnoreCase)]
    private static partial Regex BlockAttrRegex();

    [GeneratedRegex("<script\\b[^>]*>.*?</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptTagRegex();

    // Single-file enforcement: an external/separate stylesheet or an external script are not allowed.
    [GeneratedRegex("""<link\b[^>]*rel\s*=\s*["']?\s*stylesheet""", RegexOptions.IgnoreCase)]
    private static partial Regex StylesheetLinkRegex();

    [GeneratedRegex("""<script\b[^>]*\bsrc\s*=""", RegexOptions.IgnoreCase)]
    private static partial Regex ExternalScriptRegex();

    private static readonly JsonSerializerOptions JsonOut = new() { WriteIndented = false };

    /// <param name="allowScripts">
    /// Admin/first-party templates may ship their own (inline) JS (kept). Set false for untrusted
    /// (e.g. community) authors to strip all inline &lt;script&gt; tags.
    /// </param>
    public async Task<RawPublishedPackage> PublishAsync(
        string slug, string version, string html, bool allowScripts = true, CancellationToken ct = default)
    {
        EnsureSelfContained(html);

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

        var finalHtml = WireInjector(html, allowScripts);
        var basePath = $"templates/{slug}@{version}";

        // One self-contained document is served to the sandboxed iframe; the manifest is platform metadata.
        await storage.PutAsync($"{basePath}/index.html", Bytes(finalHtml), "text/html", ct);
        var manifestJson = JsonSerializer.Serialize(manifest, JsonOut);
        await storage.PutAsync($"{basePath}/manifest.json", Bytes(manifestJson), "application/json", ct);

        return new RawPublishedPackage(storage.PublicUrl($"{basePath}/"), manifest, manifestJson);
    }

    /// <summary>Reject anything that isn't self-contained in the single HTML file.</summary>
    private static void EnsureSelfContained(string html)
    {
        if (StylesheetLinkRegex().IsMatch(html))
            throw new BusinessRuleException(
                "A template must be one self-contained file — inline your CSS in a <style> tag (no <link rel=\"stylesheet\"> / separate .css).",
                "template_not_self_contained");
        if (ExternalScriptRegex().IsMatch(html))
            throw new BusinessRuleException(
                "A template must be one self-contained file — inline your JavaScript in a <script> tag (no external <script src>).",
                "template_not_self_contained");
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

    /// <summary>Strip author scripts only for untrusted authors, then inline the trusted injector.</summary>
    private static string WireInjector(string html, bool allowScripts)
    {
        var cleaned = allowScripts ? html : ScriptTagRegex().Replace(html, "");
        var injection = TemplateInjector.InviteDataScript + "\n<script>" + TemplateInjector.Js + "</script>";
        return InsertBefore(cleaned, "</body>", injection) ?? cleaned + "\n" + injection;
    }

    private static string? InsertBefore(string html, string marker, string insertion)
    {
        var idx = html.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return idx < 0 ? null : html[..idx] + insertion + "\n" + html[idx..];
    }

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);
}
