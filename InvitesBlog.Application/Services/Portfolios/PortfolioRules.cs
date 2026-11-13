using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace InvitesBlog.Application.Services.Portfolios;

/// <summary>Handle rules (docs/PORTFOLIO.md §2): 3-30 of a-z 0-9 . _, lowercase, unique, not reserved.</summary>
public static partial class PortfolioHandles
{
    public const int Min = 3;
    public const int Max = 30;

    /// <summary>How long a changed-away handle keeps redirecting (and stays taken).</summary>
    public static readonly TimeSpan RedirectFor = TimeSpan.FromDays(30);

    /// <summary>
    /// Words nobody may take: our own names, words that would read as us, and every top-level route of
    /// invites.blog and app.invites.blog (so a handle can never be mistaken for a page of the site).
    /// </summary>
    public static readonly IReadOnlySet<string> Reserved = new HashSet<string>(StringComparer.Ordinal)
    {
        // Ours and look-alikes
        "admin", "administrator", "api", "app", "apps", "assets", "invites", "invitesblog", "invites.blog", "invite",
        "support", "help", "helpdesk", "studio", "studios", "official", "staff", "team", "moderator", "mod", "root",
        "system", "owner", "billing", "payments", "pay", "account", "accounts", "settings", "security", "status",
        "blog", "news", "press", "mail", "email", "www", "web", "static", "cdn", "media", "portfolio", "portfolios",
        "about", "contact", "jobs", "careers", "legal", "abuse", "report", "null", "undefined", "anonymous",
        // Top-level routes of the website and the app (projects/web-inviter app.routes.ts), and the API's own pages
        "bring-your-own", "buckets", "child-safety", "create", "dashboard", "delete-account", "design", "designer",
        "events", "example-event", "favourites", "feed", "guide", "handoff", "inbox", "inquire", "invitation",
        "invitations", "join", "login", "logout", "me", "my-templates", "oauth", "pricing", "plans", "privacy", "q",
        "refunds", "signup", "sign-in", "sign-up", "template-designer", "template-guide", "templates", "terms",
        "i", "o", "r", "h", "health", "openapi", "well-known", ".well-known", "sitemap", "robots", "favicon",
    };

    public static string Normalize(string? raw) => (raw ?? string.Empty).Trim().TrimStart('@').ToLowerInvariant();

    [GeneratedRegex("^[a-z0-9._]+$")]
    private static partial Regex Allowed();

    /// <summary>Null when the handle's shape is fine; else what is wrong, in plain words.</summary>
    public static string? ShapeProblem(string handle)
    {
        if (handle.Length < Min) return $"Use at least {Min} letters or numbers.";
        if (handle.Length > Max) return $"Use at most {Max} characters.";
        if (!Allowed().IsMatch(handle)) return "Use only letters, numbers, dots and underscores.";
        if (handle.StartsWith('.') || handle.EndsWith('.')) return "A dot can't be first or last.";
        if (handle.Contains("..")) return "Use one dot at a time.";
        if (handle.All(char.IsDigit)) return "Add at least one letter.";
        return null;
    }

    public static bool IsReserved(string handle) =>
        Reserved.Contains(handle) || Reserved.Contains(handle.Replace(".", "").Replace("_", ""))
        || handle.Replace(".", "").Replace("_", "").Contains("invitesblog");

    /// <summary>A handle made from a name: accents dropped, spaces to dots, cut to fit. Never empty.</summary>
    public static string FromName(string? name)
    {
        var plain = new StringBuilder();
        foreach (var c in (name ?? string.Empty).Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            var l = char.ToLowerInvariant(c);
            if (l is >= 'a' and <= 'z' or >= '0' and <= '9') plain.Append(l);
            else if ((char.IsWhiteSpace(l) || l is '.' or '_' or '-' or '&') && plain.Length > 0 && plain[^1] != '.') plain.Append('.');
        }
        var s = plain.ToString().Trim('.');
        if (s.Length > Max) s = s[..Max].Trim('.');
        if (s.Length < Min) s = (s + "studio").Length <= Max ? s + "studio" : "studio";
        if (s == "studio") s = "my.studio";
        return s;
    }
}

/// <summary>A theme as the picker shows it: the colours a preview is drawn from.</summary>
public sealed record PortfolioThemeChoice(string Key, string Label, bool Dark, string Bg, string Surface, string Text, string Muted, string Accent);

public sealed record PortfolioAccentChoice(string Key, string Label, string? Color);

public sealed record PortfolioFontChoice(string Key, string Label, string Heading, string Body, string Sample);

public sealed record PortfolioNamedChoice(string Key, string Label);

public sealed record PortfolioSectionKindChoice(string Kind, string Label, bool Multiple);

public sealed record PortfolioOptionsDto(
    IReadOnlyList<PortfolioThemeChoice> Themes,
    IReadOnlyList<PortfolioAccentChoice> Accents,
    IReadOnlyList<PortfolioFontChoice> Fonts,
    IReadOnlyList<PortfolioNamedChoice> Layouts,
    IReadOnlyList<PortfolioSectionKindChoice> SectionKinds);

