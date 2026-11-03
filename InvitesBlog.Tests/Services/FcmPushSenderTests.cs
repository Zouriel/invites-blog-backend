using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Infrastructure;
using InvitesBlog.Infrastructure.Notifications.Push;
using InvitesBlog.Infrastructure.Persistence;
using InvitesBlog.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>
/// Push through FCM, against a stand-in for its API: one request per device in v1's shape, dead tokens
/// forgotten, and nothing a device or Google does can fail the caller. Off unless switched on.
/// </summary>
public class FcmPushSenderTests
{
    private const string Unregistered = """
        {"error":{"code":404,"status":"NOT_FOUND","details":[
          {"@type":"type.googleapis.com/google.firebase.fcm.v1.FcmError","errorCode":"UNREGISTERED"}]}}
        """;

    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"push-{Guid.NewGuid()}").Options);
    private readonly IFcmCredentials _credentials = Substitute.For<IFcmCredentials>();
    private readonly StubFcm _fcm = new();
    private readonly Guid _user = Guid.NewGuid();

    public FcmPushSenderTests()
    {
        _credentials.ProjectIdAsync(Arg.Any<CancellationToken>()).Returns("demo-project");
        _credentials.AccessTokenAsync(Arg.Any<CancellationToken>()).Returns("access-token");
    }

    private FcmPushSender Sut()
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(FcmPushSender.ClientName)
            .Returns(_ => new HttpClient(_fcm, disposeHandler: false) { BaseAddress = new Uri("https://fcm.test/") });
        return new FcmPushSender(factory, _credentials, new BaseRepository<DeviceToken>(_db), new UnitOfWork(_db),
            NullLogger<FcmPushSender>.Instance);
    }

    private void Device(string token, Guid? user = null)
    {
        var now = DateTimeOffset.UtcNow;
        _db.DeviceTokens.Add(new DeviceToken
        {
            Id = Guid.NewGuid(), UserId = user ?? _user, Token = token, Platform = "android", CreatedAt = now, UpdatedAt = now,
        });
        _db.SaveChanges();
    }

    private Task Send(IReadOnlyDictionary<string, string>? data = null) =>
        Sut().SendAsync(_user, "Title", "Body", data, CancellationToken.None);

    private List<string> Remaining() => [.. _db.DeviceTokens.AsNoTracking().Select(d => d.Token).OrderBy(t => t)];

    [Fact]
    public async Task Each_of_the_account_s_devices_gets_a_v1_message_and_nobody_else_s_does()
    {
        Device("phone");
        Device("tablet");
        Device("someone-else", Guid.NewGuid());

        await Send(new Dictionary<string, string> { ["campaignId"] = "abc" });

        Assert.Equal(["phone", "tablet"], _fcm.Requests.Select(r => r.Token).OrderBy(t => t));
        foreach (var r in _fcm.Requests)
        {
            Assert.Equal(HttpMethod.Post, r.Method);
            Assert.Equal("https://fcm.test/v1/projects/demo-project/messages:send", r.Url);
            Assert.Equal("Bearer access-token", r.Authorization);
            var message = r.Body["message"]!;
            Assert.Equal("Title", (string?)message["notification"]!["title"]);
            Assert.Equal("Body", (string?)message["notification"]!["body"]);
            Assert.Equal("abc", (string?)message["data"]!["campaignId"]);
        }
    }

    [Fact]
    public async Task With_no_data_the_field_is_left_out()
    {
        Device("phone");

        await Send();

        Assert.False(_fcm.Requests.Single().Body["message"]!.AsObject().ContainsKey("data"));
    }

    [Fact]
    public async Task An_unregistered_token_is_forgotten_and_the_others_still_get_it()
    {
        Device("alive-1");
        Device("dead");
        Device("alive-2");
        _fcm.Reply["dead"] = (HttpStatusCode.NotFound, Unregistered);

        await Send();

        Assert.Equal(3, _fcm.Requests.Count);
        Assert.Equal(["alive-1", "alive-2"], Remaining());
    }

    /// <summary>The answers FCM gives for a token that will never work again (seen from the real API).</summary>
    [Theory]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"code":400,"message":"The registration token is not a valid FCM registration token","status":"INVALID_ARGUMENT","details":[{"@type":"type.googleapis.com/google.firebase.fcm.v1.FcmError","errorCode":"INVALID_ARGUMENT"}]}}""")]
    [InlineData(HttpStatusCode.Forbidden, """{"error":{"code":403,"message":"SenderId mismatch","status":"PERMISSION_DENIED","details":[{"errorCode":"SENDER_ID_MISMATCH"}]}}""")]
    public async Task A_token_fcm_calls_invalid_or_foreign_is_forgotten(HttpStatusCode status, string body)
    {
        Device("alive");
        Device("dead");
        _fcm.Reply["dead"] = (status, body);

        await Send();

        Assert.Equal(["alive"], Remaining());
    }

    [Fact]
    public async Task Android_gets_high_priority_on_the_app_s_channel()
    {
        Device("phone");

        await Send();

        var android = _fcm.Requests.Single().Body["message"]!["android"]!;
        Assert.Equal("HIGH", (string?)android["priority"]);
        Assert.Equal("events", (string?)android["notification"]!["channel_id"]);
    }

    /// <summary>A 404 or 400 on its own doesn't mean the device is gone: our message or project could be wrong.</summary>
    [Theory]
    [InlineData(HttpStatusCode.NotFound, """{"error":{"code":404,"status":"NOT_FOUND"}}""")]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"status":"INVALID_ARGUMENT","details":[{"errorCode":"INVALID_ARGUMENT"}]}}""")]
    [InlineData(HttpStatusCode.BadRequest, """{"error":{"message":"Invalid JSON payload received. Unknown name \"foo\"","status":"INVALID_ARGUMENT","details":[{"errorCode":"INVALID_ARGUMENT"}]}}""")]
    [InlineData(HttpStatusCode.ServiceUnavailable, "<html>not json</html>")]
    [InlineData(HttpStatusCode.InternalServerError, "[]")]
    public async Task Any_other_failure_keeps_the_device(HttpStatusCode status, string body)
    {
        Device("phone");
        _fcm.Reply["phone"] = (status, body);

        await Send();

        Assert.Equal(["phone"], Remaining());
    }

    [Fact]
    public async Task One_device_timing_out_does_not_stop_the_next()
    {
        Device("a-slow");
        Device("b-fine");
        Device("c-fine");
        _fcm.ThrowFor.Add("a-slow");
        _fcm.ThrowFor.Add("c-fine");

        await Send();

        Assert.Equal(3, _fcm.Requests.Count);
        Assert.Equal(3, Remaining().Count);
    }

    [Fact]
    public async Task Missing_credentials_are_not_the_caller_s_problem()
    {
        Device("phone");
        _credentials.AccessTokenAsync(Arg.Any<CancellationToken>()).ThrowsAsync(new FileNotFoundException());

        await Send();

        Assert.Empty(_fcm.Requests);
        Assert.Equal(["phone"], Remaining());
    }

    [Fact]
    public async Task An_account_with_no_devices_needs_no_credentials()
    {
        await Send();

        await _credentials.DidNotReceive().AccessTokenAsync(Arg.Any<CancellationToken>());
    }

    // ----- switched off by default ----------------------------------------------------------------

    private static Type SenderFor(params (string Key, string Value)[] settings)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value))).Build();
        var services = new ServiceCollection();
        services.AddInvitesBlogInfrastructure(config);
        var registration = services.Single(d => d.ServiceType == typeof(IPushSender));
        if (registration.ImplementationType is { } type) return type;
        // On, it's the composite of whichever services are configured: report the ones it is made of.
        return services.Any(d => d.ServiceType == typeof(FcmPushSender)) ? typeof(FcmPushSender) : typeof(ApnsPushSender);
    }

    [Fact]
    public void With_no_setting_nothing_can_be_sent() => Assert.Equal(typeof(NoOpPushSender), SenderFor());

    [Fact]
    public void Disabled_means_nothing_can_be_sent() =>
        Assert.Equal(typeof(NoOpPushSender), SenderFor(("Push:Enabled", "false")));

    [Fact]
    public void Apple_keys_alone_turn_on_apns_but_not_fcm()
    {
        using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
        Assert.Equal(typeof(ApnsPushSender), SenderFor(
            ("Push:Apns:KeyId", "K"), ("Push:Apns:TeamId", "T"), ("Push:Apns:PrivateKey", key.ExportPkcs8PrivateKeyPem())));
    }

    [Fact]
    public void Half_set_apple_keys_send_nothing() =>
        Assert.Equal(typeof(NoOpPushSender), SenderFor(("Push:Apns:KeyId", "K"), ("Push:Apns:TeamId", "T")));

    [Fact]
    public void Enabled_uses_fcm() =>
        Assert.Equal(typeof(FcmPushSender), SenderFor(("Push:Enabled", "true")));

    [Fact]
    public async Task The_no_op_sender_does_nothing_and_succeeds() =>
        await new NoOpPushSender().SendAsync(_noUser, "t", "b", null, CancellationToken.None);

    private static readonly Guid _noUser = Guid.NewGuid();

    // ----- the service account --------------------------------------------------------------------

    /// <summary>
    /// The one path the FCM stand-in skips: the real Google library, a generated key, and a local
    /// server in place of Google's token endpoint.
    /// </summary>
    [Fact]
    public async Task The_service_account_file_gives_the_project_and_a_token_for_the_messaging_scope()
    {
        using var listener = new HttpListener();
        var free = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        free.Start();
        var port = ((IPEndPoint)free.LocalEndpoint).Port;
        free.Stop();
        var tokenUri = $"http://127.0.0.1:{port}/token/";
        listener.Prefixes.Add(tokenUri);
        listener.Start();

        using var rsa = RSA.Create(2048);
        var file = Path.Combine(Path.GetTempPath(), $"sa-{Guid.NewGuid():N}.json");
        await File.WriteAllTextAsync(file, new JsonObject
        {
            ["type"] = "service_account",
            ["project_id"] = "demo-project",
            ["private_key_id"] = "key-1",
            ["private_key"] = rsa.ExportPkcs8PrivateKeyPem(),
            ["client_email"] = "push@demo-project.iam.gserviceaccount.com",
            ["client_id"] = "1",
            ["token_uri"] = tokenUri,
        }.ToJsonString());

        var grants = new List<string>();
        var server = Task.Run(async () =>
        {
            var context = await listener.GetContextAsync();
            using var reader = new StreamReader(context.Request.InputStream);
            grants.Add(await reader.ReadToEndAsync());
            var reply = Encoding.UTF8.GetBytes("""{"access_token":"from-google","expires_in":3600,"token_type":"Bearer"}""");
            context.Response.ContentType = "application/json";
            await context.Response.OutputStream.WriteAsync(reply);
            context.Response.Close();
        });

        try
        {
            var credentials = new ServiceAccountFcmCredentials(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { [ServiceAccountFcmCredentials.ConfigKey] = file })
                .Build());

            Assert.Equal("demo-project", await credentials.ProjectIdAsync(CancellationToken.None));
            Assert.Equal("from-google", await credentials.AccessTokenAsync(CancellationToken.None));
            // Cached: the local server only answers once, so a second fetch would hang.
            Assert.Equal("from-google", await credentials.AccessTokenAsync(CancellationToken.None));
            await server;

            var assertion = System.Web.HttpUtility.ParseQueryString(Assert.Single(grants))["assertion"]!;
            var jwt = new JwtSecurityTokenHandler().ReadJwtToken(assertion);
            Assert.Equal("push@demo-project.iam.gserviceaccount.com", jwt.Issuer);
            Assert.Equal("https://www.googleapis.com/auth/firebase.messaging", jwt.Claims.Single(c => c.Type == "scope").Value);
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public async Task No_path_configured_is_a_clear_failure_for_the_sender_to_log()
    {
        var credentials = new ServiceAccountFcmCredentials(new ConfigurationBuilder().Build());

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => credentials.ProjectIdAsync(CancellationToken.None));
        Assert.Contains(ServiceAccountFcmCredentials.ConfigKey, ex.Message);
    }

    private sealed class StubFcm : HttpMessageHandler
    {
        public sealed record Seen(HttpMethod Method, string Url, string? Authorization, JsonNode Body, string Token);

        public List<Seen> Requests { get; } = [];
        public Dictionary<string, (HttpStatusCode Status, string Body)> Reply { get; } = [];
        public HashSet<string> ThrowFor { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
            var token = (string)body["message"]!["token"]!;
            Requests.Add(new Seen(request.Method, request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), body, token));

            if (ThrowFor.Contains(token)) throw new TaskCanceledException("timed out");
            var (status, reply) = Reply.TryGetValue(token, out var r) ? r : (HttpStatusCode.OK, """{"name":"projects/demo-project/messages/1"}""");
            return new HttpResponseMessage(status) { Content = new StringContent(reply, Encoding.UTF8, "application/json") };
        }
    }
}
