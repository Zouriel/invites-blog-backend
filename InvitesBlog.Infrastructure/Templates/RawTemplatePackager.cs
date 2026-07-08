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

    // An image slot = any element carrying data-src. We capture the whole opening tag so we can also
    // read an optional data-slot-label off the same element.
    [GeneratedRegex("""<[a-zA-Z][^>]*?\bdata-src\b[^>]*>""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ImageTagRegex();

    [GeneratedRegex("""\bdata-src\s*=\s*"([^"]*)"|\bdata-src\s*=\s*'([^']*)'""", RegexOptions.IgnoreCase)]
    private static partial Regex DataSrcRegex();

    [GeneratedRegex("""\bdata-slot-label\s*=\s*"([^"]*)"|\bdata-slot-label\s*=\s*'([^']*)'""", RegexOptions.IgnoreCase)]
    private static partial Regex SlotLabelRegex();

    [GeneratedRegex("(?<=[a-z0-9])(?=[A-Z])")]
    private static partial Regex CamelBoundaryRegex();

    // A fillable field = any element carrying data-var or data-href. Capture the whole opening tag so
    // we can also read optional data-field-label / data-field-type off the same element.
    [GeneratedRegex("""<[a-zA-Z][^>]*?\b(?:data-var|data-href)\b[^>]*>""", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex FieldTagRegex();

    [GeneratedRegex("""\bdata-var\s*=\s*"([^"]*)"|\bdata-var\s*=\s*'([^']*)'""", RegexOptions.IgnoreCase)]
    private static partial Regex DataVarRegex();

    [GeneratedRegex("""\bdata-href\s*=\s*"([^"]*)"|\bdata-href\s*=\s*'([^']*)'""", RegexOptions.IgnoreCase)]
    private static partial Regex DataHrefRegex();

    [GeneratedRegex("""\bdata-field-label\s*=\s*"([^"]*)"|\bdata-field-label\s*=\s*'([^']*)'""", RegexOptions.IgnoreCase)]
    private static partial Regex FieldLabelRegex();

    [GeneratedRegex("""\bdata-field-type\s*=\s*"([^"]*)"|\bdata-field-type\s*=\s*'([^']*)'""", RegexOptions.IgnoreCase)]
    private static partial Regex FieldTypeRegex();

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
        var imageSlots = CollectImageSlots(html);
        var fields = CollectFields(html);

        var manifest = new TemplateManifest
        {
            Slug = slug,
            Version = version,
            Variables = variables.ToList(),
            ContentBlocks = contentBlocks.ToList(),
            ImageSlots = imageSlots,
            Fields = fields,
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

    /// <summary>
    /// One image slot per distinct <c>data-src</c> path. The label comes from an optional
    /// <c>data-slot-label</c> on the same element, otherwise it's derived from the path.
    /// </summary>
    private static List<TemplateImageSlot> CollectImageSlots(string html)
    {
        var byKey = new Dictionary<string, TemplateImageSlot>(StringComparer.Ordinal);
        foreach (Match tag in ImageTagRegex().Matches(html))
        {
            var src = DataSrcRegex().Match(tag.Value);
            if (!src.Success) continue;
            var key = (src.Groups[1].Success ? src.Groups[1].Value : src.Groups[2].Value).Trim();
            if (string.IsNullOrWhiteSpace(key) || byKey.ContainsKey(key)) continue;

            var lbl = SlotLabelRegex().Match(tag.Value);
            var label = lbl.Success
                ? (lbl.Groups[1].Success ? lbl.Groups[1].Value : lbl.Groups[2].Value).Trim()
                : Prettify(key);
            byKey[key] = new TemplateImageSlot { Key = key, Label = string.IsNullOrWhiteSpace(label) ? Prettify(key) : label };
        }
        return byKey.Values.OrderBy(s => s.Key, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// One field per distinct <c>data-var</c>/<c>data-href</c> path. Label comes from an optional
    /// <c>data-field-label</c>, and the widget type from an optional <c>data-field-type</c> (else inferred).
    /// </summary>
    private static List<TemplateFieldSlot> CollectFields(string html)
    {
        var byKey = new Dictionary<string, TemplateFieldSlot>(StringComparer.Ordinal);
        foreach (Match tag in FieldTagRegex().Matches(html))
        {
            var varMatch = DataVarRegex().Match(tag.Value);
            var hrefMatch = DataHrefRegex().Match(tag.Value);

            bool isHref;
            string key;
            if (varMatch.Success)
            {
                key = (varMatch.Groups[1].Success ? varMatch.Groups[1].Value : varMatch.Groups[2].Value).Trim();
                isHref = false;
            }
            else if (hrefMatch.Success)
            {
                key = (hrefMatch.Groups[1].Success ? hrefMatch.Groups[1].Value : hrefMatch.Groups[2].Value).Trim();
                isHref = true;
            }
            else continue;

            if (string.IsNullOrWhiteSpace(key) || byKey.ContainsKey(key)) continue;

            var lbl = FieldLabelRegex().Match(tag.Value);
            var label = lbl.Success
                ? (lbl.Groups[1].Success ? lbl.Groups[1].Value : lbl.Groups[2].Value).Trim()
                : Prettify(key);

            var explicitType = FieldTypeRegex().Match(tag.Value);
            var type = explicitType.Success
                ? (explicitType.Groups[1].Success ? explicitType.Groups[1].Value : explicitType.Groups[2].Value).Trim().ToLowerInvariant()
                : InferFieldType(key, isHref);

            byKey[key] = new TemplateFieldSlot
            {
                Key = key,
                Label = string.IsNullOrWhiteSpace(label) ? Prettify(key) : label,
                Type = string.IsNullOrWhiteSpace(type) ? "text" : type
            };
        }
        return byKey.Values.OrderBy(f => f.Key, StringComparer.Ordinal).ToList();
    }

    /// <summary>Guesses a widget type from the field's leaf name (date/time/textarea/url/text).</summary>
    private static string InferFieldType(string key, bool isHref)
    {
        var leaf = key.Split('.').Last().ToLowerInvariant();
        if (leaf.Contains("date")) return "date";
        if (leaf.Contains("time")) return "time";
        if (leaf.Contains("description") || leaf.Contains("schedule") || leaf.Contains("note")
            || leaf.Contains("message") || leaf.Contains("story") || leaf.Contains("address")) return "textarea";
        if (isHref || leaf.Contains("link") || leaf.Contains("url")) return "url";
        return "text";
    }

    /// <summary>Turns a data path like <c>event.coverImage</c> into a readable label ("Cover image").</summary>
    private static string Prettify(string path)
    {
        var last = path.Split('.').Last().Replace('_', ' ').Replace('-', ' ');
        var spaced = CamelBoundaryRegex().Replace(last, " ").Trim();
        if (spaced.Length == 0) return path;
        return char.ToUpperInvariant(spaced[0]) + spaced[1..].ToLowerInvariant();
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
