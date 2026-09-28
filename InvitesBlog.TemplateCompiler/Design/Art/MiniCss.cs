using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace InvitesBlog.TemplateCompiler.Design.Art;

/// <summary>
/// Just enough CSS to read what illustration tools write into an SVG's <c>&lt;style&gt;</c>: rules on
/// tags, classes and ids (with descendant combinators), and <c>@keyframes</c>. Pseudo-classes, attribute
/// selectors and the rest are skipped — a rule this can't match is a rule that doesn't apply, which is
/// the safe way round, because nothing read here reaches a page without going through the sanitiser.
/// </summary>
public sealed partial class MiniCss
{
    public sealed record Rule(IReadOnlyList<Selector> Selectors, IReadOnlyList<KeyValuePair<string, string>> Declarations, int Order);

    public sealed record KeyframeStop(double Offset, IReadOnlyDictionary<string, string> Declarations);

    /// <summary>A compound selector chain: the last part must match the element, the others an ancestor, in order.</summary>
    public sealed record Selector(IReadOnlyList<SimpleSelector> Parts)
    {
        public int Specificity => Parts.Sum(p => (p.Id is null ? 0 : 100) + p.Classes.Count * 10 + (p.Tag is null ? 0 : 1));

        public bool Matches(XElement el)
        {
            if (Parts.Count == 0 || !Parts[^1].Matches(el)) return false;
            var k = Parts.Count - 2;
            for (var a = el.Parent; a is not null && k >= 0; a = a.Parent)
                if (Parts[k].Matches(a)) k--;
            return k < 0;
        }
    }

    public sealed record SimpleSelector(string? Tag, string? Id, IReadOnlyList<string> Classes)
    {
        public bool Matches(XElement el)
        {
            if (Tag is not null && Tag != "*" && el.Name.LocalName != Tag) return false;
            if (Id is not null && (string?)el.Attribute("id") != Id) return false;
            if (Classes.Count > 0)
            {
                var cls = ((string?)el.Attribute("class") ?? string.Empty).Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (!Classes.All(cls.Contains)) return false;
            }
            return true;
        }
    }

    public List<Rule> Rules { get; } = new();
    public Dictionary<string, List<KeyframeStop>> Keyframes { get; } = new(StringComparer.Ordinal);

    [GeneratedRegex(@"/\*.*?\*/", RegexOptions.Singleline)]
    private static partial Regex CommentRegex();

    [GeneratedRegex(@"^(?<tag>[A-Za-z*][\w-]*)?(?<rest>(?:[.#][\w-]+)*)$")]
    private static partial Regex CompoundRegex();

    public static MiniCss Parse(string css)
    {
        var sheet = new MiniCss();
        sheet.ParseBlock(CommentRegex().Replace(css ?? string.Empty, " "));
        return sheet;
    }

