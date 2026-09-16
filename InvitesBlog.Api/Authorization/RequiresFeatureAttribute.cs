using InvitesBlog.Application.Common;
using InvitesBlog.Application.Services.Features;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace InvitesBlog.Api.Authorization;

/// <summary>
/// Refuses the action unless the caller has the feature — released, an admin, or a listed tester.
/// Runs after authorization, so the caller is already known.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class RequiresFeatureAttribute(string feature) : TypeFilterAttribute(typeof(RequiresFeatureFilter))
{
    public string Feature { get; } = feature;
}

public sealed class RequiresFeatureFilter(IFeatureAccessService access) : IAsyncActionFilter
{
    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var attribute = context.ActionDescriptor.EndpointMetadata.OfType<RequiresFeatureAttribute>().LastOrDefault();
        if (attribute is not null && !await access.HasAsync(attribute.Feature, context.HttpContext.RequestAborted))
        {
            context.Result = new ObjectResult(ApiResponse<object?>.Fail(
                "This feature is still being tested and isn't available on your account yet."))
            { StatusCode = StatusCodes.Status403Forbidden };
            return;
        }
        await next();
    }
}
