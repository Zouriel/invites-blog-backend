using System.Text.Json.Nodes;
using InvitesBlog.Domain.Entities;

namespace InvitesBlog.Application.Abstractions;

/// <summary>The sandbox payload for one invite: package URL + the JSON injected into the iframe (§5.3).</summary>
public sealed record InviteRenderPayload(string PackageUrl, JsonObject Data, bool RequiresOtp, string CampaignStatus);

/// <summary>
/// Builds the personalized invite render payload (resolving §12 rules server-side). Implemented in
/// Infrastructure; exposed as a port so Application services can render without referencing it.
/// </summary>
public interface IInviteRenderer
{
    InviteRenderPayload Build(
        Campaign campaign, Template template, Guest guest, Invite invite,
        string inviteLink, string? inviterName, string? inviterPhone, string? inviterEmail);
}
