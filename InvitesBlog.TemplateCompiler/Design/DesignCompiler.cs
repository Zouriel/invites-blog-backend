using System.Net;
using System.Text;

namespace InvitesBlog.TemplateCompiler.Design;

public sealed class DesignCompileOptions
{
    /// <summary>Where published fonts are served from, ending in a slash (e.g. <c>/assets/fonts/</c>).</summary>
    public string FontBaseUrl { get; init; } = "/assets/fonts/";
    public string Title { get; init; } = "Invitation";
    /// <summary>Adds the editor bridge (scroll sync). Never set for a published package.</summary>
    public bool EditorPreview { get; init; }
    /// <summary>Editor preview only: where the page opens, in scroll units.</summary>
    public double InitialScroll { get; init; }
    /// <summary>Editor preview only: elements muted in the timeline are left out.</summary>
    public IReadOnlySet<string>? HiddenElementIds { get; init; }
}

/// <summary>
/// Compiles a <see cref="DesignScene"/> into the single self-contained template document the platform
/// already accepts — the same contract a hand-written template follows (<c>data-var</c>,
/// <c>data-href</c>, <c>data-src</c>, <c>data-optional</c>, <c>data-block</c>, <c>--ib-*</c>), so the
/// packager, manifest, builder and render never learn that a designer made it.
///
/// <para><b>Deterministic.</b> The same scene always produces byte-identical output: element classes
/// are positional, never taken from ids in the scene, and every number goes through
/// <see cref="DesignCss.Num"/>.</para>
///
/// <para><b>Motion.</b> Each element is two boxes. The outer <c>.e</c> is positioned and, when the element is
/// pinned, carries a linear translate that cancels the scroll for the length of its track. The inner
/// <c>.a</c> plays the keyframes. Keeping them apart is what lets a pinned element also ease: an eased
/// pin would drift against the page.</para>
///
/// <para><b>Old browsers.</b> Where <c>animation-timeline</c> is unsupported, a tiny script pauses the
/// very same CSS animations and sets their <c>currentTime</c> from the scroll position. Same keyframes,
/// so the fallback cannot look different from the native path. It creates no elements.</para>
/// </summary>
public static class DesignCompiler
{
    public static string Compile(DesignScene scene, DesignCompileOptions? options = null)
    {
        options ??= new DesignCompileOptions();
        var ctx = new Context(scene, options);

        var body = new StringBuilder();
        var css = new StringBuilder();

        EmitRootCss(ctx, css);
        EmitSymbols(ctx, body);

        body.Append("<main class=\"ib-page\">");
        EmitSections(ctx, body, css);
        foreach (var element in scene.Elements)
            EmitElement(ctx, element, body, css);
        body.Append("</main><div class=\"ib-tail\"></div>");

        var html = new StringBuilder();
        html.Append("<!doctype html>\n<html lang=\"en\">\n<head>\n");
        html.Append("<meta charset=\"utf-8\">\n");
        html.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1, viewport-fit=cover\">\n");
        html.Append("<meta name=\"generator\" content=\"invites.blog designer\">\n");
        var roles = scene.Roles.Where(r => !string.IsNullOrWhiteSpace(r)).Select(r => r.Trim()).Distinct().ToList();
        if (roles.Count > 0)
            html.Append("<meta name=\"ib-roles\" content=\"").Append(Attr(string.Join(", ", roles))).Append("\">\n");
        if (ctx.OfferedFonts.Count > 0)
            html.Append("<meta name=\"ib-fonts\" content=\"")
                .Append(Attr(string.Join(", ", ctx.OfferedFonts.Select(f => f.Name)))).Append("\">\n");
        html.Append("<title>").Append(WebUtility.HtmlEncode(options.Title)).Append("</title>\n");
        html.Append("<style>").Append(css).Append("</style>\n");
        html.Append("<script>").Append(DetectScript).Append("</script>\n");
        html.Append("</head>\n<body>");
        html.Append(body);
        html.Append("\n<script>").Append(FallbackScript).Append("</script>");
        if (options.EditorPreview)
        {
            html.Append("\n<script>")
                .Append(EditorScript.Replace("__SCROLL__", DesignCss.Num(Math.Max(0, options.InitialScroll))))
                .Append("</script>");
        }
        html.Append("\n</body>\n</html>\n");
        return html.ToString();
    }

    // ------------------------------------------------------------------------------------------------

    private sealed class Context
    {
        public Context(DesignScene scene, DesignCompileOptions options)
        {
            Scene = scene;
            Options = options;
            ThemeKeys = scene.Theme
                .Where(t => DesignCss.IsThemeKey(t.Key))
                .Select(t => t.Key)
                .ToHashSet(StringComparer.Ordinal);
            Fields = scene.Fields
                .Where(f => !string.IsNullOrWhiteSpace(f.Path))
                .GroupBy(f => f.Path, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

            // Fonts: whatever the author offers the inviter, plus anything actually used, so switching
            // a theme font in the builder never lands on a face the page didn't load.
            var ids = new List<string>();
            foreach (var id in scene.Fonts) if (DesignCatalog.FindFont(id) is not null) ids.Add(id);
            foreach (var t in scene.Theme) if (t.IsFont && DesignCatalog.FindFont(t.Value) is not null) ids.Add(t.Value);
            OfferedFonts = scene.Fonts.Select(DesignCatalog.FindFont).OfType<DesignCatalog.Font>().Distinct().ToList();
            foreach (var t in scene.Theme)
                if (t.IsFont && DesignCatalog.FindFont(t.Value) is { } f && !OfferedFonts.Contains(f)) OfferedFonts.Add(f);
            foreach (var (el, _, _) in scene.Walk())
            {
                var font = el.Text?.Style.Font ?? el.Button?.Style.Font ?? el.Dress?.Style.Font;
                if (font is not null && !font.StartsWith("theme:", StringComparison.Ordinal) && DesignCatalog.FindFont(font) is not null)
                    ids.Add(font);
            }
            LoadedFonts = ids.Distinct().Select(id => DesignCatalog.FindFont(id)!).ToList();

            var symbolIndex = 0;
            foreach (var (el, _, _) in scene.Walk())
            {
                if (el.Type != "svg" || el.Svg is null) continue;
                if (!scene.Assets.TryGetValue(el.Svg.Asset, out var asset) || asset.Kind != "svg") continue;
                if (!SvgSymbols.ContainsKey(el.Svg.Asset)) SvgSymbols[el.Svg.Asset] = symbolIndex++;
            }
            var imageIndex = 0;
            foreach (var (el, _, _) in scene.Walk())
            {
                if (el.Type != "image" || el.Image is null) continue;
                if (!scene.Assets.TryGetValue(el.Image.Asset, out var asset) || asset.Kind != "image") continue;
                if (!ImageClasses.ContainsKey(el.Image.Asset)) ImageClasses[el.Image.Asset] = imageIndex++;
            }
        }

        public DesignScene Scene { get; }
        public DesignCompileOptions Options { get; }
        public HashSet<string> ThemeKeys { get; }
        public Dictionary<string, DesignCustomField> Fields { get; }
        public List<DesignCatalog.Font> OfferedFonts { get; }
        public List<DesignCatalog.Font> LoadedFonts { get; }
        public Dictionary<string, int> SvgSymbols { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, int> ImageClasses { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, IReadOnlyList<string>> SvgColors { get; } = new(StringComparer.Ordinal);
        public HashSet<string> EmittedImages { get; } = new(StringComparer.Ordinal);
        public int Next;
    }

    // ----- Stylesheet head ---------------------------------------------------------------------------

    private static void EmitRootCss(Context ctx, StringBuilder css)
    {
        // Theme variables FIRST: the manifest scanner takes the first declaration of each key as its
        // default, and the builder's theming step is built from exactly that.
        css.Append(":root{");
        foreach (var entry in ctx.Scene.Theme)
        {
            if (!DesignCss.IsThemeKey(entry.Key)) continue;
            string? value = entry.IsFont
                ? DesignCatalog.FindFont(entry.Value)?.Stack
                : DesignCss.IsHex(entry.Value) ? entry.Value.ToLowerInvariant() : null;
            if (value is null) continue;
            css.Append("--ib-").Append(entry.Key).Append(':').Append(value).Append(';');
        }
        css.Append('}');

        css.Append(":root{--u:calc(min(100vw, 480px) / ").Append(DesignCss.Num(DesignCanvas.Width)).Append(")}");

        foreach (var font in ctx.LoadedFonts)
            foreach (var weight in font.Weights)
                css.Append("@font-face{font-family:\"").Append(font.Name).Append("\";font-style:normal;font-weight:")
                    .Append(weight).Append(";font-display:swap;src:url(\"")
                    .Append(Attr(ctx.Options.FontBaseUrl)).Append(font.Id).Append('-').Append(weight)
                    .Append(".woff2\") format(\"woff2\")}");

        var bg = ctx.ThemeKeys.Contains("bg") ? "var(--ib-bg)" : "#ffffff";
        var text = ctx.ThemeKeys.Contains("text") ? "var(--ib-text)" : "#111111";
        css.Append("html{background:").Append(bg).Append(";overflow-x:hidden}");
        css.Append("body{margin:0;color:").Append(text)
            .Append(";-webkit-text-size-adjust:100%;text-size-adjust:100%;-webkit-font-smoothing:antialiased}");
        css.Append(".ib-page{position:relative;display:block;margin:0 auto;overflow:hidden;width:")
            .Append(DesignCss.U(DesignCanvas.Width)).Append(";height:").Append(DesignCss.U(ctx.Scene.Canvas.PageHeight)).Append('}');
        // Keeps the authored scroll range reachable on a phone taller (in canvas units) than the
        // reference one. lvh, not vh/dvh: it doesn't move while the URL bar does.
        css.Append(".ib-tail{height:max(0px, calc(100lvh - ")
            .Append(DesignCss.Num(DesignCanvas.ReferenceViewport)).Append(" * var(--u)))}");
        css.Append(".ib-sec{position:absolute;left:0;width:100%}");
        css.Append(".e{position:absolute;margin:0}");
        css.Append(".a{position:relative;width:100%;height:100%;transform-origin:50% 50%}");
        css.Append(".t{margin:0;white-space:pre-wrap;overflow-wrap:break-word}");
        css.Append(".b{display:flex;align-items:center;justify-content:center;width:100%;height:100%;text-decoration:none;box-sizing:border-box;text-align:center}");
        css.Append(".s{display:block;width:100%;height:100%;overflow:visible}");
        css.Append(".ib-fb .e,.ib-fb .a{animation-play-state:paused!important}");
        if (ctx.Options.EditorPreview)
            css.Append("html{scrollbar-width:none}html::-webkit-scrollbar{display:none}");
    }

    private static void EmitSymbols(Context ctx, StringBuilder body)
    {
        if (ctx.SvgSymbols.Count == 0) return;
        body.Append("<svg width=\"0\" height=\"0\" style=\"position:absolute\" aria-hidden=\"true\">");
        foreach (var (assetId, index) in ctx.SvgSymbols.OrderBy(p => p.Value))
        {
            var asset = ctx.Scene.Assets[assetId];
            // Already sanitised at import AND re-sanitised here: the scene is client-supplied JSON, so
            // whatever arrives in an asset is treated as untrusted every single time it is compiled.
            SanitizedSvg clean;
            try { clean = SvgSanitizer.Sanitize(asset.Data, $"s{index}-"); }
            catch (SvgRejectedException) { continue; }
            ctx.SvgColors[assetId] = clean.Colors;
            body.Append("<symbol id=\"s").Append(index).Append("\" viewBox=\"").Append(clean.ViewBox).Append("\">")
                .Append(clean.InnerMarkup).Append("</symbol>");
        }
        body.Append("</svg>");
    }

    private static void EmitSections(Context ctx, StringBuilder body, StringBuilder css)
    {
        double top = 0;
        foreach (var section in ctx.Scene.Canvas.Sections)
        {
            var color = DesignCss.Color(section.Background, ctx.ThemeKeys);
            if (color is not null)
            {
                var n = ctx.Next++;
                body.Append("<div class=\"ib-sec e").Append(n).Append("\"></div>");
                css.Append(".e").Append(n).Append("{top:").Append(DesignCss.U(top)).Append(";height:")
                    .Append(DesignCss.U(section.Height)).Append(";background:").Append(color).Append('}');
            }
            top += section.Height;
        }
    }

    // ----- Elements ----------------------------------------------------------------------------------

    private static void EmitElement(Context ctx, DesignElement el, StringBuilder body, StringBuilder css)
    {
        if (ctx.Options.HiddenElementIds?.Contains(el.Id) == true) return;
        if (!DesignCatalog.ElementTypes.Contains(el.Type)) return;

        var n = ctx.Next++;
        var cls = $".e{n}";
        var inner = new StringBuilder();
        var boundAnything = false;

        switch (el.Type)
        {
            case "text":
                boundAnything = EmitText(ctx, el, inner, css, cls);
                break;
            case "shape":
                EmitShape(ctx, el, inner);
                break;
            case "svg":
                EmitSvg(ctx, el, inner, css, cls);
                break;
            case "image":
                EmitImage(ctx, el, inner, css, cls);
                break;
            case "slot":
                EmitSlot(ctx, el, inner, css, cls);
                boundAnything = true;
                break;
            case "rsvp":
            case "link":
                boundAnything = EmitButton(ctx, el, inner, css, cls);
                break;
            case "dress":
                EmitDress(ctx, el, inner, css, cls);
                break;
            case "group":
                foreach (var child in el.Children ?? [])
                    EmitElement(ctx, child, inner, css);
                break;
        }

        var track = TrackOf(ctx.Scene, el);
        var animated = el.Keyframes.Count > 0;

        body.Append("<div class=\"e e").Append(n).Append('"');
        if (animated || el.Pinned)
            body.Append(" data-ts=\"").Append(DesignCss.Num(track.Start)).Append("\" data-te=\"")
                .Append(DesignCss.Num(track.End)).Append('"');
        if (!string.IsNullOrWhiteSpace(el.Block) && Slug(el.Block) is { Length: > 0 } block)
            body.Append(" data-block=\"").Append(block).Append('"');
        // Always optional when bound: a missing value blanks its element, and a blank label with
        // nothing in it is worse than no element at all. Hand-written templates can forget this; a
        // designed one can't.
        if (boundAnything) body.Append(" data-optional");
        body.Append("><div class=\"a\">").Append(inner).Append("</div></div>");

        // Box. Pinning and coming to the front both animate the box itself (the motion is on .a inside
        // it): a transform on .a can't lift the element above its siblings, only z-index on the box can.
        var frames = animated && track.End > track.Start ? ResolveFrames(el) : [];
        var lifts = frames.Any(f => f.Lift > 0);
        css.Append(cls).Append("{left:").Append(DesignCss.U(el.X)).Append(";top:").Append(DesignCss.U(el.Y))
            .Append(";width:").Append(DesignCss.U(Math.Max(1, el.W))).Append(";height:")
            .Append(DesignCss.U(Math.Max(1, el.H))).Append(';');
        var boxAnimations = new List<string>();
        if (el.Pinned && track.End > track.Start) boxAnimations.Add($"p{n}");
        if (lifts) boxAnimations.Add($"z{n}");
        if (boxAnimations.Count > 0)
        {
            var range = DesignCss.U(track.Start) + " " + DesignCss.U(track.End);
            css.Append("animation:").Append(string.Join(',', boxAnimations.Select(a => a + " 1s linear both")))
                .Append(";animation-timeline:").Append(string.Join(',', boxAnimations.Select(_ => "scroll(root)")))
                .Append(";animation-range:").Append(string.Join(',', boxAnimations.Select(_ => range))).Append(';');
        }
        css.Append('}');
        if (lifts)
        {
            css.Append("@keyframes z").Append(n).Append('{');
            foreach (var frame in frames)
                css.Append(DesignCss.Num(frame.T * 100)).Append("%{z-index:").Append(frame.Lift).Append('}');
            css.Append('}');
        }
        if (el.Pinned && track.End > track.Start)
        {
            css.Append("@keyframes p").Append(n).Append("{from{transform:translateY(0px)}to{transform:translateY(")
                .Append(DesignCss.U(track.End - track.Start)).Append(")}}");
        }

        // Resting state + motion
        var rest = new StringBuilder();
        var baseTransform = Transform(0, 0, el.Rotate, el.Scale);
        if (baseTransform != "none") rest.Append("transform:").Append(baseTransform).Append(';');
        var opacity = DesignCss.Clamp(el.Opacity, 0, 1);
        if (opacity < 1) rest.Append("opacity:").Append(DesignCss.Num(opacity)).Append(';');

        if (animated && track.End > track.Start)
        {
            rest.Append("animation:k").Append(n).Append(" 1s linear both;animation-timeline:scroll(root);animation-range:")
                .Append(DesignCss.U(track.Start)).Append(' ').Append(DesignCss.U(track.End)).Append(';');
            css.Append("@keyframes k").Append(n).Append('{');
            foreach (var frame in ResolveFrames(el))
            {
                css.Append(DesignCss.Num(frame.T * 100)).Append("%{transform:")
                    .Append(Transform(frame.X - el.X, frame.Y - el.Y, frame.Rotate, frame.Scale))
                    .Append(";opacity:").Append(DesignCss.Num(frame.Opacity)).Append(';');
                if (frame.Easing is { } easing && easing != "linear")
                    css.Append("animation-timing-function:").Append(easing).Append(';');
                css.Append('}');
            }
            css.Append('}');
        }
        if (rest.Length > 0) css.Append(cls).Append(">.a{").Append(rest).Append('}');
    }

    private static bool EmitText(Context ctx, DesignElement el, StringBuilder inner, StringBuilder css, string cls)
    {
        var text = el.Text ?? new DesignText();
        var bound = false;
        inner.Append("<p class=\"t\">");
        foreach (var run in text.Runs)
        {
            var open = new StringBuilder();
            var close = new StringBuilder();
            if (run.Bold) { open.Append("<b>"); close.Insert(0, "</b>"); }
            if (run.Italic) { open.Append("<i>"); close.Insert(0, "</i>"); }

            if (!string.IsNullOrWhiteSpace(run.Var))
            {
                var span = VariableSpan(ctx, el, run.Var, "var");
                if (span is null) continue;
                inner.Append(open).Append(span).Append(close);
                bound = true;
            }
            else if (!string.IsNullOrEmpty(run.Text))
            {
                inner.Append(open).Append(WebUtility.HtmlEncode(Truncate(run.Text))).Append(close);
            }
        }
        inner.Append("</p>");

        css.Append(cls).Append(">.a{display:flex;flex-direction:column;justify-content:")
            .Append(text.Style.VAlign switch { "top" => "flex-start", "bottom" => "flex-end", _ => "center" })
            .Append('}');
        css.Append(cls).Append(" .t{").Append(Typography(ctx, text.Style)).Append('}');
        return bound;
    }

    /// <summary>A <c>data-var</c>/<c>data-href</c> span for a scene path, or null when the path isn't allowed.</summary>
    private static string? VariableSpan(Context ctx, DesignElement el, string path, string kind)
    {
        path = path.Trim();
        var catalog = DesignCatalog.FindVariable(path);
        ctx.Fields.TryGetValue(path, out var custom);
        if (catalog is null && custom is null) return null;
        if (catalog is { Kind: "image" }) return null;

        var sb = new StringBuilder("<span data-var=\"").Append(Attr(path)).Append('"');
        if (custom is not null)
        {
            sb.Append(" data-field-label=\"").Append(Attr(custom.Label)).Append('"');
            if (DesignCatalog.FieldTypes.Contains(custom.Type) && custom.Type != "text")
                sb.Append(" data-type=\"").Append(custom.Type).Append('"');
            if (custom.Type == "select" && custom.Options is { Count: > 0 })
                sb.Append(" data-options=\"").Append(Attr(string.Join(",", custom.Options.Select(o => o.Replace(",", " "))))).Append('"');
        }
        else if (catalog is not null && path.StartsWith("event.", StringComparison.Ordinal) && !path.StartsWith("event.venue.", StringComparison.Ordinal))
        {
            sb.Append(" data-field-label=\"").Append(Attr(catalog.Label)).Append('"');
            if (catalog.Type is "textarea" or "date" or "time")
                sb.Append(" data-type=\"").Append(catalog.Type).Append('"');
        }
        var scope = RoleScope(ctx, custom?.RoleScope ?? el.RoleScope);
        if (scope is not null) sb.Append(" data-role-scope=\"").Append(Attr(scope)).Append('"');

        var sample = custom?.Sample ?? catalog?.Sample ?? custom?.Label ?? string.Empty;
        sb.Append('>').Append(WebUtility.HtmlEncode(Truncate(sample))).Append("</span>");
        return sb.ToString();
    }

    private static void EmitShape(Context ctx, DesignElement el, StringBuilder inner)
    {
        var shape = el.Shape ?? new DesignShape();
        var w = Math.Max(1, el.W);
        var h = Math.Max(1, el.H);
        var fill = DesignCss.Color(shape.Fill, ctx.ThemeKeys) ?? "transparent";
        var stroke = DesignCss.Color(shape.Stroke, ctx.ThemeKeys);
        var sw = stroke is null ? 0 : DesignCss.Clamp(shape.StrokeWidth, 0, Math.Min(w, h) / 2);
        var style = new StringBuilder("fill:").Append(fill);
        if (sw > 0) style.Append(";stroke:").Append(stroke).Append(";stroke-width:").Append(DesignCss.Num(sw));
        var half = sw / 2;

        inner.Append("<svg class=\"s\" viewBox=\"0 0 ").Append(DesignCss.Num(w)).Append(' ').Append(DesignCss.Num(h))
            .Append("\" preserveAspectRatio=\"none\" aria-hidden=\"true\">");
        if (shape.Kind == "path" && shape.Path is { } drawn)
        {
            EmitPath(inner, drawn, fill, stroke, sw, DesignCss.Color(shape.Fill, ctx.ThemeKeys));
            return;
        }
        switch (shape.Kind)
        {
            case "ellipse":
                inner.Append("<ellipse cx=\"").Append(DesignCss.Num(w / 2)).Append("\" cy=\"").Append(DesignCss.Num(h / 2))
                    .Append("\" rx=\"").Append(DesignCss.Num(Math.Max(0, w / 2 - half))).Append("\" ry=\"")
                    .Append(DesignCss.Num(Math.Max(0, h / 2 - half))).Append("\" style=\"").Append(style).Append("\"/>");
                break;
            case "line":
                var lineStroke = DesignCss.Color(shape.Stroke, ctx.ThemeKeys) ?? DesignCss.Color(shape.Fill, ctx.ThemeKeys) ?? "currentColor";
                var lw = DesignCss.Clamp(shape.StrokeWidth <= 0 ? 2 : shape.StrokeWidth, 0.5, h);
                inner.Append("<line x1=\"0\" y1=\"").Append(DesignCss.Num(h / 2)).Append("\" x2=\"").Append(DesignCss.Num(w))
                    .Append("\" y2=\"").Append(DesignCss.Num(h / 2)).Append("\" style=\"stroke:").Append(lineStroke)
                    .Append(";stroke-width:").Append(DesignCss.Num(lw)).Append("\"/>");
                break;
            case "polygon":
                var sides = Math.Clamp(shape.Sides, 3, 12);
                var points = Enumerable.Range(0, sides).Select(i =>
                {
                    var angle = -Math.PI / 2 + i * 2 * Math.PI / sides;
                    return $"{DesignCss.Num(w / 2 + (w / 2 - half) * Math.Cos(angle))},{DesignCss.Num(h / 2 + (h / 2 - half) * Math.Sin(angle))}";
                });
                inner.Append("<polygon points=\"").Append(string.Join(' ', points)).Append("\" style=\"").Append(style).Append("\"/>");
                break;
            default:
                var r = DesignCss.Clamp(shape.Radius, 0, Math.Min(w, h) / 2);
                inner.Append("<rect x=\"").Append(DesignCss.Num(half)).Append("\" y=\"").Append(DesignCss.Num(half))
                    .Append("\" width=\"").Append(DesignCss.Num(Math.Max(0, w - sw))).Append("\" height=\"")
                    .Append(DesignCss.Num(Math.Max(0, h - sw))).Append('"');
                if (r > 0) inner.Append(" rx=\"").Append(DesignCss.Num(r)).Append('"');
                inner.Append(" style=\"").Append(style).Append("\"/>");
                break;
        }
        inner.Append("</svg>");
    }

    /// <summary>
    /// A drawn outline, in its own viewBox so it stretches with the element. Closed contours share one
    /// path (filled, non-zero, so overlapping parts read as one shape); open ones are lines.
    /// </summary>
    private static void EmitPath(StringBuilder inner, DesignPath path, string fill, string? stroke, double strokeWidth, string? fillColor)
    {
        var pw = DesignCss.Clamp(path.Width, 1, 10000);
        var ph = DesignCss.Clamp(path.Height, 1, 10000);
        // Nested in the element's own <svg>, in the path's space, so it stretches with the element's box.
        inner.Append("<svg viewBox=\"0 0 ").Append(DesignCss.Num(pw)).Append(' ').Append(DesignCss.Num(ph))
            .Append("\" preserveAspectRatio=\"none\" width=\"100%\" height=\"100%\" overflow=\"visible\">");
        var closed = PathD(path.Contours.Where(c => c.Closed));
        var open = PathD(path.Contours.Where(c => !c.Closed));
        if (closed.Length > 0)
        {
            inner.Append("<path d=\"").Append(closed).Append("\" fill-rule=\"nonzero\" style=\"fill:").Append(fill);
            if (strokeWidth > 0 && stroke is not null)
                inner.Append(";stroke:").Append(stroke).Append(";stroke-width:").Append(DesignCss.Num(strokeWidth)).Append(";stroke-linejoin:round");
            inner.Append("\"/>");
        }
        if (open.Length > 0)
        {
            var lineColor = stroke ?? fillColor ?? "currentColor";
            var lineWidth = strokeWidth > 0 ? strokeWidth : 2;
            inner.Append("<path d=\"").Append(open).Append("\" style=\"fill:none;stroke:").Append(lineColor)
                .Append(";stroke-width:").Append(DesignCss.Num(lineWidth)).Append(";stroke-linecap:round;stroke-linejoin:round\"/>");
        }
        inner.Append("</svg></svg>");
    }

    /// <summary>The SVG path data for contours. Only numbers are written, so nothing but geometry can come out.</summary>
    public static string PathD(IEnumerable<DesignContour> contours)
    {
        var d = new StringBuilder();
        foreach (var c in contours.Take(DesignCatalog.MaxPathContours))
        {
            var pts = c.Points.Where(p => Finite(p.X, p.Y) && (p.In is null || Finite(p.In.X, p.In.Y)) && (p.Out is null || Finite(p.Out.X, p.Out.Y)))
                .Take(DesignCatalog.MaxPathPoints).ToList();
            if (pts.Count < 2) continue;
            d.Append('M').Append(DesignCss.Num(pts[0].X)).Append(' ').Append(DesignCss.Num(pts[0].Y));
            var count = c.Closed ? pts.Count : pts.Count - 1;
            for (var i = 0; i < count; i++)
            {
                var a = pts[i];
                var b = pts[(i + 1) % pts.Count];
                if (a.Out is null && b.In is null)
                {
                    d.Append('L').Append(DesignCss.Num(b.X)).Append(' ').Append(DesignCss.Num(b.Y));
                }
                else
                {
                    var c1x = a.Out?.X ?? a.X; var c1y = a.Out?.Y ?? a.Y;
                    var c2x = b.In?.X ?? b.X; var c2y = b.In?.Y ?? b.Y;
                    d.Append('C').Append(DesignCss.Num(c1x)).Append(' ').Append(DesignCss.Num(c1y)).Append(' ')
                        .Append(DesignCss.Num(c2x)).Append(' ').Append(DesignCss.Num(c2y)).Append(' ')
                        .Append(DesignCss.Num(b.X)).Append(' ').Append(DesignCss.Num(b.Y));
                }
            }
            if (c.Closed) d.Append('Z');
        }
        return d.ToString();

        static bool Finite(double x, double y) => double.IsFinite(x) && double.IsFinite(y);
    }

    private static void EmitSvg(Context ctx, DesignElement el, StringBuilder inner, StringBuilder css, string cls)
    {
        if (el.Svg is null || !ctx.SvgSymbols.TryGetValue(el.Svg.Asset, out var index)) return;
        if (!ctx.SvgColors.TryGetValue(el.Svg.Asset, out var colors)) return;
        inner.Append("<svg class=\"s\" aria-hidden=\"true\"><use href=\"#s").Append(index)
            .Append("\" width=\"100%\" height=\"100%\"/></svg>");

        var vars = new StringBuilder();
        for (var i = 0; i < colors.Count; i++)
        {
            if (!el.Svg.Fills.TryGetValue(colors[i], out var reference)) continue;
            var color = DesignCss.Color(reference, ctx.ThemeKeys);
            if (color is not null) vars.Append("--c").Append(i).Append(':').Append(color).Append(';');
        }
        if (vars.Length > 0) css.Append(cls).Append("{").Append(vars).Append('}');
    }

    private static void EmitImage(Context ctx, DesignElement el, StringBuilder inner, StringBuilder css, string cls)
    {
        if (el.Image is null || !ctx.ImageClasses.TryGetValue(el.Image.Asset, out var index)) return;
        var asset = ctx.Scene.Assets[el.Image.Asset];
        var uri = ImageDataUri(asset.Data);
        if (uri is null) return;

        // Declared once per asset however many elements use it — a repeated flourish costs its bytes once.
        var imageClass = $"i{index}";
        if (ctx.EmittedImages.Add(imageClass))
            css.Append('.').Append(imageClass).Append("{background-image:url(\"").Append(uri).Append("\")}");

        inner.Append("<div class=\"").Append(imageClass).Append("\" role=\"presentation\"></div>");
        css.Append(cls).Append(" .").Append(imageClass).Append("{width:100%;height:100%;background-repeat:no-repeat;background-position:center;background-size:")
            .Append(el.Image.Fit == "contain" ? "contain" : "cover");
        if (el.Image.Radius > 0) css.Append(";border-radius:").Append(DesignCss.U(el.Image.Radius));
        css.Append('}');
    }

    private static void EmitSlot(Context ctx, DesignElement el, StringBuilder inner, StringBuilder css, string cls)
    {
        var slot = el.Slot ?? new DesignSlot();
        var path = SlotPath(slot.Path);
        // One photo of a gallery: the binder reads "event.gallery.2" as the third item, and the
        // packager folds the path back into the one gallery the host fills.
        // A lone element on the gallery is its first photo unless told otherwise.
        var index = slot.Index ?? (!slot.Multiple && path == "event.gallery" ? 1 : null);
        if (!slot.Multiple && index is >= 1 and <= DesignCatalog.MaxGalleryIndex) path += "." + (index.Value - 1);
        inner.Append("<img data-src=\"").Append(Attr(path)).Append("\" data-slot-label=\"")
            .Append(Attr(string.IsNullOrWhiteSpace(slot.Label) ? "Photo" : Truncate(slot.Label.Trim(), 60))).Append('"');
        if (slot.Multiple)
        {
            inner.Append(" data-multiple=\"true\"");
            if (slot.Min is > 0) inner.Append(" data-min-images=\"").Append(slot.Min.Value).Append('"');
            if (slot.Max is > 0) inner.Append(" data-max-images=\"").Append(slot.Max.Value).Append('"');
        }
        var scope = RoleScope(ctx, el.RoleScope);
        if (scope is not null) inner.Append(" data-role-scope=\"").Append(Attr(scope)).Append('"');
        inner.Append(" alt=\"\">");

        var fit = slot.Fit == "contain" ? "contain" : "cover";
        var radius = slot.Radius > 0 ? $";border-radius:{DesignCss.U(slot.Radius)}" : string.Empty;
        if (slot.Multiple)
        {
            var cols = Math.Clamp(slot.Columns, 1, 6);
            css.Append(cls).Append(">.a{display:grid;grid-template-columns:repeat(").Append(cols)
                .Append(",minmax(0,1fr));gap:").Append(DesignCss.U(DesignCss.Clamp(slot.Gap, 0, 80)))
                .Append(";align-content:start;overflow:hidden}");
            css.Append(cls).Append(" img{display:block;width:100%;aspect-ratio:")
                .Append(DesignCss.Num(DesignCss.Clamp(slot.Aspect, 0.2, 5))).Append(";object-fit:").Append(fit).Append(radius).Append('}');
        }
        else
        {
            css.Append(cls).Append(" img{display:block;width:100%;height:100%;object-fit:").Append(fit).Append(radius).Append('}');
        }
    }

    private static bool EmitButton(Context ctx, DesignElement el, StringBuilder inner, StringBuilder css, string cls)
    {
        var button = el.Button ?? new DesignButton();
        string path;
        if (el.Type == "rsvp")
        {
            path = "rsvp.link";
        }
        else
        {
            path = button.Path?.Trim() ?? string.Empty;
            var allowed = DesignCatalog.LinkPaths.Contains(path)
                          || (ctx.Fields.TryGetValue(path, out var field) && field.Type == "url");
            if (!allowed) return false;
        }

        inner.Append("<a class=\"b\" data-href=\"").Append(Attr(path)).Append("\" href=\"#\"");
        if (path == "event.venue.mapLink" || ctx.Fields.ContainsKey(path))
            inner.Append(" target=\"_blank\" rel=\"noopener\"");
        var scope = ctx.Fields.TryGetValue(path, out var custom) ? RoleScope(ctx, custom.RoleScope ?? el.RoleScope) : null;
        if (scope is not null) inner.Append(" data-role-scope=\"").Append(Attr(scope)).Append('"');
        inner.Append('>');
        if (el.Type == "rsvp")
            inner.Append("<span data-var=\"rsvp.label\">Reply now</span>");
        else
            inner.Append(WebUtility.HtmlEncode(Truncate(string.IsNullOrWhiteSpace(button.Label) ? "Open" : button.Label, 80)));
        inner.Append("</a>");

        css.Append(cls).Append(" .b{").Append(Typography(ctx, button.Style));
        var fill = DesignCss.Color(button.Fill, ctx.ThemeKeys);
        if (fill is not null) css.Append("background:").Append(fill).Append(';');
        var stroke = DesignCss.Color(button.Stroke, ctx.ThemeKeys);
        if (stroke is not null && button.StrokeWidth > 0)
            css.Append("border:").Append(DesignCss.U(DesignCss.Clamp(button.StrokeWidth, 0, 20))).Append(" solid ").Append(stroke).Append(';');
        css.Append("border-radius:").Append(DesignCss.U(DesignCss.Clamp(button.Radius, 0, 999))).Append(";padding:0 ")
            .Append(DesignCss.U(12)).Append('}');
        return true;
    }

    private static void EmitDress(Context ctx, DesignElement el, StringBuilder inner, StringBuilder css, string cls)
    {
        var dress = el.Dress ?? new DesignDress();
        inner.Append("<div class=\"d\" data-dress-colors></div>");
        var size = DesignCss.U(DesignCss.Clamp(dress.Swatch, 8, 160));
        css.Append(cls).Append(" .d{width:100%;height:100%;overflow:hidden}");
        css.Append(cls).Append(" .ib-dress{margin:0 0 ").Append(DesignCss.U(12)).Append('}');
        css.Append(cls).Append(" .ib-dress__role{margin:0 0 ").Append(DesignCss.U(8)).Append(';').Append(Typography(ctx, dress.Style)).Append('}');
        css.Append(cls).Append(" .ib-dress__swatches{display:flex;flex-wrap:wrap;gap:").Append(DesignCss.U(DesignCss.Clamp(dress.Gap, 0, 60)))
            .Append(";justify-content:").Append(dress.Style.Align switch { "left" => "flex-start", "right" => "flex-end", _ => "center" }).Append('}');
        css.Append(cls).Append(" .ib-dress__swatch{display:block;width:").Append(size).Append(";height:").Append(size)
            .Append(";border-radius:").Append(dress.Shape == "square" ? DesignCss.U(6) : "50%")
            .Append(";box-shadow:0 0 0 1px color-mix(in srgb, currentColor 25%, transparent)}");
    }

    // ----- Helpers -----------------------------------------------------------------------------------

    private static string Typography(Context ctx, DesignTypography style)
    {
        var sb = new StringBuilder();
        var font = DesignCss.Font(style.Font, ctx.ThemeKeys);
        if (font is not null) sb.Append("font-family:").Append(font).Append(';');
        sb.Append("font-size:").Append(DesignCss.U(DesignCss.Clamp(style.Size, 4, 400))).Append(';');
        sb.Append("font-weight:").Append(Math.Clamp(style.Weight / 100 * 100, 100, 900)).Append(';');
        if (style.Italic) sb.Append("font-style:italic;");
        var color = DesignCss.Color(style.Color, ctx.ThemeKeys);
        if (color is not null) sb.Append("color:").Append(color).Append(';');
        sb.Append("text-align:").Append(style.Align is "left" or "right" ? style.Align : "center").Append(';');
        sb.Append("line-height:").Append(DesignCss.Num(DesignCss.Clamp(style.LineHeight, 0.6, 4))).Append(';');
        if (style.LetterSpacing != 0)
            sb.Append("letter-spacing:").Append(DesignCss.Num(DesignCss.Clamp(style.LetterSpacing, -0.2, 2))).Append("em;");
        if (style.Uppercase) sb.Append("text-transform:uppercase;");
        return sb.ToString();
    }

    private static string Transform(double dx, double dy, double rotate, double scale)
    {
        var parts = new List<string>();
        if (Math.Abs(dx) > 0.0005 || Math.Abs(dy) > 0.0005) parts.Add($"translate({DesignCss.U(dx)},{DesignCss.U(dy)})");
        if (Math.Abs(rotate) > 0.0005) parts.Add($"rotate({DesignCss.Num(DesignCss.Clamp(rotate, -3600, 3600))}deg)");
        if (Math.Abs(scale - 1) > 0.0005) parts.Add($"scale({DesignCss.Num(DesignCss.Clamp(scale, 0, 20))})");
        return parts.Count == 0 ? "none" : string.Join(' ', parts);
    }

    public sealed record ResolvedFrame(double T, double X, double Y, double Rotate, double Scale, double Opacity, string? Easing, int Lift = 0);

    /// <summary>
    /// The keyframes with every property filled in. A property a keyframe doesn't set carries over from
    /// the keyframe before it (the element's resting value before the first). The first and last states
    /// are pinned to 0% and 100% so the element HOLDS them outside its keyframes instead of drifting
    /// from its resting state — the way every animation tool behaves.
    /// </summary>
    public static IReadOnlyList<ResolvedFrame> ResolveFrames(DesignElement el)
    {
        var frames = el.Keyframes
            .Where(k => !double.IsNaN(k.T))
            .OrderBy(k => DesignCss.Clamp(k.T, 0, 1))
            .Take(DesignCatalog.MaxKeyframes)
            .ToList();
        var result = new List<ResolvedFrame>();
        double x = el.X, y = el.Y, rotate = el.Rotate, scale = el.Scale, opacity = DesignCss.Clamp(el.Opacity, 0, 1);
        var lift = 0;
        foreach (var k in frames)
        {
            lift = Math.Clamp(k.Lift ?? lift, 0, DesignCatalog.MaxLift);
            x = k.X ?? x;
            y = k.Y ?? y;
            rotate = k.Rotate ?? rotate;
            scale = k.Scale ?? scale;
            opacity = DesignCss.Clamp(k.Opacity ?? opacity, 0, 1);
            var t = DesignCss.Clamp(k.T, 0, 1);
            // Two keyframes at one position: the later wins, as it does when you drop one on another.
            if (result.Count > 0 && Math.Abs(result[^1].T - t) < 0.00001) result.RemoveAt(result.Count - 1);
            result.Add(new ResolvedFrame(t, x, y, rotate, scale, opacity, DesignCss.Easing(k.Easing), lift));
        }
        if (result.Count == 0) return result;
        if (result[0].T > 0) result.Insert(0, result[0] with { T = 0, Easing = null });
        if (result[^1].T < 1) result.Add(result[^1] with { T = 1, Easing = null });
        return result;
    }

    /// <summary>The element's track, clamped to the page; the whole page when it has none.</summary>
    public static DesignTrack TrackOf(DesignScene scene, DesignElement el)
    {
        var range = Math.Max(1, scene.Canvas.ScrollRange);
        if (el.Track is null) return new DesignTrack { Start = 0, End = range };
        var start = DesignCss.Clamp(el.Track.Start, 0, range);
        var end = DesignCss.Clamp(el.Track.End, 0, range);
        return new DesignTrack { Start = start, End = end };
    }

    private static string? RoleScope(Context ctx, string? scope)
    {
        if (string.IsNullOrWhiteSpace(scope)) return null;
        var slug = Slug(scope);
        return ctx.Scene.Roles.Any(r => Slug(r) == slug) ? slug : null;
    }

    private static string SlotPath(string? path)
    {
        var p = path?.Trim() ?? string.Empty;
        if (DesignCatalog.FindVariable(p) is { Kind: "image" }) return p;
        if (p.StartsWith("event.", StringComparison.Ordinal) && Slug(p[6..], camel: true) is { Length: > 0 } key)
            return "event." + key;
        return "event.coverImage";
    }

    /// <summary>
    /// A data: URI rebuilt from its parts: an allowed raster type and strict base64. Anything else is
    /// dropped rather than escaped, so no quote or parenthesis can ever reach the stylesheet.
    /// </summary>
    public static string? ImageDataUri(string? data)
    {
        if (string.IsNullOrEmpty(data)) return null;
        string[] types = ["image/webp", "image/png", "image/jpeg", "image/gif"];
        foreach (var type in types)
        {
            var prefix = $"data:{type};base64,";
            if (!data.StartsWith(prefix, StringComparison.Ordinal)) continue;
            var payload = data[prefix.Length..];
            foreach (var c in payload)
                if (!(char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '=')) return null;
            return prefix + payload;
        }
        return null;
    }

    /// <summary>Lowercase slug: letters, digits and dashes. With <paramref name="camel"/>, a camelCase key instead.</summary>
    public static string Slug(string value, bool camel = false)
    {
        var sb = new StringBuilder();
        var upperNext = false;
        foreach (var ch in value.Trim())
        {
            if (char.IsAsciiLetterOrDigit(ch))
            {
                if (camel)
                {
                    sb.Append(upperNext && sb.Length > 0 ? char.ToUpperInvariant(ch) : (sb.Length == 0 ? char.ToLowerInvariant(ch) : ch));
                }
                else
                {
                    sb.Append(char.ToLowerInvariant(ch));
                }
                upperNext = false;
            }
            else if (camel)
            {
                upperNext = true;
            }
            else if (sb.Length > 0 && sb[^1] != '-')
            {
                sb.Append('-');
            }
            if (sb.Length >= 40) break;
        }
        return sb.ToString().Trim('-');
    }

    private static string Attr(string value) => WebUtility.HtmlEncode(value);

    private static string Truncate(string value, int max = DesignCatalog.MaxTextLength) =>
        value.Length <= max ? value : value[..max];

    // ----- Scripts -----------------------------------------------------------------------------------

    /// <summary>Runs in &lt;head&gt; so animations are paused from the first frame, not after a flash of playback.</summary>
    private const string DetectScript =
        "if(!(window.CSS&&CSS.supports&&CSS.supports('animation-timeline: scroll()')))document.documentElement.classList.add('ib-fb')";

    /// <summary>
    /// Old-browser driver. Pauses the page's own CSS animations and scrubs them by scroll position.
    /// Reads geometry once per resize, never per scroll.
    /// </summary>
    private const string FallbackScript = """
        (function(){var d=document.documentElement;if(!d.classList.contains('ib-fb'))return;
        var els=[].slice.call(document.querySelectorAll('[data-ts]')),u=1,q=0;
        function size(){u=Math.min(window.innerWidth||390,480)/390;}
        function each(el,p){var list=[el,el.firstElementChild];for(var i=0;i<list.length;i++){var n=list[i];if(!n||!n.getAnimations)continue;var a=n.getAnimations();for(var j=0;j<a.length;j++){try{a[j].pause();a[j].currentTime=p*1000;}catch(e){}}}}
        function run(){q=0;var y=(window.pageYOffset||0)/u;for(var i=0;i<els.length;i++){var el=els[i],s=+el.getAttribute('data-ts'),e=+el.getAttribute('data-te'),p=e>s?(y-s)/(e-s):0;each(el,p<0?0:p>1?1:p);}}
        function tick(){if(!q)q=requestAnimationFrame(run);}
        size();run();addEventListener('scroll',tick,{passive:true});addEventListener('resize',function(){size();tick();});})();
        """;

    /// <summary>
    /// Editor preview only. Lets the designer's playhead and the preview's scroll position follow each
    /// other. Never part of a published package.
    /// </summary>
    private const string EditorScript = """
        (function(){function u(){return Math.min(window.innerWidth||390,480)/390;}
        var own=false;window.scrollTo(0,__SCROLL__*u());
        addEventListener('message',function(e){var m=e.data||{};if(m.type==='ib:scroll'){own=true;window.scrollTo(0,(+m.y||0)*u());}});
        var f=0;addEventListener('scroll',function(){if(f)return;f=requestAnimationFrame(function(){f=0;if(own){own=false;return;}parent.postMessage({type:'ib:scrolled',y:window.pageYOffset/u()},'*');});},{passive:true});
        parent.postMessage({type:'ib:ready',y:window.pageYOffset/u()},'*');})();
        """;
}
