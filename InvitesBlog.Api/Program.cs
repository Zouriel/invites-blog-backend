using InvitesBlog.Api;
using InvitesBlog.Api.Middleware;
using InvitesBlog.Infrastructure;
using InvitesBlog.Infrastructure.Seed;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddInvitesBlogInfrastructure(builder.Configuration);
builder.Services.AddInvitesBlogApi(builder.Configuration);

var app = builder.Build();

app.UseMiddleware<ExceptionHandlingMiddleware>();
app.UseCors();
app.UseRateLimiter();
app.UseAuthentication();
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
    }
    catch (Exception ex)
    {
        app.Logger.LogWarning(ex, "Database not reachable at startup — skipping migrate/seed.");
    }
}

app.Run();

public partial class Program { }