/// <summary>
/// The look, as named choices (plain-defaults rule: no colour codes to type). Theme colours are the UI library's
/// theme tokens (projects/ui/styles), so the page matches the app's themes, oxblood first.
/// </summary>
public static class PortfolioChoices
{
    public static readonly IReadOnlyList<PortfolioThemeChoice> Themes =
    [
        new("oxblood", "Oxblood", false, "#F2EADB", "#F8F3EA", "#1C1C1F", "#55504A", "#7A0C1E"),
        new("oxblood-dark", "Oxblood night", true, "#1C1C1F", "#232326", "#F2EADB", "#BDB4A6", "#F58A99"),
        new("paper", "Paper", false, "#FFFFFF", "#F5F5F4", "#18181B", "#52525B", "#18181B"),
        new("cocoa", "Cocoa", false, "#EEE4D6", "#F6EFE5", "#1F1612", "#5A4A40", "#B3102E"),
        new("cocoa-dark", "Cocoa night", true, "#1F1612", "#271C17", "#EEE4D6", "#BFAE9E", "#FF7D93"),
        new("maldives", "Maldives", false, "#F3F8FB", "#FFFFFF", "#0B2D3A", "#365F6E", "#0B2D3A"),
        new("maldives-dark", "Maldives night", true, "#071B23", "#0D2731", "#E6F1F3", "#A3BFC8", "#6CC0D4"),
        new("glacier", "Glacier", false, "#F1F3F4", "#F8FBFC", "#0E1A24", "#475867", "#B30022"),
        new("rugged", "Sand", false, "#D8C3A0", "#E6D7BC", "#1E2015", "#4A4632", "#5C4600"),
        new("graphite-dark", "Graphite", true, "#1A1B1F", "#212226", "#ECEDEF", "#AEB2BC", "#FF8A4C"),
        new("violet-dark", "Violet night", true, "#111316", "#181A1E", "#E9ECEF", "#AAB0B9", "#AC92FF"),
    ];

    public static readonly IReadOnlyList<PortfolioAccentChoice> Accents =
    [
        new("theme", "Theme colour", null),
        new("gold", "Gold", "#B8892F"),
        new("rose", "Rose", "#C2577A"),
        new("sage", "Sage", "#5E7D5B"),
        new("ocean", "Ocean", "#2D6E8E"),
        new("plum", "Plum", "#7A3E6E"),
        new("charcoal", "Charcoal", "#2B2B2E"),
    ];

    public static readonly IReadOnlyList<PortfolioFontChoice> Fonts =
    [
        new("classic", "Classic", "Playfair Display", "Inter", "Aa"),
        new("elegant", "Elegant", "Cormorant Garamond", "Lato", "Aa"),
        new("modern", "Modern", "Poppins", "Poppins", "Aa"),
        new("editorial", "Editorial", "DM Serif Display", "DM Sans", "Aa"),
        new("simple", "Simple", "system-ui", "system-ui", "Aa"),
    ];

    public static readonly IReadOnlyList<PortfolioNamedChoice> Layouts =
        [new("grid", "Grid"), new("masonry", "Mosaic"), new("slideshow", "Slideshow")];

    public static readonly IReadOnlyList<PortfolioSectionKindChoice> SectionKinds =
    [
        new(PortfolioSectionKinds.Collection, "Collection of photos", true),
        new(PortfolioSectionKinds.About, "About", false),
        new(PortfolioSectionKinds.Designs, "Invitation designs", false),
        new(PortfolioSectionKinds.Packages, "Packages & prices", false),
        new(PortfolioSectionKinds.Reviews, "Reviews", false),
        new(PortfolioSectionKinds.Contact, "Contact", false),
    ];

    public static readonly PortfolioOptionsDto Options = new(Themes, Accents, Fonts, Layouts, SectionKinds);

    public static PortfolioThemeChoice Theme(string? key) => Themes.FirstOrDefault(t => t.Key == key) ?? Themes[0];
    public static PortfolioFontChoice Font(string? key) => Fonts.FirstOrDefault(f => f.Key == key) ?? Fonts[0];

    /// <summary>The colour buttons and links use: the chosen accent, or the theme's own.</summary>
    public static string AccentColour(string? theme, string? accent) =>
        Accents.FirstOrDefault(a => a.Key == accent)?.Color ?? Theme(theme).Accent;
}

public static class PortfolioSectionKinds
{
    public const string Cover = "cover";
    public const string Collection = "collection";
    public const string About = "about";
    public const string Designs = "designs";
    public const string Packages = "packages";
    public const string Reviews = "reviews";
    public const string Contact = "contact";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string> { Cover, Collection, About, Designs, Packages, Reviews, Contact };

    /// <summary>Kinds a studio adds through POST sections (a collection comes with its collection; the cover always exists).</summary>
    public static readonly IReadOnlySet<string> Addable = new HashSet<string> { About, Designs, Packages, Reviews, Contact };
}
