using System.Net.Http.Headers;
using System.Security.Claims;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Infrastructure.Accounts;
using InvitesBlog.Infrastructure.Persistence;
using InvitesBlog.Infrastructure.Storage;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Npgsql;
using Xunit;

namespace InvitesBlog.Tests.Integration;

/// <summary>
/// A test that needs a real Postgres. Set IB_TEST_POSTGRES to a connection string for a throwaway
/// server (no database name); each run makes its own database there and drops it afterwards. Without
/// it these report as skipped, never as passed.
/// </summary>
public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PostgresApiFixture.ServerVariable)))
            Skip = $"Set {PostgresApiFixture.ServerVariable} to run against real Postgres.";
    }
}

/// <summary>The whole API on a real database, its migrations included, with outside services replaced.</summary>
public sealed class PostgresApiFixture : IAsyncLifetime
{
    public const string ServerVariable = "IB_TEST_POSTGRES";

    /// <summary>The API reads its settings from the process's environment, so fixtures take turns starting it.</summary>
    public static readonly SemaphoreSlim EnvLock = new(1, 1);

    private string _database = default!;
    private string _server = default!;
    private string _storageRoot = default!;
    private ApiFactory? _factory;

    public CapturedCodes Codes { get; } = new();
    public RecordingBank Bank { get; } = new();
    public RecordingMail Mail { get; } = new();
    public FlakyStorage Storage { get; private set; } = default!;
    public GooglePlayStub Google { get; } = new();
    public RecordingLogs Logs { get; } = new();
    public ApnsStub Apns { get; } = new();
    public AppleIdStub Apple { get; } = new();
    public IServiceProvider Services => _factory!.Services;
    public string StorageRoot => _storageRoot;

