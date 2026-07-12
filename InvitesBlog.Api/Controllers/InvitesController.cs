using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Dtos.Invites;
using InvitesBlog.Application.Services.Invites;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Infrastructure.Rendering;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvitesBlog.Api.Controllers;

/// <summary>§10.8 Invitee: token view (no login), RSVP, inbox, claim. Thin controller — delegates to
/// <see cref="IInviteService"/>. It also bridges the Infrastructure <see cref="InviteRenderService"/>
/// into the service (the Application layer cannot reference Infrastructure).</summary>
[Route("api/invites")]
public sealed class InvitesController(IInviteService invites, InviteRenderService renderer) : BaseApiController
{
    [HttpGet("by-token/{token}")]
    [AllowAnonymous]
    [HasPermission(Permissions.Invites.View)]
    public async Task<IActionResult> GetByToken(string token, CancellationToken ct) =>
        Success(await invites.GetByTokenAsync(token, Render, ct));

    [HttpPost("by-token/{token}/rsvp")]
    [AllowAnonymous]
    [HasPermission(Permissions.Invites.Rsvp)]
    public async Task<IActionResult> Rsvp(string token, [FromBody] RsvpRequest req, CancellationToken ct) =>
        Success(await invites.RsvpAsync(token, req, ct));

    // Authenticated RSVP from the inbox (ownership-checked by verified contact).
    [HttpPost("{inviteId:guid}/rsvp")]
    [HasPermission(Permissions.Inbox.Read)]
    public async Task<IActionResult> RsvpByInviteId(Guid inviteId, [FromBody] RsvpRequest req, CancellationToken ct) =>
        Success(await invites.RsvpByInviteIdAsync(inviteId, req, ct));

    [HttpGet("/api/me/invites")]
    [HasPermission(Permissions.Inbox.Read)]
    public async Task<IActionResult> Inbox(CancellationToken ct) =>
        Success(await invites.GetInboxAsync(ct));

    // Claim by possession of the raw token (not by invite id — see ClaimAsync).
    [HttpPost("by-token/{token}/claim")]
    [HasPermission(Permissions.Invites.Claim)]
    public async Task<IActionResult> Claim(string token, CancellationToken ct) =>
        Success(await invites.ClaimAsync(token, ct));

    // Bridges the Infrastructure renderer into the Application service without a layer dependency.
    private InviteRenderData Render(
        Campaign campaign, Template template, Guest guest, Invite invite, string inviteLink,
        string? inviterName, string? inviterPhone, string? inviterEmail)
    {
        var p = renderer.Build(campaign, template, guest, invite, inviteLink, inviterName, inviterPhone, inviterEmail);
        return new InviteRenderData(p.PackageUrl, p.Data, p.RequiresOtp, p.CampaignStatus);
    }
}
