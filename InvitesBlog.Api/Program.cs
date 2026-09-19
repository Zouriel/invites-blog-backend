using InvitesBlog.Api;
using InvitesBlog.Api.Middleware;
using InvitesBlog.Infrastructure;
using InvitesBlog.Infrastructure.Notifications;
using InvitesBlog.Infrastructure.Seed;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInvitesBlogInfrastructure(builder.Configuration);
builder.Services.AddInvitesBlogApi(builder.Configuration);
// Tells an event's people about new photos, once per quiet period. Registered here rather than in the
// worker because the worker is not deployed; it must stay registered in exactly one host, or the
// digest goes out twice. Dormant unless Notifications:PhotoDigest:Enabled is true.
builder.Services.AddHostedService<PhotoDigestService>();
// Emails organisers as an event's photo cover runs out, and removes the photos 90 days after.
builder.Services.AddHostedService<MediaRetentionService>();
builder.Services.AddHostedService<DesignerAccessSweeper>();

var app = builder.Build();

// There is no real payment provider yet, so production runs on the Fake one on purpose. Said out loud
// at every start so it is never mistaken for a working checkout: with it, "paid" means somebody
// pressed a button. The dev checkout pages that press it are refused outside Development.
if (app.Environment.IsProduction()
    && app.Services.GetRequiredService<InvitesBlog.Application.Abstractions.IPaymentProvider>().Name == "Fake")
    app.Logger.LogWarning(
        "Payments are using the Fake provider in Production. No real payment is taken; replace it before charging anyone.");

// Production: the API is never internet-reachable directly — only the shared Caddy container can
// reach it, over an internal Docker network (see deploy/compose.prod.yml, no published port on the
// api service). That makes Caddy the one trusted hop, so it's safe to accept whatever X-Forwarded-For
// it sets (Caddy always sets it) without a fixed KnownProxies/KnownNetworks allowlist — the container
// network boundary IS the trust boundary here. Without this, HttpContext.Connection.RemoteIpAddress
// would resolve to Caddy's own address for every request, which silently breaks anything keyed on the
// real client IP (today: the OTP/resend rate limiters; also the personal-link IP-trust feature).
var forwardedHeaders = new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
};
// KnownIPNetworks replaces KnownNetworks, which is obsolete in .NET 10.
forwardedHeaders.KnownIPNetworks.Clear();
forwardedHeaders.KnownProxies.Clear();
// This yields the address CADDY saw. When Cloudflare sits in front of Caddy that is an edge server,
// not the visitor — anything keyed on the visitor must go through RateLimiting.ClientAddress.
app.UseForwardedHeaders(forwardedHeaders);

app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseCors();
app.UseAuthentication();
// After authentication, so a limit can tell a signed-in account from an anonymous visitor (the
// create-event policy gives accounts a larger allowance). The address-keyed policies are unaffected.
app.UseRateLimiter();
app.UseAuthorization();

// Serve compiled template packages / assets locally at /assets (assets.invites.blog in prod).
var assetsRoot = Path.Combine(AppContext.BaseDirectory, "wwwroot", "assets");
Directory.CreateDirectory(assetsRoot);
app.UseStaticFiles(new StaticFileOptions
{
    FileProvider = new Microsoft.Extensions.FileProviders.PhysicalFileProvider(assetsRoot),
    RequestPath = "/assets",
    OnPrepareResponse = ctx =>
    {
        // Invitations render on an opaque origin (sandboxed), and fonts are fetched in CORS mode, so
        // the designer's fonts need this even on the same host. Production sets it in Caddy.
        if (ctx.File.Name.EndsWith(".woff2"))
            ctx.Context.Response.Headers["Access-Control-Allow-Origin"] = "*";
        if (ctx.File.Name.EndsWith(".html"))
            ctx.Context.Response.Headers["Content-Security-Policy"] =
                "default-src 'none'; script-src 'unsafe-inline' 'self'; style-src 'unsafe-inline' 'self'; img-src 'self' data:; font-src 'self' data:; base-uri 'none'; form-action 'none'";
    }
});

app.MapOpenApi();
app.MapGet("/", () => Results.Ok(new { service = "invites.blog API", status = "ok", docs = "/openapi/v1.json" }));
app.MapGet("/health", () => Results.Ok(new { status = "healthy" }));
app.MapControllers();

// Dev: apply migrations + seed RBAC + gallery on startup.
using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    try
    {
        await services.GetRequiredService<TemplateSeeder>().SeedAsync();
        await services.GetRequiredService<RawTemplateSeeder>().SeedAsync();
        await services.GetRequiredService<TemplateManifestRefresher>().RefreshAsync();
        await services.GetRequiredService<TemplateTypeSeeder>().SeedAsync();
        await services.GetRequiredService<RbacSeeder>().SeedAsync();
        await services.GetRequiredService<DesignFontSeeder>().SeedAsync();
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Database not reachable at startup — skipping migrate/seed.");
    }
}

app.Run();

public partial class Program { }