    private void ParseBlock(string css)
    {
        var i = 0;
        while (i < css.Length)
        {
            var open = css.IndexOf('{', i);
            if (open < 0) return;
            var prelude = css[i..open].Trim();
            var close = MatchingBrace(css, open);
            if (close < 0) return;
            var body = css[(open + 1)..close];
            i = close + 1;

            // A statement at-rule (@import, @charset) ends at its ';' — skip past any before this block.
            while (prelude.StartsWith('@') && prelude.Contains(';'))
                prelude = prelude[(prelude.IndexOf(';') + 1)..].Trim();

            if (prelude.StartsWith("@", StringComparison.Ordinal))
            {
                var at = prelude.Split([' ', '\t', '\n', '\r'], 2, StringSplitOptions.RemoveEmptyEntries);
                var keyword = at[0].ToLowerInvariant();
                if (keyword is "@keyframes" or "@-webkit-keyframes" or "@-moz-keyframes" && at.Length > 1)
                    Keyframes[at[1].Trim().Trim('"', '\'')] = ParseKeyframes(body);
                // Motion is what's being converted; a reduced-motion override would switch it off.
                else if (keyword == "@media" && !prelude.Contains("reduce", StringComparison.OrdinalIgnoreCase))
                    ParseBlock(body);
                else if (keyword == "@supports") ParseBlock(body);
                continue;
            }

            var selectors = new List<Selector>();
            foreach (var part in prelude.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (ParseSelector(part) is { } sel) selectors.Add(sel);
            if (selectors.Count > 0) Rules.Add(new Rule(selectors, Declarations(body), Rules.Count));
        }
    }

    private static int MatchingBrace(string s, int open)
    {
        var depth = 0;
        for (var k = open; k < s.Length; k++)
        {
            if (s[k] == '{') depth++;
            else if (s[k] == '}' && --depth == 0) return k;
        }
        return -1;
    }

    private static Selector? ParseSelector(string text)
    {
        var parts = new List<SimpleSelector>();
        foreach (var raw in text.Split([' ', '\t', '\n', '\r', '>'], StringSplitOptions.RemoveEmptyEntries))
        {
            var m = CompoundRegex().Match(raw);
            if (!m.Success) return null; // pseudo-classes, attributes, siblings: not ours to guess at
            var tag = m.Groups["tag"].Success && m.Groups["tag"].Length > 0 ? m.Groups["tag"].Value : null;
            string? id = null;
            var classes = new List<string>();
            foreach (Match piece in Regex.Matches(m.Groups["rest"].Value, @"([.#])([\w-]+)"))
            {
                if (piece.Groups[1].Value == "#") id = piece.Groups[2].Value;
                else classes.Add(piece.Groups[2].Value);
            }
            parts.Add(new SimpleSelector(tag, id, classes));
        }
        return parts.Count == 0 ? null : new Selector(parts);
    }

    /// <summary><c>a: b; c: d</c> as pairs, in order, property names lowercased.</summary>
    public static List<KeyValuePair<string, string>> Declarations(string? body)
    {
        var list = new List<KeyValuePair<string, string>>();
        if (string.IsNullOrWhiteSpace(body)) return list;
        foreach (var decl in SplitTopLevel(body, ';'))
        {
            var colon = decl.IndexOf(':');
            if (colon <= 0) continue;
            var name = decl[..colon].Trim().ToLowerInvariant();
            var value = decl[(colon + 1)..].Trim();
            if (value.EndsWith("!important", StringComparison.OrdinalIgnoreCase)) value = value[..^10].Trim();
            if (name.Length > 0 && value.Length > 0) list.Add(new(name, value));
        }
        return list;
    }

    /// <summary>Splits on a separator that isn't inside parentheses (so <c>cubic-bezier(a,b,c,d)</c> stays whole).</summary>
    public static List<string> SplitTopLevel(string text, char separator)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        for (var k = 0; k < text.Length; k++)
        {
            if (text[k] == '(') depth++;
            else if (text[k] == ')') depth = Math.Max(0, depth - 1);
            else if (text[k] == separator && depth == 0)
            {
                parts.Add(text[start..k].Trim());
                start = k + 1;
            }
        }
        var last = text[start..].Trim();
        if (last.Length > 0) parts.Add(last);
        return parts.Where(p => p.Length > 0).ToList();
    }

    private static List<KeyframeStop> ParseKeyframes(string body)
    {
        var stops = new List<KeyframeStop>();
        var i = 0;
        while (i < body.Length)
        {
            var open = body.IndexOf('{', i);
            if (open < 0) break;
            var close = MatchingBrace(body, open);
            if (close < 0) break;
            var selector = body[i..open];
            var declarations = Declarations(body[(open + 1)..close])
                .GroupBy(d => d.Key).ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.Ordinal);
            foreach (var s in selector.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                double? offset = s.ToLowerInvariant() switch
                {
                    "from" => 0,
                    "to" => 1,
                    _ when s.EndsWith('%') && double.TryParse(s[..^1], NumberStyles.Float, CultureInfo.InvariantCulture, out var pct) => pct / 100,
                    _ => null,
                };
                if (offset is { } o && o is >= 0 and <= 1) stops.Add(new KeyframeStop(o, declarations));
            }
            i = close + 1;
        }
        return stops.OrderBy(s => s.Offset).ToList();
    }

    /// <summary>
    /// Every declaration that applies to an element, lowest priority first: matching rules by
    /// specificity then order, then its own <c>style</c> attribute.
    /// </summary>
    public List<KeyValuePair<string, string>> Cascade(XElement el)
    {
        var matched = new List<(int Specificity, int Order, Rule Rule)>();
        foreach (var rule in Rules)
        {
            var best = -1;
            foreach (var sel in rule.Selectors)
                if (sel.Matches(el)) best = Math.Max(best, sel.Specificity);
            if (best >= 0) matched.Add((best, rule.Order, rule));
        }
        var result = new List<KeyValuePair<string, string>>();
        foreach (var (_, _, rule) in matched.OrderBy(m => m.Specificity).ThenBy(m => m.Order))
            result.AddRange(rule.Declarations);
        result.AddRange(Declarations((string?)el.Attribute("style")));
        return result;
    }
}
