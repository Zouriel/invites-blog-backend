using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Services.Contacts;
using InvitesBlog.Domain.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace InvitesBlog.Api.Controllers;

/// <summary>Body for requesting a code to a linkable contact, named by its masked form.</summary>
public sealed record RequestContactLinkRequest(string Masked);

/// <summary>Body for proving a linkable contact.</summary>
public sealed record VerifyContactLinkRequest(Guid ChallengeId, string Code);

/// <summary>
/// Adding a second contact to an invitee's inbox — the "invited by email, signing in by phone" case.
/// Everything here is scoped to the caller's own verified identity; the service refuses any contact
/// that isn't already paired with it on a guest row, so this can't be used to probe addresses.
/// </summary>
[Route("api/me/contact-links")]
public sealed class ContactLinksController(IContactLinkService links) : BaseApiController
{
    /// <summary>Contacts the caller could add, masked, with how many invitations each would bring.</summary>
    [HttpGet]
    [HasPermission(Permissions.Inbox.Read)]
    public async Task<IActionResult> Linkable(CancellationToken ct) =>
        Success(await links.GetLinkableAsync(ct));

    // Shares the "otp" limiter: this sends real codes, so it gets the same per-caller ceiling.
    [HttpPost("request")]
    [HasPermission(Permissions.Inbox.Read)]
    [EnableRateLimiting("otp")]
    public async Task<IActionResult> RequestCode(
        [FromBody] RequestContactLinkRequest req, CancellationToken ct) =>
        Success(new { challengeId = await links.RequestLinkCodeAsync(req.Masked, ct) });

    [HttpPost("verify")]
    [HasPermission(Permissions.Inbox.Read)]
    public async Task<IActionResult> Verify(
        [FromBody] VerifyContactLinkRequest req, CancellationToken ct) =>
        Success(await links.VerifyLinkAsync(req.ChallengeId, req.Code, ct));
}
