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
    /// A wizard step path (<c>roles</c>, <c>guests</c>, <c>inviter</c>, <c>photos</c> — the plan step),
    /// or null when there is nothing to finish: the event is live.
    /// </returns>
    public static string? Step(Campaign campaign, bool isImported, int guestCount)
    {
        if (campaign.Status != CampaignStatus.Draft) return null;
        // No design yet. A save the date needs one (null: its page offers "Choose a design"); any
        // other event can finish as photos only, at the plan step.
        if (string.IsNullOrWhiteSpace(campaign.TemplatePackageUrl))
            return campaign.Kind == CampaignKind.SaveTheDate ? null : "photos";

        // The plan step ("photos") comes last before Share: a pass is chosen, and paid for, before
        // anything is sent.
        if (isImported) return campaign.InviterId is null ? "guests" : "photos";
        // A save the date has no roles step: there is nothing for a role to decide yet.
        if (campaign.Kind == CampaignKind.SaveTheDate)
            return guestCount == 0 ? "guests" : campaign.InviterId is null ? "inviter" : "photos";
        if (!HasRoles(campaign.RolesJson)) return "roles";
        if (guestCount == 0) return "guests";
        if (campaign.InviterId is null) return "inviter";
        return "photos";
    }

    private static bool HasRoles(string? rolesJson)
    {
        if (string.IsNullOrWhiteSpace(rolesJson)) return false;
        try { return JsonNode.Parse(rolesJson)?["roles"] is JsonArray { Count: > 0 }; }
        catch (JsonException) { return false; }
    }
}
