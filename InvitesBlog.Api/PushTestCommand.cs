using InvitesBlog.Application.Abstractions;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Infrastructure;
using InvitesBlog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Api;

/// <summary>
/// Sends one test notification to every phone an account has registered, from inside the api container:
/// <c>docker exec invites-blog-api dotnet InvitesBlog.Api.dll send-test-push someone@example.com</c>.
/// No web endpoint, so nobody outside the server can trigger it. Builds the services without starting
/// the host, so no migrations, workers or seeders run beside the live API.
/// </summary>
public static class PushTestCommand
{
    public const string Name = "send-test-push";
    public const string Title = "invites.blog";
    public const string Body = "Test notification: notifications are working on this phone.";
    public const string Path = "/inbox";

    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length != 2 || string.IsNullOrWhiteSpace(args[1]))
        {
            Console.Error.WriteLine($"Usage: dotnet InvitesBlog.Api.dll {Name} <account email>");
            return 2;
        }

        var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
        {
            ContentRootPath = AppContext.BaseDirectory,
            EnvironmentName = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ?? Environments.Production,
        });
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.Services.AddInvitesBlogInfrastructure(builder.Configuration);
        using var host = builder.Build();
        using var scope = host.Services.CreateScope();
        return await SendAsync(scope.ServiceProvider, args[1], Console.Out);
    }

    /// <summary>The part tests run: find the account, say what it has, send.</summary>
    public static async Task<int> SendAsync(IServiceProvider services, string email, TextWriter output, CancellationToken ct = default)
    {
        var db = services.GetRequiredService<AppDbContext>();
        var wanted = email.Trim().ToLowerInvariant();
        var user = await db.Set<AppUser>().AsNoTracking()
            .Where(u => u.Email != null && u.Email.ToLower() == wanted)
            .Select(u => new { u.Id }).SingleOrDefaultAsync(ct);
        if (user is null)
        {
            output.WriteLine("No account has that email.");
            return 1;
        }

        var platforms = await db.DeviceTokens.AsNoTracking().Where(d => d.UserId == user.Id)
            .GroupBy(d => d.Platform).Select(g => new { Platform = g.Key, Count = g.Count() }).ToListAsync(ct);
        if (platforms.Count == 0)
        {
            output.WriteLine("That account has no phones registered: install the app, sign in and allow notifications first.");
            return 1;
        }
        output.WriteLine("Registered phones: " + string.Join(", ", platforms.Select(p => $"{p.Platform} {p.Count}")));

        await services.GetRequiredService<IPushSender>().SendAsync(user.Id, Title, Body,
            new Dictionary<string, string> { ["path"] = Path }, ct);

        var left = await db.DeviceTokens.AsNoTracking().CountAsync(d => d.UserId == user.Id, ct);
        output.WriteLine($"Sent. Phones still registered afterwards: {left} (a phone the push service called dead is removed).");
        return 0;
    }
}
