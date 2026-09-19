using InvitesBlog.Application.Plans;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvitesBlog.Api.Controllers;

/// <summary>The plans and prices, for the pricing page. Public.</summary>
[Route("api/plans")]
public sealed class PlansController(IPriceBook prices) : BaseApiController
{
    [HttpGet]
    [AllowAnonymous]
    public async Task<IActionResult> Catalog(CancellationToken ct) =>
        Success(PlanCatalog.Describe(await prices.CurrentAsync(ct)));
}
