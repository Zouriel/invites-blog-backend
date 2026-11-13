using System.Threading.RateLimiting;
using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Abstractions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection.Extensions;
using InvitesBlog.Application.Common;

namespace InvitesBlog.Api;

/// <summary>Registers the API/presentation layer: controllers, auth, RBAC policies, rate limits.</summary>
public static class DependencyInjection
{
    public static IServiceCollection AddInvitesBlogApi(this IServiceCollection services, IConfiguration config)
    {
        // Model-binding failures answer in the API's own envelope, not ProblemDetails — see
        // InvalidModelStateResponse for why the two shapes cannot coexist.
        services.AddControllers()
            .ConfigureApiBehaviorOptions(o => o.InvalidModelStateResponseFactory = Middleware.InvalidModelStateResponse.Create);
        services.AddOpenApi();
        services.AddHttpContextAccessor();
        services.AddScoped<ICurrentUser, CurrentUser>();

        // Sign in with Apple on the website (off unless OAuth:Apple:WebEnabled): its settings, read once,
        // and the one-time codes that carry a finished sign-in back into the page.
        services.AddSingleton(AppleWebSettings.From(config));
        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<Controllers.AppleWebHandoffs>();
        // invites.blog -> app.invites.blog sign-in handoff codes (60 s, one use, in memory).
        services.AddSingleton<Controllers.SessionHandoffs>();

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
        //
        // This is the TEMPLATE-image ceiling, and it stays. Event photo uploads are exempt from it
        // per-endpoint ([RequestFormLimits] + [DisableRequestSizeLimit] on the photo actions), because
        // a guest's photograph of the night is not something to refuse for being large. Raising this
        // globally instead would have quietly lifted the cap on template images too.
        services.Configure<FormOptions>(o => o.MultipartBodyLengthLimit = 24 * 1024 * 1024);

        // The server-rendered guest path (/i, /r, rsvp).
        services.AddSingleton<Rendering.RenderTickets>();

        // Same shape and the same key as the render ticket, under its own context string: what
        // somebody who scanned a bucket's QR carries while they are adding to it.
        services.AddSingleton<MediaBuckets.ContributorTickets>();
        services.AddSingleton<Photos.ArchiveTickets>();
        services.AddScoped<Rendering.RenderedInvitations>();

        // The web app at app.invites.blog calls https://invites.blog/api for Sign in with Apple's start and
        // redeem (their cookies live on the host Apple returns to), so it needs credentials across origins.
        // Named origins, never a wildcard — which is also what makes AllowCredentials safe to
        // grant. It is needed by the contributor's admission cookie: in production the app and the
        // API share an origin and CORS never enters into it, but a browser pointed at the dev server
        // is talking cross-origin and drops a Set-Cookie it was not told to expect.
        services.AddCors(o => o.AddDefaultPolicy(p => p
            .WithOrigins(new[] { config.InviterBase(), config.AppBase(), config.InviteeBase() }.Distinct().ToArray())
            .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            // The same envelope as every other refusal, so the apps can show why.
            options.OnRejected = (context, ct) =>
            {
                // The guest pages are plain forms a person is looking at, so they get a page rather
                // than an envelope only the apps can read.
                var path = context.HttpContext.Request.Path;
                if (path.StartsWithSegments("/r") || path.StartsWithSegments("/o"))
                {
                    context.HttpContext.Response.ContentType = "text/html; charset=utf-8";
                    return new ValueTask(context.HttpContext.Response.WriteAsync(Rendering.GuestPages.TooMany(), ct));
                }
                return new ValueTask(context.HttpContext.Response.WriteAsJsonAsync(
                    Application.Common.ApiResponse<object?>.Fail("Too many requests. Please wait a few minutes and try again."),
                    ct));
            };

            // Every policy is keyed on ClientAddress, never on RemoteIpAddress directly: behind
            // Cloudflare the remote address is an edge server that can change from request to request,
            // which gives every request its own quota. See ClientAddress.
            options.AddPolicy(RateLimiting.RateLimitPolicies.OtpVerify, ctx => RateLimitPartition.GetFixedWindowLimiter(
                RateLimiting.ClientAddress.PartitionKey(ctx),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = RateLimiting.RateLimitPolicies.OtpVerifyPerWindow, Window = TimeSpan.FromMinutes(10), QueueLimit = 0 }));
            options.AddPolicy("otp", ctx => RateLimitPartition.GetFixedWindowLimiter(
                RateLimiting.ClientAddress.PartitionKey(ctx),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(10) }));
            // The open link is the one lookup an anonymous caller can aim at freely, and the code
            // behind it is short by design. 59 bits is far past guessable, but a limiter is what
            // turns "not worth trying" into "not possible to try at scale".
            // Sign in with Apple on the website: start, Apple's return and the redeem are three calls per
            // sign-in, so it has its own allowance rather than spending the code-senders' five.
            options.AddPolicy(Controllers.AppleWebSignInController.RateLimitPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
                RateLimiting.ClientAddress.PartitionKey(ctx),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(10) }));
            // Moving a sign-in from invites.blog to app.invites.blog: one code and one redeem per move, so this
            // is generous for a person and still caps guessing codes (32 random bytes) at nothing useful.
            options.AddPolicy(Controllers.SessionHandoffController.RateLimitPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
                RateLimiting.ClientAddress.PartitionKey(ctx),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(10) }));
            options.AddPolicy("openlink", ctx => RateLimitPartition.GetFixedWindowLimiter(
                RateLimiting.ClientAddress.PartitionKey(ctx),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(1) }));
            // Each reply through an open guest list adds a guest. Enough for a family replying one by
            // one from one phone; not enough for a script to fill a host's list.
            // Asking for and entering the code that deletes an account. Each code already allows five
            // tries and three sends an hour; this bounds one address going through many accounts' codes.
            options.AddPolicy("account-delete", ctx => RateLimitPartition.GetFixedWindowLimiter(
                RateLimiting.ClientAddress.PartitionKey(ctx),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(10) }));
            options.AddPolicy("open-list-join", ctx => RateLimitPartition.GetFixedWindowLimiter(
                RateLimiting.ClientAddress.PartitionKey(ctx),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(10) }));
            options.AddPolicy(RateLimiting.RateLimitPolicies.CreateEvent, RateLimiting.RateLimitPolicies.CreateEventPartition);
            options.AddPolicy(RateLimiting.RateLimitPolicies.DesignAssistant, RateLimiting.RateLimitPolicies.DesignAssistantPartition);
            // The designer previews on every edit (debounced in the editor); this only stops a runaway client.
            // Direct uploads ask for one link per photo and a party can share one connection, so this is
            // about four photos a second from one address: well past a crowd, well short of a flood.
            options.AddPolicy("upload-link", ctx => RateLimitPartition.GetFixedWindowLimiter(
                RateLimiting.ClientAddress.PartitionKey(ctx),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 240, Window = TimeSpan.FromMinutes(1) }));
            options.AddPolicy("upload-progress", ctx => RateLimitPartition.GetFixedWindowLimiter(
                RateLimiting.ClientAddress.PartitionKey(ctx),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 600, Window = TimeSpan.FromMinutes(1) }));
            options.AddPolicy("design-preview", ctx => RateLimitPartition.GetFixedWindowLimiter(
                RateLimiting.ClientAddress.PartitionKey(ctx),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 240, Window = TimeSpan.FromMinutes(1) }));
            options.AddPolicy("design-assets", ctx => RateLimitPartition.GetFixedWindowLimiter(
                RateLimiting.ClientAddress.PartitionKey(ctx),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 40, Window = TimeSpan.FromMinutes(10) }));
            // Art library searches follow typing (debounced) and paging; imports download and convert a file each.
            options.AddPolicy("design-art-search", ctx => RateLimitPartition.GetFixedWindowLimiter(
                RateLimiting.ClientAddress.PartitionKey(ctx),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 90, Window = TimeSpan.FromMinutes(1) }));
            options.AddPolicy("design-art-import", ctx => RateLimitPartition.GetFixedWindowLimiter(
                RateLimiting.ClientAddress.PartitionKey(ctx),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 60, Window = TimeSpan.FromMinutes(10) }));
            // Reporting is anonymous; a limiter keeps one address from flooding the admin queue.
            // The camera's own error reports: a phone sends a handful per page at most.
            options.AddPolicy("client-errors", ctx => RateLimitPartition.GetFixedWindowLimiter(
                RateLimiting.ClientAddress.PartitionKey(ctx),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromMinutes(10) }));
            // Reports from signed-in people: plenty for anyone honest, not enough to flood the queue.
            options.AddPolicy("content-report", ctx => RateLimitPartition.GetFixedWindowLimiter(
                RateLimiting.ClientAddress.PartitionKey(ctx),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 30, Window = TimeSpan.FromHours(1) }));
            // A visitor's enquiries to studios and "Ask to remove a photo" requests: enough for a person, not a script.
            options.AddPolicy(Controllers.PortfolioPublicController.EnquiryPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
                RateLimiting.ClientAddress.PartitionKey(ctx),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromHours(1) }));
            options.AddPolicy(Controllers.PortfolioPublicController.ReportPolicy, ctx => RateLimitPartition.GetFixedWindowLimiter(
                RateLimiting.ClientAddress.PartitionKey(ctx),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromHours(1) }));
            options.AddPolicy("template-report", ctx => RateLimitPartition.GetFixedWindowLimiter(
                RateLimiting.ClientAddress.PartitionKey(ctx),
                _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromHours(1) }));
        });

        return services;
    }
}
