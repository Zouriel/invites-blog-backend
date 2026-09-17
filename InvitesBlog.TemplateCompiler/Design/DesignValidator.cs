using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace InvitesBlog.TemplateCompiler.Design;

public sealed record DesignIssue(
    [property: JsonPropertyName("severity")] string Severity,
    [property: JsonPropertyName("code")] string Code,
    [property: JsonPropertyName("message")] string Message,
    [property: JsonPropertyName("elementId")] string? ElementId = null)
{
    public const string Error = "error";
    public const string Warning = "warning";
}

/// <summary>
/// The designer's Check. Errors block publishing; warnings don't. Runs in the editor on every preview
/// and again on the server's own copy of the scene at publish, so what the editor said is what the
/// server enforces.
/// </summary>
public static partial class DesignValidator
{
    public const int SoftBytes = 300 * 1024;
    public const int HardBytes = 800 * 1024;

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex IdRegex();

    [GeneratedRegex("^event\\.[a-z][A-Za-z0-9]{0,39}$")]
    private static partial Regex CustomPathRegex();

    public static IReadOnlyList<DesignIssue> Validate(DesignScene scene)
    {
        var issues = new List<DesignIssue>();
        void Error(string code, string message, string? id = null) => issues.Add(new(DesignIssue.Error, code, message, id));
        void Warn(string code, string message, string? id = null) => issues.Add(new(DesignIssue.Warning, code, message, id));

        if (scene.Schema != DesignScene.CurrentSchema)
            Error("schema", "This design was made with a different version of the editor.");

        // ----- Page -----
        var lowest = scene.Elements.Where(e => double.IsFinite(e.Y) && double.IsFinite(e.H)).Select(e => e.Y + e.H).DefaultIfEmpty(0).Max();
        if (lowest > DesignCatalog.MaxPageHeight)
            Error("page_too_long", $"The page runs longer than {DesignCatalog.MaxPageHeight / DesignCanvas.ReferenceViewport:0} screens — bring things closer together.");

        // ----- Theme -----
        var themeKeys = ThemeKeys(scene);
        if (scene.Theme.Count > DesignCatalog.MaxThemeEntries)
            Error("too_many_theme", $"Keep the theme to {DesignCatalog.MaxThemeEntries} colours and fonts.");
        foreach (var required in DesignCatalog.RequiredThemeKeys)
            if (!scene.Theme.Any(t => t.Key == required))
                Error("theme_required", $"The theme needs its “{required}” colour.");
        foreach (var group in scene.Theme.GroupBy(t => t.Key).Where(g => g.Count() > 1))
            Error("theme_duplicate", $"The theme has “{group.Key}” twice.");
        foreach (var t in scene.Theme)
        {
            if (!DesignCss.IsThemeKey(t.Key))
                Error("theme_key", $"“{t.Key}” isn't a usable theme name — lowercase letters, digits and dashes.");
            else if (t.IsFont && DesignCatalog.FindFont(t.Value) is null)
                Error("theme_font", $"“{t.Label}” uses a font we don't host.");
            else if (!t.IsFont && !DesignCss.IsHex(t.Value))
                Error("theme_color", $"“{t.Label}” isn't a valid colour.");
            if (DesignCatalog.RequiredThemeKeys.Contains(t.Key) && t.IsFont)
                Error("theme_color", $"“{t.Key}” has to be a colour.");
        }
        foreach (var font in scene.Fonts)
            if (DesignCatalog.FindFont(font) is null) Error("font_unknown", $"“{font}” isn't a font we host.");

        // ----- Roles and fields -----
        if (scene.Roles.Count > DesignCatalog.MaxRoles) Error("too_many_roles", $"A template can declare at most {DesignCatalog.MaxRoles} roles.");
        var roleSlugs = scene.Roles.Select(r => DesignCompiler.Slug(r)).ToHashSet(StringComparer.Ordinal);
        if (scene.Roles.Any(r => DesignCompiler.Slug(r).Length == 0)) Error("role_name", "Every role needs a name.");
        if (roleSlugs.Count != scene.Roles.Count) Error("role_duplicate", "Two roles have the same name.");

        if (scene.Fields.Count > DesignCatalog.MaxCustomFields) Error("too_many_fields", $"Keep custom fields to {DesignCatalog.MaxCustomFields}.");
        var fields = new Dictionary<string, DesignCustomField>(StringComparer.Ordinal);
        foreach (var f in scene.Fields)
        {
            if (!CustomPathRegex().IsMatch(f.Path ?? string.Empty) || DesignCatalog.FindVariable(f.Path!) is not null)
            {
                Error("field_path", $"“{f.Label}” needs a key of its own, like event.giftNote.");
                continue;
            }
            if (!fields.TryAdd(f.Path!, f)) Error("field_duplicate", $"The field {f.Path} is defined twice.");
            if (string.IsNullOrWhiteSpace(f.Label)) Error("field_label", $"The field {f.Path} needs a label the inviter will see.");
            if (!DesignCatalog.FieldTypes.Contains(f.Type)) Error("field_type", $"“{f.Label}” has an unknown input type.");
            if (f.Type == "select" && (f.Options is null || f.Options.Count(o => !string.IsNullOrWhiteSpace(o)) == 0))
                Error("select_options", $"“{f.Label}” is a dropdown with no options.");
            if (f.RoleScope is not null && !roleSlugs.Contains(DesignCompiler.Slug(f.RoleScope)))
                Error("field_role", $"“{f.Label}” belongs to a role the template doesn't declare.");
        }

        // ----- Assets -----
        foreach (var (id, asset) in scene.Assets)
        {
            if (!IdRegex().IsMatch(id)) { Error("asset_id", "An imported file has an invalid id."); continue; }
            var bytes = Encoding.UTF8.GetByteCount(asset.Data ?? string.Empty);
            switch (asset.Kind)
            {
                case "svg":
                    if (bytes > DesignCatalog.MaxSvgBytes) Error("svg_too_large", $"“{asset.Name ?? id}” is over {DesignCatalog.MaxSvgBytes / 1024}KB.");
                    try { SvgSanitizer.Sanitize(asset.Data ?? string.Empty); }
                    catch (SvgRejectedException e) { Error("svg_invalid", $"“{asset.Name ?? id}”: {e.Message}"); }
                    break;
                case "image":
                    if (bytes > DesignCatalog.MaxImageBytes * 4 / 3 + 64) Error("image_too_large", $"“{asset.Name ?? id}” is over {DesignCatalog.MaxImageBytes / 1024}KB.");
                    if (DesignCompiler.ImageDataUri(asset.Data) is null) Error("image_invalid", $"“{asset.Name ?? id}” isn't a usable picture.");
                    break;
                default:
                    Error("asset_kind", $"“{asset.Name ?? id}” is a kind of file the designer doesn't use.");
                    break;
            }
        }

        // ----- Elements -----
        var all = scene.Walk().ToList();
        if (all.Count > DesignCatalog.MaxElements) Error("too_many_elements", $"A design can have at most {DesignCatalog.MaxElements} elements.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var range = scene.ScrollRange();
        var page = scene.PageHeight();
        var animated = 0;
        var rsvp = 0;
        var dress = 0;
        var pathsOutsideBlocks = new HashSet<string>(StringComparer.Ordinal);
        var pathsInsideBlocks = new List<(string Path, string ElementId)>();

        foreach (var (el, parent, depth) in all)
        {
            var label = string.IsNullOrWhiteSpace(el.Name) ? el.Type : el.Name;
            if (!IdRegex().IsMatch(el.Id ?? string.Empty)) { Error("element_id", $"“{label}” has an invalid id."); continue; }
            if (!ids.Add(el.Id!)) Error("element_duplicate", $"Two elements share the id {el.Id}.", el.Id);
            if (depth >= DesignCatalog.MaxDepth) Error("too_deep", $"“{label}” is nested too deeply — groups can go {DesignCatalog.MaxDepth} levels.", el.Id);
            if (!DesignCatalog.ElementTypes.Contains(el.Type)) { Error("element_type", $"“{label}” is an unknown kind of element.", el.Id); continue; }

            double[] numbers = [el.X, el.Y, el.W, el.H, el.Rotate, el.Scale, el.Opacity];
            if (numbers.Any(n => !double.IsFinite(n))) Error("element_number", $"“{label}” has a position or size that isn't a number.", el.Id);
            if (el.W <= 0 || el.H <= 0) Error("element_size", $"“{label}” has no size.", el.Id);

            if (parent is null && double.IsFinite(el.Y) && (el.Y >= page || el.Y + el.H <= 0 || el.X >= DesignCanvas.Width || el.X + el.W <= 0))
                Warn("off_page", $"“{label}” is entirely off the page, so nobody will see it.", el.Id);

            if (el.Track is { } track)
            {
                if (!double.IsFinite(track.Start) || !double.IsFinite(track.End) || track.End <= track.Start)
                    Error("track", $"“{label}” has a scroll track that ends before it starts.", el.Id);
                else if (track.Start > range + 1)
                    Warn("track_unreachable", $"“{label}” starts moving after the page has finished scrolling.", el.Id);
            }
            if (el.Keyframes.Count > DesignCatalog.MaxKeyframes)
                Error("too_many_keyframes", $"“{label}” has more than {DesignCatalog.MaxKeyframes} keyframes.", el.Id);
            foreach (var k in el.Keyframes)
            {
                double?[] values = [k.T, k.X, k.Y, k.Rotate, k.Scale, k.Opacity];
                if (values.Any(v => v is { } d && !double.IsFinite(d)) || k.T is < 0 or > 1)
                    Error("keyframe", $"“{label}” has a keyframe outside its track.", el.Id);
                if (k.Easing is not null && DesignCss.Easing(k.Easing) is null)
                    Error("easing", $"“{label}” uses an easing we don't recognise.", el.Id);
            }
            if (el.Keyframes.Count > 0 || el.Pinned) animated++;

            if (el.Block is not null && DesignCompiler.Slug(el.Block).Length == 0)
                Error("block", $"“{label}” has a section-visibility name that's empty.", el.Id);
            if (el.RoleScope is not null && !roleSlugs.Contains(DesignCompiler.Slug(el.RoleScope)))
                Error("element_role", $"“{label}” belongs to a role the template doesn't declare.", el.Id);

            var inBlock = el.Block is not null || AncestorHasBlock(scene, el);
            void NotePath(string path)
            {
                if (inBlock) pathsInsideBlocks.Add((path, el.Id!));
                else pathsOutsideBlocks.Add(path);
            }

            switch (el.Type)
            {
                case "text":
                    var runs = el.Text?.Runs ?? [];
                    if (runs.Sum(r => r.Text?.Length ?? 0) > DesignCatalog.MaxTextLength)
                        Error("text_length", $"“{label}” has more text than a single element can hold.", el.Id);
                    if (runs.Count == 0 || runs.All(r => string.IsNullOrWhiteSpace(r.Text) && string.IsNullOrWhiteSpace(r.Var)))
                        Warn("text_empty", $"“{label}” has no text.", el.Id);
                    foreach (var run in runs.Where(r => !string.IsNullOrWhiteSpace(r.Var)))
                    {
                        var v = DesignCatalog.FindVariable(run.Var!);
                        if ((v is null && !fields.ContainsKey(run.Var!)) || v is { Kind: not "text" })
                            Error("variable_unknown", $"“{label}” uses {run.Var}, which isn't a field this template can fill.", el.Id);
                        else NotePath(run.Var!);
                    }
                    CheckTypography(el.Text?.Style, label, el.Id!, themeKeys, Error, Warn);
                    break;
                case "shape":
                    var shape = el.Shape ?? new DesignShape();
                    if (shape.Kind is not ("rect" or "ellipse" or "line" or "polygon" or "path")) Error("shape_kind", $"“{label}” is an unknown shape.", el.Id);
                    if (shape.Kind == "path")
                    {
                        var contours = shape.Path?.Contours ?? [];
                        var total = contours.Sum(c => c.Points.Count);
                        if (contours.Count == 0 || total < 2)
                            Error("shape_path", $"“{label}” is a drawn shape with nothing drawn.", el.Id);
                        else if (contours.Count > DesignCatalog.MaxPathContours || total > DesignCatalog.MaxPathPoints)
                            Error("shape_path", $"“{label}” has too many points — merge or simplify it.", el.Id);
                        else if (contours.SelectMany(c => c.Points).Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y)))
                            Error("shape_path", $"“{label}” has a point that isn't a number.", el.Id);
                    }
                    CheckColor(shape.Fill, label, el.Id!, themeKeys, Error, Warn);
                    CheckColor(shape.Stroke, label, el.Id!, themeKeys, Error, Warn);
                    break;
                case "svg":
                    if (el.Svg is null || !scene.Assets.TryGetValue(el.Svg.Asset, out var svgAsset) || svgAsset.Kind != "svg")
                        Error("asset_missing", $"“{label}” points at an SVG that isn't in the design.", el.Id);
                    else foreach (var reference in el.Svg.Fills.Values) CheckColor(reference, label, el.Id!, themeKeys, Error, Warn);
                    break;
                case "image":
                    if (el.Image is null || !scene.Assets.TryGetValue(el.Image.Asset, out var imageAsset) || imageAsset.Kind != "image")
                        Error("asset_missing", $"“{label}” points at a picture that isn't in the design.", el.Id);
                    break;
                case "slot":
                    var slot = el.Slot ?? new DesignSlot();
                    var slotVar = DesignCatalog.FindVariable(slot.Path);
                    if (!(slotVar is { Kind: "image" } || CustomPathRegex().IsMatch(slot.Path) && DesignCatalog.FindVariable(slot.Path) is null))
                        Error("slot_path", $"“{label}” needs an image key like event.coverImage.", el.Id);
                    if (string.IsNullOrWhiteSpace(slot.Label)) Warn("slot_label", $"“{label}” has no label, so the inviter won't know what to upload.", el.Id);
                    if (slot.Multiple && slot.Min is { } min && slot.Max is { } max && min > max)
                        Error("slot_bounds", $"“{label}” asks for more photos at least than at most.", el.Id);
                    NotePath(slot.Path);
                    break;
                case "rsvp":
                    rsvp++;
                    CheckButton(el, label, themeKeys, Error, Warn);
                    NotePath("rsvp.link");
                    break;
                case "link":
                    var path = el.Button?.Path ?? string.Empty;
                    if (!DesignCatalog.LinkPaths.Contains(path) && !(fields.TryGetValue(path, out var urlField) && urlField.Type == "url"))
                        Error("link_path", $"“{label}” needs to open the camera, the photos, the map, or a link field.", el.Id);
                    else NotePath(path);
                    CheckButton(el, label, themeKeys, Error, Warn);
                    break;
                case "dress":
                    dress++;
                    CheckTypography(el.Dress?.Style, label, el.Id!, themeKeys, Error, Warn);
                    break;
                case "group":
                    if (el.Children is null || el.Children.Count == 0) Warn("group_empty", $"“{label}” is an empty group.", el.Id);
                    break;
            }
        }

