using System.Net;
using InvitesBlog.Application.Dtos.Invites;

namespace InvitesBlog.Api.Rendering;

/// <summary>
/// The credit line a guest sees: "Made with invites.blog" on a Free event, the Studio designer who
/// made the invitation, the venue whose albums they are. Kept small and out of the way — it is a
/// signature, not an advert — and every value is encoded on the way in.
/// </summary>
public static class GuestCreditHtml
{
    /// <summary>Where the "Made with" link goes, tagged so the landing page can tell who came from an invitation.</summary>
    public const string HomeUrl = "https://invites.blog/?ref=invitation";

    /// <summary>
    /// The credit for an invitation: a small pill pinned to the bottom corner, added just before
    /// <c>&lt;/body&gt;</c>. The invitation is the template's own document, so this is the one thing
    /// the server adds to it; inline styles only, which its policy allows.
    /// </summary>
    public static string InjectIntoInvitation(string html, GuestCredit? credit)
    {
        if (credit is null || (!credit.MadeWith && credit.DesignedBy is null)) return html;
        var parts = new List<string>();
        if (credit.DesignedBy is { } designer) parts.Add($"Designed by {E(designer)}");
        if (credit.MadeWith)
            parts.Add($"""<a href="{E(HomeUrl)}" target="_blank" rel="noopener" style="color:inherit;text-decoration:none;font-weight:600">Made with invites.blog</a>""");

        var pill = $"""<div data-ib-credit style="position:fixed;right:10px;bottom:10px;z-index:2147483000;font:500 11px/1.2 system-ui,-apple-system,sans-serif;letter-spacing:.01em;color:#fff;background:rgba(20,20,20,.55);-webkit-backdrop-filter:blur(6px);backdrop-filter:blur(6px);padding:5px 10px;border-radius:999px;pointer-events:auto">{string.Join(" · ", parts)}</div>""";

        var at = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return at < 0 ? html + pill : html.Insert(at, pill);
    }

    /// <summary>The credit for the guests' album page, as a line of its own at the foot.</summary>
    public static string AlbumFooter(GuestCredit? credit)
    {
        if (credit is null || credit.IsEmpty) return "";
        var parts = new List<string>();
        if (credit.VenueName is { } venue) parts.Add($"Photos at {E(venue)}");
        if (credit.MadeWith) parts.Add($"""<a href="{E(HomeUrl)}" target="_blank" rel="noopener">Made with invites.blog</a>""");
        return parts.Count == 0 ? "" : $"""<p class="credit">{string.Join(" · ", parts)}</p>""";
    }

    /// <summary>The venue's name and logo at the head of the guests' album page.</summary>
    public static string AlbumHeader(GuestCredit? credit)
    {
        if (credit?.VenueName is not { } venue) return "";
        var logo = credit.VenueLogoUrl is { } url ? $"""<img src="{E(url)}" alt="" class="venue__logo">""" : "";
        return $"""<div class="venue">{logo}<span>{E(venue)}</span></div>""";
    }

    private static string E(string s) => WebUtility.HtmlEncode(s);
}
