using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AngleSharp;
using AngleSharp.Dom;
using AngleSharp.Html.Dom;
using AngleSharp.Html.Parser;

namespace InvitesBlog.TemplateCompiler;

/// <summary>
/// Binds a campaign's resolved payload into a packaged template, server-side, once — the guest-facing
/// half of the contract documented on <see cref="TemplateInjector"/>. The browser half shrinks to
/// <see cref="TemplateRuntime"/>, which knows nothing about data.
///
/// This exists to delete two bug classes by construction. Binding in the browser ran on load AND on
/// every host post, so anything that cloned had to be idempotent — a gallery that wasn't turned six
/// photos into thirty-six, then two hundred and sixteen. And the invitation had to live in an iframe
/// to receive that post, which put `vh` units inside a box the phone's URL bar resizes mid-scroll.
/// Bound here, the document is complete before it is sent: no second pass, no nested viewport.
///
/// Deliberately mirrors the JavaScript's behaviour down to its edge cases, because templates were
/// written against them — including that a missing value BLANKS its element rather than leaving the
/// authored placeholder, and that `null` reads as missing.
/// </summary>
public static class ServerBinder
{
    private static readonly HtmlParser Parser = new();

    private static readonly JsonSerializerOptions PayloadJson = new()
    {
        // The payload is embedded in a <script> block; keeping non-ASCII escaped means no character
        // in a guest's name can terminate the element early.
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>
    /// Returns <paramref name="packagedHtml"/> with the payload applied and the browser runtime
    /// swapped in for the injector. Input is a packaged template — the output of
    /// <c>RawTemplatePackager</c>, i.e. one self-contained document ending in the injector script.
    /// </summary>
    public static string Bind(string packagedHtml, JsonObject data)
    {
        var doc = Parser.ParseDocument(packagedHtml);

        ApplyTheme(doc, data["themeVars"] as JsonObject);
        ApplyText(doc, data);
        ApplyHrefs(doc, data);
        ApplyImages(doc, data);
        // Order matters: [data-optional] decides by what binding actually produced, so it has to run
        // after all three passes above — the same order the JavaScript ran them in.
        HideEmptyOptionals(doc);
        ApplyBlocks(doc, data["resolvedBlocks"] as JsonArray);
        StripReducedMotion(doc);
        SwapRuntime(doc, data);

        return doc.ToHtml();
    }

    /// <summary>
    /// The inviter's theme choices, as the CSS custom properties the author declared. Set inline on
    /// &lt;html&gt; rather than as a rule, because that is where the JavaScript set them and inline
    /// styles outrank any stylesheet — a template's own <c>:root{--ib-accent:…}</c> default must not
    /// win over a choice the inviter actually made.
    /// </summary>
    private static void ApplyTheme(IHtmlDocument doc, JsonObject? vars)
    {
        if (vars is null || doc.DocumentElement is null) return;

        var declarations = new StringBuilder(doc.DocumentElement.GetAttribute("style") ?? string.Empty);
        foreach (var (name, node) in vars)
        {
            // Only ever custom properties, and never let a value close the declaration and start
            // another — the value reaches us as data and must stay data.
            if (!name.StartsWith("--", StringComparison.Ordinal) || node is null) continue;
            var value = node.ToString();
            if (value.Contains(';') || value.Contains('}')) continue;

            if (declarations.Length > 0 && declarations[^1] != ';') declarations.Append(';');
            declarations.Append(name).Append(':').Append(value).Append(';');
        }

        if (declarations.Length > 0) doc.DocumentElement.SetAttribute("style", declarations.ToString());
    }

    /// <summary>Text, never markup — <c>TextContent</c> is what makes guest content inert.</summary>
    private static void ApplyText(IHtmlDocument doc, JsonObject data)
    {
        foreach (var el in doc.QuerySelectorAll("[data-var]"))
        {
            var value = Resolve(data, el.GetAttribute("data-var"));
            // A missing value BLANKS the element. Authored placeholder text is what shows before data
            // arrives; it was never a fallback, and templates are written expecting this.
            el.TextContent = value is null ? string.Empty : Stringify(value);
        }
    }

    private static void ApplyHrefs(IHtmlDocument doc, JsonObject data)
    {
        foreach (var el in doc.QuerySelectorAll("[data-href]"))
        {
            var value = Resolve(data, el.GetAttribute("data-href"));
            if (value is not null) el.SetAttribute("href", Stringify(value));
        }
    }

    /// <summary>
    /// Images, and galleries. An array means the authored element is the template for the first photo
    /// and is cloned for the rest, so the author's markup and styling carry to every image.
    /// </summary>
    private static void ApplyImages(IHtmlDocument doc, JsonObject data)
    {
        var galleryGroups = 0;

        foreach (var el in doc.QuerySelectorAll("[data-src]").ToList())
        {
            var value = Resolve(data, el.GetAttribute("data-src"));
            if (value is null) continue;

            if (value is not JsonArray gallery)
            {
                el.SetAttribute("src", Stringify(value));
                continue;
            }

            if (gallery.Count == 0) continue;

            // The clone bookkeeping the JavaScript needed to stay idempotent across repeated passes is
            // pointless here — this runs once — but the attributes are kept so the rendered DOM is the
            // one templates were written and styled against.
            var groupId = "g" + ++galleryGroups;
            el.SetAttribute("data-gallery-of", groupId);
            el.SetAttribute("src", Stringify(gallery[0]!));

            var anchor = el;
            for (var i = 1; i < gallery.Count; i++)
            {
                if (gallery[i] is not { } photo) continue;
                var clone = (IElement)el.Clone(deep: true);
                clone.RemoveAttribute("data-gallery-of");
                clone.SetAttribute("data-gallery-clone", groupId);
                clone.SetAttribute("src", Stringify(photo));
                anchor.InsertAfter(clone);
                anchor = clone;
            }
        }
    }

    /// <summary>
    /// Hides any <c>[data-optional]</c> element whose bound value(s) came back empty, so nullable or
    /// omitted fields don't leave stray labels, dead links and blank rows behind.
    /// </summary>
    private static void HideEmptyOptionals(IHtmlDocument doc)
    {
        foreach (var el in doc.QuerySelectorAll("[data-optional]"))
        {
            var filled = IsFilled(el)
                         || el.QuerySelectorAll("[data-var], [data-href], [data-src]").Any(IsFilled);
            if (!filled) Hide(el);
        }
    }

    private static bool IsFilled(IElement el)
    {
        if (el.HasAttribute("data-var") && el.TextContent.Trim().Length > 0) return true;

        if (el.HasAttribute("data-href"))
        {
            var href = el.GetAttribute("href");
            if (!string.IsNullOrEmpty(href) && href != "#"
                && !href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) return true;
        }

        if (el.HasAttribute("data-src") && !string.IsNullOrEmpty(el.GetAttribute("src"))) return true;

        return false;
    }

    /// <summary>Show the blocks the rules resolved for this guest, hide the rest.</summary>
    private static void ApplyBlocks(IHtmlDocument doc, JsonArray? resolved)
    {
        if (resolved is null) return;

        var keep = resolved.Select(b => b?.ToString())
            .Where(b => !string.IsNullOrEmpty(b))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var el in doc.QuerySelectorAll("[data-block]"))
            if (!keep.Contains(el.GetAttribute("data-block") ?? string.Empty)) Hide(el);
    }

