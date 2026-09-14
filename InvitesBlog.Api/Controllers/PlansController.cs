using InvitesBlog.Application.Plans;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvitesBlog.Api.Controllers;

/// <summary>The plans and prices, for the pricing page. Public.</summary>
[Route("api/plans")]
public sealed class PlansController(IPlanService plans) : BaseApiController
{
    [HttpGet]
    [AllowAnonymous]
    public IActionResult Catalog() => Success(plans.Catalog());
}
