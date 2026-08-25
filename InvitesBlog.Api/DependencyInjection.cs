using System.Threading.RateLimiting;
using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace InvitesBlog.Api;

/// <summary>Registers the API/presentation layer: controllers, auth, RBAC policies, rate limits.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInvitesBlogApi(this IServiceCollection services, IConfiguration config)
    {
        services.AddControllers();
        services.AddOpenApi();
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, CurrentUser>();

        // Every request is resolved into a permissioned principal by our scheme (full-RBAC model).
        services.AddAuthentication(AppAuthenticationHandler.SchemeName)
            .AddScheme<AuthenticationSchemeOptions, AppAuthenticationHandler>(AppAuthenticationHandler.SchemeName, null);

        // Permission policies are built on demand for any [HasPermission("x")].
        services.AddAuthorization();
        // Replace the default provider so any perm:* policy is built on demand.
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();

        // Sits ABOVE the 20 MB image cap on purpose: this bounds the whole multipart envelope —
        // boundaries, headers and any other form fields — so a file exactly at the cap would trip a
        // limit set to the same number, and the caller would see a protocol error instead of the
        // "Images must be 20 MB or smaller." message the service is there to give them.
        services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 24 * 1024 * 1024);

        services.AddCors(o => o.AddDefaultPolicy(p => p
            .WithOrigins(
                config["Urls:InviterBase"] ?? "http://localhost:4200",
                config["Urls:InviteeBase"] ?? "http://localhost:4201")
            .AllowAnyHeader().AllowAnyMethod()));

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("otp", ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(10) }));
            options.AddPolicy("resend", ctx => RateLimitPartition.GetFixedWindowLimiter(
                ctx.Connection.RemoteIpAddress?.ToString() ?? "anon",
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(60) }));
        });

        return services;
    }
}
