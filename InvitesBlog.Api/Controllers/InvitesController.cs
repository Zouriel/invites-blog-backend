using Microsoft.Extensions.Configuration;
using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Dtos.Invites;
using InvitesBlog.Application.Dtos.Otp;
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
public sealed class InvitesController(
    IInviteService invites, InviteRenderService renderer,
    InvitesBlog.Api.Rendering.RenderTickets tickets, IConfiguration config) : BaseApiController
{
    [HttpGet("by-token/{token}")]
    [AllowAnonymous]
    [HasPermission(Permissions.Invites.View)]
    public async Task<IActionResult> GetByToken(string token, CancellationToken ct) =>
        Success(await invites.GetByTokenAsync(token, ClientIp(), Render, ct));

    [HttpPost("by-token/{token}/rsvp")]
    [AllowAnonymous]
    [HasPermission(Permissions.Invites.Rsvp)]
    public async Task<IActionResult> Rsvp(string token, [FromBody] RsvpRequest req, CancellationToken ct) =>
        Success(await invites.RsvpAsync(token, ClientIp(), req, ct));

    // Personal-link IP binding (§ personal invite links): the frontend calls these two when
    // GetByToken comes back requires-OTP because this open is from an IP not yet trusted for the
    // invite. No contact is taken from the caller — the link is already user-bound, so the code goes
    // to whatever contact the guest row itself has on file.
    [HttpPost("by-token/{token}/reauth/request")]
    [AllowAnonymous]
    [HasPermission(Permissions.Invites.View)]
    public async Task<IActionResult> RequestReauth(string token, CancellationToken ct) =>
        Success(await invites.RequestReauthAsync(token, ct));

    [HttpPost("by-token/{token}/reauth/verify")]
    [AllowAnonymous]
    [HasPermission(Permissions.Invites.View)]
    public async Task<IActionResult> VerifyReauth(string token, [FromBody] VerifyOtpRequest req, CancellationToken ct) =>
        Success(await invites.VerifyReauthAsync(token, ClientIp(), req, Render, ct));

    // Authenticated RSVP from the inbox (ownership-checked by verified contact).
    [HttpPost("{inviteId:guid}/rsvp")]
    [HasPermission(Permissions.Inbox.Read)]
    public async Task<IActionResult> RsvpByInviteId(Guid inviteId, [FromBody] RsvpRequest req, CancellationToken ct) =>
        Success(await invites.RsvpByInviteIdAsync(inviteId, req, ct));

    [HttpGet("/api/me/invites")]
    [HasPermission(Permissions.Inbox.Read)]
    public async Task<IActionResult> Inbox(CancellationToken ct) =>
        Success(await invites.GetInboxAsync(ct));

    // The signed-in caller's own invitation to a campaign, if their verified email or phone is on the
    // guest list. Lives under /api/me/ rather than /api/campaigns/ on purpose: everything under
    // /api/campaigns/{id} carries the campaign POSSESSION token (the inviter's key), and an invitation
    // you RECEIVED is authorised by your account instead. Sharing the prefix meant the wrong bearer.
    [HttpGet("/api/me/invitations/{campaignId:guid}")]
    [HasPermission(Permissions.Inbox.Read)]
    public async Task<IActionResult> MyInvite(Guid campaignId, CancellationToken ct) =>
        Success(await invites.GetMyInviteAsync(campaignId, Render, ct));

    /// <summary>
    /// Hands the signed-in caller over to their server-rendered invitation. Returns the URL to
    /// navigate to; the render host redeems the one-hop admission in it and turns it into a cookie.
    ///
    /// A handoff rather than a cookie set directly, because the app holding the session and the app
    /// rendering the invitation are different origins, and a cookie's Domain may name the setting
    /// host or a parent — never a sibling. It lives 60 seconds, exists only inside one redirect, and
    /// never reaches the rendered document.
    /// </summary>
    [HttpGet("/api/me/invitations/{campaignId:guid}/render-link")]
    [HasPermission(Permissions.Inbox.Read)]
    public async Task<IActionResult> RenderLink(Guid campaignId, CancellationToken ct)
    {
        var inviteId = await invites.ResolveMyInviteIdAsync(campaignId, ct);
        var renderBase = (config["Urls:InviteeBase"] ?? "https://me.invites.blog").TrimEnd('/');
        return Success(new { url = $"{renderBase}/h/{tickets.IssueHandoff(inviteId, DateTimeOffset.UtcNow)}" });
    }

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

    // Requires Program.cs's ForwardedHeaders middleware to have already rewritten
    // HttpContext.Connection.RemoteIpAddress from the Caddy hop's own IP to the real client IP.
    private string? ClientIp() => HttpContext.Connection.RemoteIpAddress?.ToString();
}
