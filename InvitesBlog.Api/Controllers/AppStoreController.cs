using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Services.Billing;
using InvitesBlog.Domain.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvitesBlog.Api.Controllers;

/// <summary>Purchases made through the App Store in the iPhone app. Off (503, or nothing on offer) until configured.</summary>
[Route("api/billing/app-store")]
public sealed class AppStoreController(IAppStorePurchases purchases) : BaseApiController
{
    /// <summary>What this account can buy through the App Store now, for one event or (with none named) for itself.</summary>
    [HttpGet("offers")]
    [HasPermission(Permissions.Campaigns.Read)]
    public async Task<IActionResult> Offers([FromQuery] Guid? eventId, CancellationToken ct) =>
        Success(await purchases.OffersAsync(eventId, ct));

    /// <summary>Before a purchase: its appAccountToken, which ties it to this account and event.</summary>
    [HttpPost("intents")]
    [HasPermission(Permissions.Campaigns.Read)]
    public async Task<IActionResult> Intent([FromBody] AppStoreIntentRequest request, CancellationToken ct) =>
        Success(await purchases.IntentAsync(request, ct));

    /// <summary>Hands in a signed transaction. Safe to repeat: it applies once. The app finishes it only after "applied".</summary>
    [HttpPost("transactions")]
    [HasPermission(Permissions.Campaigns.Read)]
    public async Task<IActionResult> Redeem([FromBody] AppStoreTransactionRequest request, CancellationToken ct) =>
        Success(await purchases.RedeemAsync(request, ct));

    /// <summary>App Store Server Notifications V2. Apple signs each one; a non-2xx is retried by Apple.</summary>
    [HttpPost("notifications")]
    [AllowAnonymous]
    public async Task<IActionResult> Notifications([FromBody] AppStoreNotificationBody body, CancellationToken ct)
    {
        await purchases.HandleNotificationAsync(body.SignedPayload, ct);
        return Success(new { received = true });
    }
}

/// <summary>What Apple posts: <c>{"signedPayload": "..."}</c>.</summary>
public sealed record AppStoreNotificationBody(string? SignedPayload);
