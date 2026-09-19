using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;

namespace InvitesBlog.Application.Campaigns;

/// <summary>The few facts about save the dates that more than one service needs.</summary>
public static class SaveTheDates
{
    /// <summary>The gallery category (a TemplateType) whose designs start a save the date.</summary>
    public const string Category = "Save the Date";

    public static bool Is(Campaign c) => c.Kind == CampaignKind.SaveTheDate;

    /// <summary>"saveTheDate" or "invitation", as the API spells kinds; anything else is null.</summary>
    public static CampaignKind? Parse(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "savethedate" or "save-the-date" or "save_the_date" => CampaignKind.SaveTheDate,
        "invitation" => CampaignKind.Invitation,
        _ => null,
    };

    public static string Name(CampaignKind kind) => kind == CampaignKind.SaveTheDate ? "saveTheDate" : "invitation";

    /// <summary>Said by every door a save the date doesn't have.</summary>
    public const string NoAlbumMessage = "A save the date has no photo album. Photos come with the invitation.";
    public const string NoRepliesMessage = "A save the date doesn't take replies. The invitation will ask.";
}

/// <summary>The calendar entry a guest saves: the event's name, day (and time when known) and place.</summary>
public static class EventCalendar
{
    public static Events.CalendarEntry For(Campaign campaign, string? url)
    {
        string? venueName = null, city = null, title = null;
        try
        {
            var content = System.Text.Json.Nodes.JsonNode.Parse(
                string.IsNullOrWhiteSpace(campaign.CustomContentJson) ? "{}" : campaign.CustomContentJson) as System.Text.Json.Nodes.JsonObject;
            title = Text(content?["title"]);
            if (content?["venue"] is System.Text.Json.Nodes.JsonObject venue)
            {
                venueName = Text(venue["name"]);
                city = Text(venue["city"]);
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // Unreadable content: the entry still has the event's name and day.
        }

        var place = string.Join(", ", new[] { venueName, city }.Where(p => !string.IsNullOrWhiteSpace(p)));
        return new Events.CalendarEntry(
            Title: title ?? campaign.Title,
            Start: campaign.EventStartAt,
            End: campaign.AllDay ? null : campaign.EventEndAt,
            AllDay: campaign.AllDay,
            Location: place.Length == 0 ? null : place,
            Details: SaveTheDates.Is(campaign) ? "Save the date. The invitation will follow." : null,
            Uid: $"{campaign.Id:N}@invites.blog",
            Url: url);
    }

    private static string? Text(System.Text.Json.Nodes.JsonNode? node)
    {
        try { return node?.GetValue<string>() is { Length: > 0 } s ? s.Trim() : null; }
        catch (InvalidOperationException) { return null; }
    }
}