    public async Task InitializeAsync()
    {
        var server = Environment.GetEnvironmentVariable(ServerVariable);
        if (string.IsNullOrWhiteSpace(server)) return;
        _server = server;
        _database = $"ib_del_{Guid.NewGuid():N}";
        _storageRoot = Path.Combine(Path.GetTempPath(), _database);

        await using (var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_server) { Database = "postgres" }.ToString()))
        {
            await admin.OpenAsync();
            await using var create = new NpgsqlCommand($"CREATE DATABASE {_database}", admin);
            await create.ExecuteNonQueryAsync();
        }

        await Google.StartAsync();
        await Apple.StartAsync();

        var conn = new NpgsqlConnectionStringBuilder(_server) { Database = _database }.ToString();
        // Environment rather than the factory's configuration hook: Program reads configuration while it
        // registers services, before such a hook would take effect. One fixture at a time: see EnvLock.
        await EnvLock.WaitAsync();
        try
        {
        foreach (var (key, value) in new Dictionary<string, string>
        {
            ["ConnectionStrings__Postgres"] = conn,
            ["Otp__Channels"] = "Email,Sms",
            ["Email__Provider"] = "Console",
            ["Storage__Provider"] = "Local",
            ["Storage__LocalRoot"] = _storageRoot,
            ["Payments__Provider"] = "Fake",
            ["Urls__ApiBase"] = "http://localhost",
            ["Urls__InviterBase"] = "http://localhost",
            ["Urls__InviteeBase"] = "http://localhost",
            ["Urls__AssetsBase"] = "http://localhost/assets",
            ["Jwt__SigningKey"] = "integration-test-signing-key-long-enough-for-hmac-sha256-0123456789",
            ["GooglePlay__PackageName"] = GooglePlayStub.Package,
            ["GooglePlay__ServiceAccountJson"] = Google.ServiceAccountJson(),
            ["GooglePlay__ApiBaseUrl"] = Google.BaseUrl,
            ["GooglePlay__Notifications__Audience"] = GooglePlayStub.Audience,
            ["GooglePlay__Notifications__ServiceAccountEmail"] = GooglePlayStub.PubSubEmail,
            ["GooglePlay__Notifications__JwksUrl"] = $"{Google.BaseUrl}/jwks",
            // Apple push on, with a key made for this run; requests go to Apns, never to Apple.
            ["Push__Apns__KeyId"] = ApnsStub.KeyId,
            ["Push__Apns__TeamId"] = ApnsStub.TeamId,
            ["Push__Apns__PrivateKey"] = Apns.PrivateKeyOneLine,
            // Sign in with Apple, with a key made for this run; every call goes to Apple (the stub).
            ["OAuth__Apple__ClientId"] = AppleIdStub.ClientId,
            ["OAuth__Apple__TeamId"] = AppleIdStub.TeamId,
            ["OAuth__Apple__KeyId"] = AppleIdStub.KeyId,
            ["OAuth__Apple__PrivateKey"] = Apple.PrivateKeyOneLine,
            ["OAuth__Apple__BaseUrl"] = Apple.BaseUrl,
        })
            Environment.SetEnvironmentVariable(key, value);

        _factory = new ApiFactory(this);
        _ = _factory.Server; // starts the app: migrates and seeds

        // Read once at start-up; cleared so no other test host in this process gets a push key.
        foreach (var key in new[] { "Push__Apns__KeyId", "Push__Apns__TeamId", "Push__Apns__PrivateKey",
                     "OAuth__Apple__ClientId", "OAuth__Apple__TeamId", "OAuth__Apple__KeyId", "OAuth__Apple__PrivateKey", "OAuth__Apple__BaseUrl" })
            Environment.SetEnvironmentVariable(key, null);
        }
        finally
        {
            EnvLock.Release();
        }

        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        if (!await db.Roles.AnyAsync(r => r.Name == Roles.Customer))
            throw new InvalidOperationException("The app didn't migrate and seed the test database.");
    }

    public async Task DisposeAsync()
    {
        if (_factory is null) return;
        await _factory.DisposeAsync();
        await Google.DisposeAsync();
        await Apple.DisposeAsync();
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_server) { Database = "postgres" }.ToString());
        await admin.OpenAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS {_database} WITH (FORCE)", admin);
        await drop.ExecuteNonQueryAsync();
        if (Directory.Exists(_storageRoot)) Directory.Delete(_storageRoot, recursive: true);
    }

    /// <summary>A database context of its own, so what it reads is what was committed.</summary>
    public async Task<T> ReadAsync<T>(Func<AppDbContext, Task<T>> read)
    {
        using var scope = Services.CreateScope();
        return await read(scope.ServiceProvider.GetRequiredService<AppDbContext>());
    }

    public async Task WriteAsync(Func<AppDbContext, Task> write)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        await write(db);
        await db.SaveChangesAsync();
    }

    /// <summary>Sends every queued push now, as the worker would.</summary>
    public Task<int> DrainPushesAsync() =>
        Services.GetRequiredService<InvitesBlog.Infrastructure.Notifications.Push.PushWorker>().DrainAsync();

    /// <summary>Works through the removal list until nothing more comes off it, as the service does.</summary>
    public async Task RunRemovalsAsync()
    {
        var service = Services.GetRequiredService<PendingRemovalService>();
        while (await service.RunOnceAsync(CancellationToken.None) > 0) { }
    }

    /// <summary>A client with its own address, so one test's requests don't use up another's limits.</summary>
    public HttpClient Client(string? token = null, string? address = null)
    {
        var client = _factory!.CreateClient();
        client.DefaultRequestHeaders.Add(TestAddressHeader, address ?? $"10.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}.{Random.Shared.Next(1, 250)}");
        if (token is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    /// <summary>The token sign-in issues, in the same shape.</summary>
    public string TokenFor(Guid userId, string contactType, string contact, params string[] roles)
    {
        var issuer = Services.GetRequiredService<IInviteeTokenIssuer>();
        return issuer.IssueForRoles(
            roles.Length == 0 ? [Roles.Customer] : roles,
            new Dictionary<string, string>
            {
                [ClaimTypes.NameIdentifier] = userId.ToString(),
                ["contact_type"] = contactType,
                ["contact"] = contact,
            },
            TimeSpan.FromDays(14));
    }

    public const string TestAddressHeader = "X-Test-Address";

    private sealed class ApiFactory(PostgresApiFixture fixture) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Development");
            builder.ConfigureLogging(l => l.AddProvider(fixture.Logs));
            builder.ConfigureTestServices(services =>
            {
                // No sweeps racing the assertions. Removals are run by hand; the service itself is real.
                services.RemoveAll<IHostedService>();
                services.AddSingleton<PendingRemovalService>();

                services.RemoveAll<IOtpSender>();
                services.AddSingleton<IOtpSender>(new CapturingSender("email", fixture.Codes));
                services.AddSingleton<IOtpSender>(new CapturingSender("sms", fixture.Codes));

                services.RemoveAll<IRecurringPaymentProvider>();
                services.AddSingleton<IRecurringPaymentProvider>(fixture.Bank);

                services.RemoveAll<IStorageService>();
                services.AddSingleton<IStorageService>(sp =>
                    fixture.Storage = new FlakyStorage(new LocalFileStorageService(sp.GetRequiredService<IConfiguration>())));

                services.RemoveAll<IEmailSender>();
                services.AddSingleton<IEmailSender>(fixture.Mail);

                services.AddSingleton<IStartupFilter, TestAddressFilter>();

                // Apple's push service: the stand-in answers instead, behind the client's real pipeline.
                services.AddHttpClient(InvitesBlog.Infrastructure.Notifications.Push.ApnsClient.ClientName)
                    .ConfigurePrimaryHttpMessageHandler(() => new ApnsStub.Handler(fixture.Apns));
                // The push worker isn't running (no hosted services): tests drain the line themselves.
                services.AddSingleton<InvitesBlog.Infrastructure.Notifications.Push.PushWorker>();
            });
        }
    }

    /// <summary>Gives each client an address of its own; a test server's requests have none.</summary>
    private sealed class TestAddressFilter : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (ctx, go) =>
            {
                if (System.Net.IPAddress.TryParse(ctx.Request.Headers[TestAddressHeader].ToString(), out var ip))
                    ctx.Connection.RemoteIpAddress = ip;
                await go(ctx);
            });
            next(app);
        };
    }
}

