using InvitesBlog.Domain.Entities;

namespace InvitesBlog.Application.Campaigns;

/// <summary>
/// The colours an event's tile falls back to when it has no picture: the design's background and
/// accent, as the guest sees them (the template's declared theme with the host's shared choices on
/// top). Only <c>#rrggbb</c> values come out, because the web puts them into an inline gradient.
/// Null when the design declared neither, so the client uses the theme's own colours.
/// </summary>
public static class PosterColours
{
    public static IReadOnlyList<string>? Of(Campaign campaign)
    {
        var theme = Services.Invites.InviteService.ThemeOf(campaign, null);
        var colours = new[] { theme.Background, theme.Accent }
            .Where(GuestColours.IsHex)
            .Select(c => c!.ToLowerInvariant())
            .Distinct()
            .ToList();
        return colours.Count == 0 ? null : colours;
    }
}