    /// <summary>
    /// An invitation's whole value is its motion, so a template's own
    /// <c>@media (prefers-reduced-motion: reduce)</c> rules are dropped — otherwise reduced-motion and
    /// iOS Low-Power-Mode readers get a frozen, lifeless invite. The JavaScript did this by deleting
    /// rules from <c>document.styleSheets</c> after paint, which meant the calmed styles applied for a
    /// frame first; done here they never reach the browser at all.
    /// </summary>
    private static void StripReducedMotion(IHtmlDocument doc)
    {
        foreach (var style in doc.QuerySelectorAll("style"))
        {
            var css = style.TextContent;
            if (css.Contains("prefers-reduced-motion", StringComparison.OrdinalIgnoreCase))
                style.TextContent = RemoveReducedMotionBlocks(css);
        }
    }

    /// <summary>
    /// Removes each <c>@media</c> block whose prelude asks for reduced motion, brace-matching to find
    /// its end so nested rules inside it go with it.
    /// </summary>
    public static string RemoveReducedMotionBlocks(string css)
    {
        var result = new StringBuilder(css.Length);
        var i = 0;

        while (i < css.Length)
        {
            var at = css.IndexOf("@media", i, StringComparison.OrdinalIgnoreCase);
            if (at < 0) { result.Append(css, i, css.Length - i); break; }

            var open = css.IndexOf('{', at);
            if (open < 0) { result.Append(css, i, css.Length - i); break; }

            var prelude = css.AsSpan(at, open - at);
            if (!Asks(prelude, "prefers-reduced-motion") || !Asks(prelude, "reduce"))
            {
                // Some other media query — copy through it and resume the search past its brace, so a
                // reduced-motion block nested deeper still gets found.
                result.Append(css, i, open + 1 - i);
                i = open + 1;
                continue;
            }

            var close = MatchingBrace(css, open);
            if (close < 0) { result.Append(css, i, css.Length - i); break; }

            result.Append(css, i, at - i);
            i = close + 1;
        }

        return result.ToString();
    }

