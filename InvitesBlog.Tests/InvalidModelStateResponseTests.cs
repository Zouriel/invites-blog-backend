using InvitesBlog.Api;
using InvitesBlog.Api.Middleware;
using InvitesBlog.Application.Common;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>
/// A request that fails model binding must come back in the envelope every other failure uses. The
/// apps read <c>errors</c> as an array; ASP.NET's own answer makes it an object, and their error
/// handling could not read it.
/// </summary>
public class InvalidModelStateResponseTests
{
    private static ActionContext WithErrors(params (string Key, string Message)[] errors)
    {
        var state = new ModelStateDictionary();
        foreach (var (key, message) in errors) state.AddModelError(key, message);
        return new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor(), state);
    }

    [Fact]
    public void A_binding_failure_is_the_envelope_with_an_array_of_errors_and_the_validation_status()
    {
        var result = InvalidModelStateResponse.Create(WithErrors(
            ("Email", "The Email field is required."),
            ("$.eventDate", "The JSON value could not be converted.")));

        var objectResult = Assert.IsType<UnprocessableEntityObjectResult>(result);
        // 422: the status FluentValidation failures already use, so both paths read the same.
        Assert.Equal(StatusCodes.Status422UnprocessableEntity, objectResult.StatusCode);

        var body = Assert.IsType<ApiResponse<object?>>(objectResult.Value);
        Assert.False(body.Success);
        Assert.Equal("Validation failed.", body.Message);
        Assert.NotNull(body.Errors);
        Assert.Contains(body.Errors!, e => e is { Field: "Email", Message: "The Email field is required." });
        // A JSON path is reported as the property it names.
        Assert.Contains(body.Errors!, e => e.Field == "eventDate");
    }

    [Fact]
    public void An_unreadable_body_names_no_field()
    {
        var result = (ObjectResult)InvalidModelStateResponse.Create(WithErrors(("$", "Invalid JSON.")));

        var error = Assert.Single(((ApiResponse<object?>)result.Value!).Errors!);
        Assert.Null(error.Field);
        Assert.Equal("Invalid JSON.", error.Message);
    }

    /// <summary>The factory is only worth anything if the API actually uses it.</summary>
    [Fact]
    public void The_api_is_configured_to_use_it()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddInvitesBlogApi(new ConfigurationBuilder().Build());

        var options = services.BuildServiceProvider().GetRequiredService<IOptions<ApiBehaviorOptions>>().Value;

        var result = options.InvalidModelStateResponseFactory(WithErrors(("Title", "Required.")));
        Assert.IsType<UnprocessableEntityObjectResult>(result);
    }
}
