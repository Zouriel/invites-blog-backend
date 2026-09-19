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

    /// <summary>Starts paying for one item. While online payment is off, says so instead.</summary>
    [HttpPost("checkout")]
    [HasPermission(Permissions.Campaigns.Read)]
    public async Task<IActionResult> Checkout([FromBody] CheckoutRequest req, CancellationToken ct) =>
        Success(await billing.CheckoutAsync(req, ct));
}