/// <summary>Everything the app logged, so a test can say what must never appear there.</summary>
public sealed class RecordingLogs : ILoggerProvider, ILogger
{
    public System.Collections.Concurrent.ConcurrentQueue<string> Lines { get; } = new();
    public ILogger CreateLogger(string categoryName) => this;
    public void Dispose() { }
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => true;
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
        Lines.Enqueue($"{formatter(state, exception)} {exception}");
}

public sealed class CapturedCodes
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Code, string Channel)> _latest = new();
    public void Record(string recipient, string channel, string code) => _latest[recipient] = (code, channel);
    public string CodeFor(string recipient) => _latest[recipient].Code;
    public string ChannelFor(string recipient) => _latest[recipient].Channel;
}

internal sealed class CapturingSender(string channel, CapturedCodes codes) : IOtpSender
{
    public string Channel => channel;
    public Task<DeliveryResult> SendCodeAsync(string recipient, string code, CancellationToken ct)
    {
        codes.Record(recipient, channel, code);
        return Task.FromResult(DeliveryResult.Ok("test"));
    }
}

public sealed class RecordingMail : IEmailSender
{
    public System.Collections.Concurrent.ConcurrentBag<EmailMessage> Sent { get; } = [];
    public Task<DeliveryResult> SendAsync(EmailMessage message, CancellationToken ct = default)
    {
        Sent.Add(message);
        return Task.FromResult(DeliveryResult.Ok("test"));
    }
}

public sealed class RecordingBank : IRecurringPaymentProvider
{
    public System.Collections.Concurrent.ConcurrentBag<string> Forgotten { get; } = [];
    public System.Collections.Concurrent.ConcurrentBag<SavedCardCharge> Charges { get; } = [];
    public Task<string> CreateCustomerAsync(string name, string email, CancellationToken ct) => Task.FromResult($"cus_{Guid.NewGuid():N}");
    public Task<bool> HasSavedCardAsync(string customerId, CancellationToken ct) => Task.FromResult(true);
    public Task<PaymentWebhookResult> ChargeSavedCardAsync(SavedCardCharge charge, CancellationToken ct)
    {
        Charges.Add(charge);
        return Task.FromResult(new PaymentWebhookResult(WebhookEventKind.PaymentSucceeded, null, $"tx_{Guid.NewGuid():N}", null, null));
    }
    public Task ForgetCustomerAsync(string customerId, CancellationToken ct)
    {
        Forgotten.Add(customerId);
        return Task.CompletedTask;
    }
}

/// <summary>Real files on disk, except that removals can be made to fail.</summary>
public sealed class FlakyStorage(IStorageService inner) : IStorageService
{
    public volatile bool FailRemovals;
    public Task<string> PutAsync(string key, byte[] content, string contentType, CancellationToken ct = default) => inner.PutAsync(key, content, contentType, ct);
    public Task<byte[]?> GetAsync(string key, CancellationToken ct = default) => inner.GetAsync(key, ct);
    public Task<string> PutFileAsync(string key, string path, string contentType, CancellationToken ct = default) => inner.PutFileAsync(key, path, contentType, ct);
    public Task<bool> DownloadToFileAsync(string key, string path, CancellationToken ct = default) => inner.DownloadToFileAsync(key, path, ct);
    public string PublicUrl(string key) => inner.PublicUrl(key);
    public string? KeyFor(string url) => inner.KeyFor(url);
    public Task DeleteAsync(string key, CancellationToken ct = default) =>
        FailRemovals ? throw new IOException("Storage is down.") : inner.DeleteAsync(key, ct);
    public Task DeletePrefixAsync(string prefix, CancellationToken ct = default) =>
        FailRemovals ? throw new IOException("Storage is down.") : inner.DeletePrefixAsync(prefix, ct);
}

/// <summary>A data-driven test that needs a real Postgres; skipped, never passed, without one.</summary>
public sealed class PostgresTheoryAttribute : TheoryAttribute
{
    public PostgresTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PostgresApiFixture.ServerVariable)))
            Skip = $"Set {PostgresApiFixture.ServerVariable} to run against real Postgres.";
    }
}
