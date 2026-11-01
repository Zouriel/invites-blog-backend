using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Services.Billing;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Infrastructure.Payments.GooglePlay;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvitesBlog.Api.Controllers;

/// <summary>Purchases made through Google Play in the app. Off (503, or nothing on offer) until configured.</summary>
[Route("api/billing/google-play")]
public sealed class GooglePlayController(IGooglePlayPurchases purchases, GooglePubSubTokenVerifier verifier) : BaseApiController
{
    /// <summary>What this account can buy through Play now, for one event or (with none named) for itself.</summary>
    [HttpGet("offers")]
    [HasPermission(Permissions.Campaigns.Read)]
    public async Task<IActionResult> Offers([FromQuery] Guid? eventId, CancellationToken ct) =>
        Success(await purchases.OffersAsync(eventId, ct));

    /// <summary>Hands a purchase in. Safe to repeat: it applies once. Consumed at Google only after it has.</summary>
    [HttpPost("purchases")]
    [HasPermission(Permissions.Campaigns.Read)]
    public async Task<IActionResult> Redeem([FromBody] GooglePlayPurchaseRequest request, CancellationToken ct) =>
        Success(await purchases.RedeemAsync(request, ct));

    /// <summary>Real-time Developer Notifications, by Pub/Sub push with a Google-signed token. A non-2xx is retried by Pub/Sub.</summary>
    [HttpPost("notifications")]
    [AllowAnonymous]
    public async Task<IActionResult> Notifications(CancellationToken ct)
    {
        if (!verifier.Ready)
            throw new GooglePlayUnavailableException("Notifications aren't configured here.");
        await verifier.VerifyPushAsync(Request.Headers.Authorization.ToString(), ct);
        using var reader = new StreamReader(Request.Body);
        await purchases.HandleNotificationAsync(await reader.ReadToEndAsync(ct), ct);
        return Success(new { received = true });
    }
}
