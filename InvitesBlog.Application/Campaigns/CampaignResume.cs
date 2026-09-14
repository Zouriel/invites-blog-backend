using System.Text.Json;
using System.Text.Json.Nodes;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;

namespace InvitesBlog.Application.Campaigns;

/// <summary>
/// Where a host picks up an unfinished invitation: the first wizard step still missing something.
/// Shared by the events list and the dashboard so both send them to the same place.
/// </summary>
public static class CampaignResume
{
    /// <returns>
    /// A wizard step path (<c>roles</c>, <c>guests</c>, <c>inviter</c>, <c>delivery</c>), or null when
    /// there is nothing to finish: the invitation was sent, or the event is photos only.
    /// </returns>
    public static string? Step(Campaign campaign, bool isImported, int guestCount)
    {
        if (campaign.Status != CampaignStatus.Draft) return null;
        // No pinned design means no invitation at all. That is a finished photos-only event, not a
        // half-made invitation.
        if (string.IsNullOrWhiteSpace(campaign.TemplatePackageUrl)) return null;

        if (isImported) return campaign.InviterId is null ? "guests" : "delivery";
        if (!HasRoles(campaign.RolesJson)) return "roles";
        if (guestCount == 0) return "guests";
        if (campaign.InviterId is null) return "inviter";
        return "delivery";
    }

    private static bool HasRoles(string? rolesJson)
    {
        if (string.IsNullOrWhiteSpace(rolesJson)) return false;
        try { return JsonNode.Parse(rolesJson)?["roles"] is JsonArray { Count: > 0 }; }
        catch (JsonException) { return false; }
    }
}