    private static bool Asks(ReadOnlySpan<char> prelude, string term) =>
        prelude.Contains(term, StringComparison.OrdinalIgnoreCase);

    private static int MatchingBrace(string css, int open)
    {
        var depth = 0;
        for (var i = open; i < css.Length; i++)
        {
            if (css[i] == '{') depth++;
            else if (css[i] == '}' && --depth == 0) return i;
        }
        return -1;
    }

    /// <summary>
    /// Fills the inline payload with the real data and replaces the injector with the data-blind
    /// runtime. The payload stays in the document because templates may read
    /// <c>window.invite.data</c> / the <c>invite:data</c> event, which the runtime still raises.
    /// </summary>
    private static void SwapRuntime(IHtmlDocument doc, JsonObject data)
    {
        var payload = doc.GetElementById("invite-data");
        if (payload is null)
        {
            payload = doc.CreateElement("script");
            payload.Id = "invite-data";
            payload.SetAttribute("type", "application/json");
            (doc.Body ?? doc.DocumentElement).AppendChild(payload);
        }
        payload.TextContent = data.ToJsonString(PayloadJson);
        // Says the DOM is already bound, so nothing downstream re-applies it.
        payload.SetAttribute("data-bound", "1");

        // The injector is identified by a string only it contains. Matching on content rather than on
        // position because the packager appends it, but a template author controls everything else in
        // the document and could have their own scripts either side of it.
        foreach (var script in doc.QuerySelectorAll("script").ToList())
            if (script.TextContent.Contains("__inviteReady", StringComparison.Ordinal))
                script.Remove();

        var runtime = doc.CreateElement("script");
        runtime.TextContent = TemplateRuntime.Js;
        (doc.Body ?? doc.DocumentElement).AppendChild(runtime);
    }

    private static void Hide(IElement el)
    {
        var style = el.GetAttribute("style");
        el.SetAttribute("style", string.IsNullOrEmpty(style)
            ? "display:none"
            : style.TrimEnd().TrimEnd(';') + ";display:none");
    }

    /// <summary>
    /// Walks a dot-path. Returns null for anything missing — and treats a JSON <c>null</c> as missing
    /// too, which is what the browser implementation did and what templates expect.
    /// </summary>
    private static JsonNode? Resolve(JsonObject root, string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;

        JsonNode? node = root;
        foreach (var segment in path.Split('.'))
        {
            if (node is not JsonObject obj || !obj.TryGetPropertyValue(segment, out node) || node is null)
                return null;
        }
        return node;
    }

    /// <summary>A JSON string renders as its text, not as a quoted JSON literal.</summary>
    private static string Stringify(JsonNode node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : node.ToString();
}
