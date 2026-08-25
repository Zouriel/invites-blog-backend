using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.TemplateCompiler;

namespace InvitesBlog.Infrastructure.Templates;

public sealed record RawPublishedPackage(string PackageUrl, TemplateManifest Manifest, string ManifestJson);

/// <summary>
/// Publishes a template — a SINGLE self-contained <c>index.html</c> that inlines its own CSS
/// (<c>&lt;style&gt;</c>). Separate stylesheets are rejected (§ single-file rule) and so is ALL author
/// JavaScript, for every author including admins (§ HTML/CSS-only). The trusted
/// <see cref="TemplateInjector"/> is inlined, so the served file is one document whose only script is
/// ours. The manifest is auto-derived by scanning the tags
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

    // data-type is the canonical authoring attribute; data-field-type stays supported as its legacy alias.
    [GeneratedRegex("""\bdata-type\s*=\s*"([^"]*)"|\bdata-type\s*=\s*'([^']*)'""", RegexOptions.IgnoreCase)]
    private static partial Regex DataTypeRegex();

    [GeneratedRegex("""\bdata-options\s*=\s*"([^"]*)"|\bdata-options\s*=\s*'([^']*)'""", RegexOptions.IgnoreCase)]
    private static partial Regex DataOptionsRegex();

    [GeneratedRegex("""\bdata-role-scope\s*=\s*"([^"]*)"|\bdata-role-scope\s*=\s*'([^']*)'""", RegexOptions.IgnoreCase)]
    private static partial Regex RoleScopeRegex();

    [GeneratedRegex("""\bdata-multiple\s*=\s*"([^"]*)"|\bdata-multiple\s*=\s*'([^']*)'""", RegexOptions.IgnoreCase)]
    private static partial Regex MultipleRegex();

    [GeneratedRegex("""\bdata-min-images\s*=\s*"([^"]*)"|\bdata-min-images\s*=\s*'([^']*)'""", RegexOptions.IgnoreCase)]
    private static partial Regex MinImagesRegex();

    [GeneratedRegex("""\bdata-max-images\s*=\s*"([^"]*)"|\bdata-max-images\s*=\s*'([^']*)'""", RegexOptions.IgnoreCase)]
    private static partial Regex MaxImagesRegex();

    // Theming: every --ib-* custom property the author declares, with the value they defaulted it to.
    [GeneratedRegex("""--ib-([a-zA-Z0-9-]+)\s*:\s*([^;}]+)""", RegexOptions.IgnoreCase)]
    private static partial Regex ThemeVarRegex();

    // <meta name="ib-roles" content="bride,groom"> / <meta name="ib-fonts" content="Lora, Inter">
    [GeneratedRegex("""<meta\b[^>]*\bname\s*=\s*["']ib-(roles|fonts)["'][^>]*>""", RegexOptions.IgnoreCase)]
    private static partial Regex IbMetaRegex();

    [GeneratedRegex("""\bcontent\s*=\s*"([^"]*)"|\bcontent\s*=\s*'([^']*)'""", RegexOptions.IgnoreCase)]
    private static partial Regex MetaContentRegex();

    [GeneratedRegex("""^\s*(#[0-9a-fA-F]{3,8}|rgba?\(|hsla?\(|color\()""")]
    private static partial Regex ColorValueRegex();

    // Single-file enforcement: an external/separate stylesheet is not allowed.
    [GeneratedRegex("""<link\b[^>]*rel\s*=\s*["']?\s*stylesheet""", RegexOptions.IgnoreCase)]
    private static partial Regex StylesheetLinkRegex();

    // --- Safety scan (§HTML/CSS-only platform-wide) ---------------------------------------------
    [GeneratedRegex("""<script\b""", RegexOptions.IgnoreCase)]
    private static partial Regex AnyScriptRegex();

    // on<event>= as a real ATTRIBUTE — preceded by whitespace so "on" inside a word can't trip it.
    [GeneratedRegex("""\s\bon[a-z]+\s*=""", RegexOptions.IgnoreCase)]
    private static partial Regex InlineHandlerRegex();

    [GeneratedRegex("""javascript\s*:""", RegexOptions.IgnoreCase)]
    private static partial Regex JavascriptUriRegex();

    [GeneratedRegex("""<meta\b[^>]*\bhttp-equiv\s*=\s*["']?\s*refresh""", RegexOptions.IgnoreCase)]
    private static partial Regex MetaRefreshRegex();

    private static readonly JsonSerializerOptions JsonOut = new() { WriteIndented = false };

    /// <summary>Publishes to the live template path — <c>templates/{slug}@{version}/</c>.</summary>
    public Task<RawPublishedPackage> PublishAsync(
        string slug, string version, string html, bool allowScripts = false, CancellationToken ct = default) =>
        PublishToAsync($"templates/{slug}@{version}", slug, version, html, allowScripts, ct);

    /// <summary>
    /// Publishes to an arbitrary base path. The review pipeline uses this to stage a submission
    /// somewhere reviewable (<c>submissions/{id}/</c>) WITHOUT it becoming a live gallery template —
    /// promotion to <c>templates/…</c> happens only on approval.
    /// </summary>
    public async Task<RawPublishedPackage> PublishToAsync(
        string basePath, string slug, string version, string html, bool allowScripts = false,
        CancellationToken ct = default)
    {
        EnsureSelfContainedAndSafe(html, allowScripts);

        var manifest = BuildManifest(slug, version, html);

        var finalHtml = WireInjector(html);

        // One self-contained document is served to the sandboxed iframe; the manifest is platform metadata.
        await storage.PutAsync($"{basePath}/index.html", Bytes(finalHtml), "text/html", ct);
        var manifestJson = JsonSerializer.Serialize(manifest, JsonOut);
        await storage.PutAsync($"{basePath}/manifest.json", Bytes(manifestJson), "application/json", ct);

        return new RawPublishedPackage(storage.PublicUrl($"{basePath}/"), manifest, manifestJson);
    }

    /// <summary>
    /// Derives the platform manifest (variables, content blocks, image slots, fillable fields) by
    /// scanning the template's tags — no storage writes, no injector wiring. Used both when publishing
    /// and when re-deriving an already-stored template's manifest (idempotent: the inlined injector
    /// script carries no <c>data-var/href/src="…"</c> attributes, so re-scanning served HTML is safe).
    /// </summary>
    public TemplateManifest BuildManifest(string slug, string version, string html)
    {
        var imageSlots = CollectImageSlots(html);
        var fields = CollectFields(html);
        var theme = CollectTheme(html);
        var roles = CollectRoles(html, fields, imageSlots);

        return new TemplateManifest
        {
            Slug = slug,
            Version = version,
            Variables = Collect(VarAttrRegex(), html).ToList(),
            ContentBlocks = Collect(BlockAttrRegex(), html).ToList(),
            ImageSlots = imageSlots,
            Fields = fields,
            Theme = theme,
            Roles = roles.Select(r => r.Slug).ToList(),
            RoleDefinitions = roles.Select(r => new TemplateRoleDefinition
            {
                Slug = r.Slug,
                Label = r.Label,
                ThemeKeys = theme.Keys.Select(k => k.Key).ToList(),
                Fields = fields.Where(f => Same(f.RoleScope, r.Slug)).Select(f => f.Key).ToList(),
                ImageSlots = imageSlots.Where(s => Same(s.RoleScope, r.Slug)).Select(s => s.Key).ToList()
            }).ToList(),
            GenderVariants = new(),
            EditableAreas = new()
        };
    }

    /// <summary>The hard ceiling a submission may not exceed. The recommended budget is much lower.</summary>
    public const int MaxTemplateBytes = 800 * 1024;

    /// <summary>The soft budget shown to designers — over this is a warning, not a rejection.</summary>
    public const int RecommendedTemplateBytes = 300 * 1024;

    /// <summary>
    /// Rejects anything that isn't self-contained AND anything that could execute. The product decision
    /// is HTML/CSS-only platform-wide, so there is deliberately NO trusted-author exemption: an admin
    /// upload is scanned exactly like a community submission. This throws rather than silently
    /// stripping, so an author learns their template was changed instead of discovering it in production.
    /// </summary>
    /// <param name="allowScripts">
    /// True only for first-party templates that ship in this repository, where the markup is code
    /// reviewed like any other source file. Designer submissions keep the ban: they arrive from
    /// outside and the sandbox alone is not a licence to run a stranger's JavaScript.
    /// </param>
    public static void EnsureSelfContainedAndSafe(string html, bool allowScripts = false)
    {
        EnsureSelfContained(html);

        var bytes = Encoding.UTF8.GetByteCount(html);
        if (bytes > MaxTemplateBytes)
            throw new BusinessRuleException(
                $"This template is {bytes / 1024}KB — the hard limit is {MaxTemplateBytes / 1024}KB. " +
                $"Compress or drop some embedded images (we recommend staying under {RecommendedTemplateBytes / 1024}KB).",
                "template_too_large");

        if (!allowScripts && AnyScriptRegex().IsMatch(html))
            throw new BusinessRuleException(
                "Templates are HTML and CSS only — remove the <script> tag. Use CSS animations and the "
                + "data-reveal / data-envelope hooks for motion.",
                "template_script_not_allowed");

        if (InlineHandlerRegex().IsMatch(html))
            throw new BusinessRuleException(
                "Inline event handlers (onclick, onload, …) aren't allowed — remove them and use CSS instead.",
                "template_inline_handler_not_allowed");

        if (JavascriptUriRegex().IsMatch(html))
            throw new BusinessRuleException(
                "A javascript: URL isn't allowed — links must point at a real address.",
                "template_javascript_uri_not_allowed");

        if (MetaRefreshRegex().IsMatch(html))
            throw new BusinessRuleException(
                "<meta http-equiv=\"refresh\"> isn't allowed — a template must not navigate on its own.",
                "template_meta_refresh_not_allowed");
    }

    /// <summary>Reject anything that isn't self-contained in the single HTML file.</summary>
    private static void EnsureSelfContained(string html)
    {
        if (StylesheetLinkRegex().IsMatch(html))
            throw new BusinessRuleException(
                "A template must be one self-contained file — inline your CSS in a <style> tag (no <link rel=\"stylesheet\"> / separate .css).",
                "template_not_self_contained");
    }

    private readonly record struct SlotOccurrence(
        string Key, string? Label, bool? Multiple, int? MinImages, int? MaxImages, string? RoleScope);
    private readonly record struct FieldOccurrence(
        string Key, bool IsHref, string? Label, string? Type, List<string>? Options, string? RoleScope);

    /// <summary>
    /// One image slot per distinct <c>data-src</c> path (case-insensitive). The same path used on many
    /// elements yields ONE slot; filling it feeds every element carrying that path. The label comes from
    /// an optional <c>data-slot-label</c> — when occurrences differ, the labeled one wins (else derived).
    /// </summary>
    private static List<TemplateImageSlot> CollectImageSlots(string html)
    {
        var occurrences = new List<SlotOccurrence>();
        foreach (Match tag in ImageTagRegex().Matches(html))
        {
            var src = DataSrcRegex().Match(tag.Value);
            if (!src.Success) continue;
            var key = (src.Groups[1].Success ? src.Groups[1].Value : src.Groups[2].Value).Trim();
            if (string.IsNullOrWhiteSpace(key)) continue;

            occurrences.Add(new SlotOccurrence(
                key,
                Attr(tag.Value, SlotLabelRegex()),
                ParseBool(Attr(tag.Value, MultipleRegex())),
                ParseCount(Attr(tag.Value, MinImagesRegex())),
                ParseCount(Attr(tag.Value, MaxImagesRegex())),
                Slugify(Attr(tag.Value, RoleScopeRegex()))));
        }

        return occurrences
            .GroupBy(o => o.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var key = g.First().Key; // first-seen casing is canonical
                var label = g.Select(o => o.Label).FirstOrDefault(l => l is not null) ?? Prettify(key);
                var multiple = g.Select(o => o.Multiple).FirstOrDefault(m => m is not null) ?? false;
                return new TemplateImageSlot
                {
                    Key = key,
                    Label = label,
                    Multiple = multiple,
                    // Count bounds only mean anything for a gallery slot.
                    MinImages = multiple ? g.Select(o => o.MinImages).FirstOrDefault(v => v is not null) : null,
                    MaxImages = multiple ? g.Select(o => o.MaxImages).FirstOrDefault(v => v is not null) : null,
                    RoleScope = g.Select(o => o.RoleScope).FirstOrDefault(r => r is not null)
                };
            })
            .OrderBy(s => s.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// One field per distinct <c>data-var</c>/<c>data-href</c> path (case-insensitive). A path repeated
    /// across elements is asked for ONCE; the renderer then fills every element carrying it. When
    /// occurrences differ in metadata, the one carrying <c>data-field-label</c> wins for the label and the
    /// one carrying <c>data-field-type</c> wins for the type (each defaulted independently when absent).
    /// </summary>
    private static List<TemplateFieldSlot> CollectFields(string html)
    {
        var occurrences = new List<FieldOccurrence>();
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

            if (string.IsNullOrWhiteSpace(key)) continue;

            // data-type is canonical; data-field-type is its legacy alias.
            var type = (Attr(tag.Value, DataTypeRegex()) ?? Attr(tag.Value, FieldTypeRegex()))?.ToLowerInvariant();

            occurrences.Add(new FieldOccurrence(
                key, isHref,
                Attr(tag.Value, FieldLabelRegex()),
                type,
                ParseOptions(Attr(tag.Value, DataOptionsRegex())),
                Slugify(Attr(tag.Value, RoleScopeRegex()))));
        }

        return occurrences
            .GroupBy(o => o.Key, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var first = g.First();
                var key = first.Key; // first-seen casing is canonical
                var label = g.Select(o => o.Label).FirstOrDefault(l => l is not null) ?? Prettify(key);
                var type = g.Select(o => o.Type).FirstOrDefault(t => t is not null)
                           ?? InferFieldType(key, first.IsHref);
                var options = g.Select(o => o.Options).FirstOrDefault(o => o is not null);

                if (type == "select" && (options is null || options.Count == 0))
                    throw new BusinessRuleException(
                        $"The field \"{key}\" is data-type=\"select\" but declares no data-options — list the allowed values, e.g. data-options=\"Formal,Casual,Black Tie\".",
                        "template_select_missing_options");

                return new TemplateFieldSlot
                {
                    Key = key,
                    Label = string.IsNullOrWhiteSpace(label) ? Prettify(key) : label,
                    Type = string.IsNullOrWhiteSpace(type) ? "text" : type,
                    Options = type == "select" ? options : null,
                    RoleScope = g.Select(o => o.RoleScope).FirstOrDefault(r => r is not null)
                };
            })
            .OrderBy(f => f.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// The theming surface: every <c>--ib-*</c> custom property the author declares, with the value it is
    /// defaulted to. The first declaration of a property wins (the <c>:root</c> defaults an author writes
    /// before any media-query/theme override).
    /// </summary>
    private static TemplateTheme CollectTheme(string html)
    {
        var theme = new TemplateTheme { Fonts = SplitList(MetaContent(html, "ib-fonts")) };
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in ThemeVarRegex().Matches(html))
        {
            var cssName = m.Groups[1].Value.Trim();
            var value = m.Groups[2].Value.Trim();
            if (cssName.Length == 0 || value.Length == 0 || !seen.Add(cssName)) continue;

            var key = ThemeKeyName(cssName);
            theme.Keys.Add(new TemplateThemeKey
            {
                Key = key,
                CssVar = $"--ib-{cssName}",
                Label = ThemeKeyLabel(key, cssName),
                Type = ColorValueRegex().IsMatch(value) ? "color"
                     : cssName.Contains("font", StringComparison.OrdinalIgnoreCase) ? "font"
                     : "text",
                Default = value
            });

            switch (key)
            {
                case "accentColor": theme.AccentColor = value; break;
                case "backgroundColor": theme.BackgroundColor = value; break;
                case "textColor": theme.TextColor = value; break;
            }
        }

        return theme;
    }

    private readonly record struct RoleOccurrence(string Slug, string Label);

    /// <summary>
    /// The roles the template supports: whatever it declares in <c>&lt;meta name="ib-roles"&gt;</c>, unioned
    /// with every role named by a <c>data-role-scope</c> — so scoping a single field is enough to make the
    /// role real without also having to remember the meta tag.
    /// </summary>
    private static List<RoleOccurrence> CollectRoles(
        string html, List<TemplateFieldSlot> fields, List<TemplateImageSlot> slots)
    {
        var declared = SplitList(MetaContent(html, "ib-roles")).Select(Slugify).OfType<string>();
        var scoped = fields.Select(f => f.RoleScope).Concat(slots.Select(s => s.RoleScope)).OfType<string>();

        return declared.Concat(scoped)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(slug => new RoleOccurrence(slug, Prettify(slug)))
            .ToList();
    }

    /// <summary>Reads an attribute off a captured opening tag; null when absent or blank.</summary>
    private static string? Attr(string tag, Regex regex)
    {
        var m = regex.Match(tag);
        if (!m.Success) return null;
        var value = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value).Trim();
        return value.Length == 0 ? null : value;
    }

    /// <summary>Reads a <c>&lt;meta name="…"&gt;</c>'s content out of the document; null when absent.</summary>
    private static string? MetaContent(string html, string name)
    {
        foreach (Match tag in IbMetaRegex().Matches(html))
            if (string.Equals($"ib-{tag.Groups[1].Value}", name, StringComparison.OrdinalIgnoreCase))
                return Attr(tag.Value, MetaContentRegex());
        return null;
    }

    /// <summary><c>data-options</c> accepts either a JSON array or a plain comma-separated list.</summary>
    private static List<string>? ParseOptions(string? raw)
    {
        if (raw is null) return null;
        var trimmed = raw.Trim();
        if (trimmed.StartsWith('['))
        {
            try
            {
                var parsed = JsonSerializer.Deserialize<List<string>>(trimmed);
                if (parsed is { Count: > 0 }) return parsed.Select(o => o.Trim()).Where(o => o.Length > 0).ToList();
            }
            catch (JsonException)
            {
                throw new BusinessRuleException(
                    "data-options looks like JSON but isn't a valid array of strings — use data-options='[\"A\",\"B\"]' or data-options=\"A,B\".",
                    "template_invalid_options");
            }
        }
        var list = SplitList(trimmed);
        return list.Count == 0 ? null : list;
    }

    private static List<string> SplitList(string? raw) =>
        raw is null
            ? new List<string>()
            : raw.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();

    private static bool? ParseBool(string? raw) =>
        raw is null ? null : raw is "" or "true" or "True" or "TRUE" or "1" or "yes";

    private static int? ParseCount(string? raw) =>
        int.TryParse(raw, out var n) && n >= 0 ? n : null;

    /// <summary>Normalizes an authored role name ("Bride & Groom") to a slug ("bride-groom").</summary>
    private static string? Slugify(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var chars = raw.Trim().ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        return slug.Length == 0 ? null : slug;
    }

    /// <summary>
    /// Maps a CSS custom-property name to its manifest key: the three required ones get their documented
    /// names (<c>--ib-accent</c> → <c>accentColor</c>), everything else is camel-cased as authored.
    /// </summary>
    private static string ThemeKeyName(string cssName) => cssName.ToLowerInvariant() switch
    {
        "accent" => "accentColor",
        "bg" => "backgroundColor",
        "text" => "textColor",
        _ => CamelCase(cssName)
    };

    /// <summary>The three documented keys read better spelled out than prettified ("Bg" → "Background").</summary>
    private static string ThemeKeyLabel(string key, string cssName) => key switch
    {
        "accentColor" => "Accent colour",
        "backgroundColor" => "Background",
        "textColor" => "Text colour",
        _ => Prettify(cssName)
    };

    private static string CamelCase(string kebab)
    {
        var parts = kebab.Split('-', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return kebab;
        return parts[0].ToLowerInvariant() + string.Concat(parts.Skip(1)
            .Select(p => char.ToUpperInvariant(p[0]) + p[1..].ToLowerInvariant()));
    }

    private static bool Same(string? a, string? b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

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
        var set = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match m in regex.Matches(html))
        {
            var value = m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value;
            if (!string.IsNullOrWhiteSpace(value)) set.Add(value.Trim());
        }
        return set;
    }

    /// <summary>
    /// Inlines the trusted injector. The author's own HTML is already script-free — the scan rejects
    /// any <c>&lt;script&gt;</c> before we get here — so the served document's ONLY script is ours.
    /// </summary>
    private static string WireInjector(string html)
    {
        var injection = TemplateInjector.InviteDataScript + "\n<script>" + TemplateInjector.Js + "</script>";
        return InsertBefore(html, "</body>", injection) ?? html + "\n" + injection;
    }

    private static string? InsertBefore(string html, string marker, string insertion)
    {
        var idx = html.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        return idx < 0 ? null : html[..idx] + insertion + "\n" + html[idx..];
    }

    private static byte[] Bytes(string s) => Encoding.UTF8.GetBytes(s);
}
