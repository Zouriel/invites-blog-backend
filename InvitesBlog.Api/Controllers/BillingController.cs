using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Services.Billing;
using InvitesBlog.Domain.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvitesBlog.Api.Controllers;

/// <summary>The signed-in account's billing: what it's on, what it can buy, and what it has paid.</summary>
[Route("api/billing")]
public sealed class BillingController(IBillingService billing) : BaseApiController
{
    [HttpGet]
    [HasPermission(Permissions.Campaigns.Read)]
    public async Task<IActionResult> Get(CancellationToken ct) => Success(await billing.GetAsync(ct));

    [HttpGet("events/{campaignId:guid}")]
    [HasPermission(Permissions.Campaigns.Read)]
    public async Task<IActionResult> Event(Guid campaignId, CancellationToken ct) => Success(await billing.EventAsync(campaignId, ct));

    /// <summary>One of the buyer's payments, checked with the gateway if still pending: the page after paying reads this.</summary>
    [HttpGet("payments/{paymentId:guid}")]
    [HasPermission(Permissions.Campaigns.Read)]
    public async Task<IActionResult> Payment(Guid paymentId, CancellationToken ct) =>
        Success(await billing.PaymentStatusAsync(paymentId, ct));

    /// <summary>Stops the plan renewing by itself.</summary>
    [HttpPost("auto-renew/stop")]
    [HasPermission(Permissions.Campaigns.Read)]
    public async Task<IActionResult> StopAutoRenew(CancellationToken ct) => Success(await billing.StopAutoRenewAsync(ct));

    /// <summary>What an item would cost and what it is: the review step shown before paying.</summary>
    [HttpPost("quote")]
    [HasPermission(Permissions.Campaigns.Read)]
    public async Task<IActionResult> Quote([FromBody] CheckoutRequest req, CancellationToken ct) =>
        Success(await billing.QuoteAsync(req, ct));

    /// <summary>Starts paying for one item, once its terms are accepted. While online payment is off, says so instead.</summary>
    [HttpPost("checkout")]
    [HasPermission(Permissions.Campaigns.Read)]
    public async Task<IActionResult> Checkout([FromBody] CheckoutRequest req, CancellationToken ct) =>
        Success(await billing.CheckoutAsync(req, ct));
}