        if (rsvp == 0)
            Error("rsvp_required", "Add an RSVP button — every invitation has to let guests reply.");
        if (dress == 0)
            Warn("dress_missing", "There's no spot for dress colours, so the platform will add its own section at the end.");
        if (animated > DesignCatalog.AnimatedElementWarning)
            Warn("many_animations", $"{animated} elements move. Past about {DesignCatalog.AnimatedElementWarning} a phone starts to stutter.");
        foreach (var (path, id) in pathsInsideBlocks.Where(p => !pathsOutsideBlocks.Contains(p.Path)).DistinctBy(p => p.Path))
            Warn("block_only", $"{path} only appears inside role-specific content — guests outside those roles won't see it.", id);

        return issues;
    }

    /// <summary>The size checks, which need the compiled document.</summary>
    public static IReadOnlyList<DesignIssue> CheckCompiled(string html)
    {
        var bytes = Encoding.UTF8.GetByteCount(html);
        if (bytes > HardBytes)
            return [new(DesignIssue.Error, "too_large", $"The template is {bytes / 1024}KB — the limit is {HardBytes / 1024}KB. Use smaller pictures or fewer imported files.")];
        if (bytes > SoftBytes)
            return [new(DesignIssue.Warning, "large", $"The template is {bytes / 1024}KB. Under {SoftBytes / 1024}KB opens noticeably faster on a phone.")];
        return [];
    }

    private static HashSet<string> ThemeKeys(DesignScene scene) =>
        scene.Theme.Where(t => DesignCss.IsThemeKey(t.Key)).Select(t => t.Key).ToHashSet(StringComparer.Ordinal);

    private static bool AncestorHasBlock(DesignScene scene, DesignElement target)
    {
        bool Search(IEnumerable<DesignElement> list, bool blocked)
        {
            foreach (var el in list)
            {
                if (ReferenceEquals(el, target)) return blocked;
                if (el.Children is not null && Search(el.Children, blocked || el.Block is not null)) return true;
            }
            return false;
        }
        return Search(scene.Elements, false);
    }

    private static void CheckButton(DesignElement el, string label, HashSet<string> themeKeys,
        Action<string, string, string?> error, Action<string, string, string?> warn)
    {
        var button = el.Button ?? new DesignButton();
        CheckColor(button.Fill, label, el.Id, themeKeys, error, warn);
        CheckColor(button.Stroke, label, el.Id, themeKeys, error, warn);
        CheckTypography(button.Style, label, el.Id, themeKeys, error, warn);
    }

    private static void CheckTypography(DesignTypography? style, string label, string id, HashSet<string> themeKeys,
        Action<string, string, string?> error, Action<string, string, string?> warn)
    {
        if (style is null) return;
        if (style.Font is not null && DesignCss.Font(style.Font, themeKeys) is null)
            error("font", $"“{label}” uses a font that isn't available.", id);
        CheckColor(style.Color, label, id, themeKeys, error, warn);
        if (!double.IsFinite(style.Size) || style.Size <= 0) error("font_size", $"“{label}” has no text size.", id);
    }

    private static void CheckColor(string? reference, string label, string id, HashSet<string> themeKeys,
        Action<string, string, string?> error, Action<string, string, string?> warn)
    {
        if (string.IsNullOrWhiteSpace(reference) || reference is "none" or "transparent") return;
        if (DesignCss.Color(reference, themeKeys) is null)
        {
            error("color", $"“{label}” uses a colour that isn't valid.", id);
            return;
        }
        if (!reference.StartsWith("theme:", StringComparison.Ordinal))
            warn("hardcoded_color", $"“{label}” uses a fixed colour, so the inviter can't change it with the theme.", id);
    }
}
