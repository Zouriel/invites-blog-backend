using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using InvitesBlog.Infrastructure.Notifications.Push;

namespace InvitesBlog.Tests.Integration;

/// <summary>
/// Stands in for FCM's HTTP v1 send endpoint: records each message by token and answers as Google
/// does (the refusal bodies are copied from real FCM answers). Credentials are a fixed fake, so no
/// service-account file or Google token endpoint is involved.
/// </summary>
public sealed class FcmStub
{
    public const string Project = "test-project";
    public const string AccessToken = "fcm-test-access-token";

    public ConcurrentQueue<FcmRequest> Requests { get; } = new();

    /// <summary>Token -> (status, body) to answer with instead of success.</summary>
    public ConcurrentDictionary<string, (HttpStatusCode Status, string Body)> Refuse { get; } = new();

    public const string Unregistered = """
        {"error":{"code":404,"message":"NotRegistered","status":"NOT_FOUND","details":[{"@type":"type.googleapis.com/google.firebase.fcm.v1.FcmError","errorCode":"UNREGISTERED"}]}}
        """;
    public const string NotAToken = """
        {"error":{"code":400,"message":"The registration token is not a valid FCM registration token","status":"INVALID_ARGUMENT","details":[{"@type":"type.googleapis.com/google.firebase.fcm.v1.FcmError","errorCode":"INVALID_ARGUMENT"}]}}
        """;
    public const string Quota = """
        {"error":{"code":429,"message":"Quota exceeded","status":"RESOURCE_EXHAUSTED","details":[{"errorCode":"QUOTA_EXCEEDED"}]}}
        """;

    public IReadOnlyList<FcmRequest> To(string token) => Requests.Where(r => r.Token == token).ToList();

    private async Task<HttpResponseMessage> AnswerAsync(HttpRequestMessage request, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct));
        var message = doc.RootElement.GetProperty("message").Clone();
        var token = message.GetProperty("token").GetString()!;
        Requests.Enqueue(new FcmRequest(request.RequestUri!.ToString(), request.Headers.Authorization?.ToString(), token, message));

        if (Refuse.TryGetValue(token, out var refusal))
            return new HttpResponseMessage(refusal.Status) { Content = new StringContent(refusal.Body, Encoding.UTF8, "application/json") };
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent($$"""{"name":"projects/{{Project}}/messages/1"}""", Encoding.UTF8, "application/json"),
        };
    }

    public sealed class Handler(FcmStub stub) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            stub.AnswerAsync(request, ct);
    }

    public sealed class Credentials : IFcmCredentials
    {
        public Task<string> ProjectIdAsync(CancellationToken ct) => Task.FromResult(Project);
        public Task<string> AccessTokenAsync(CancellationToken ct) => Task.FromResult(AccessToken);
    }
}

public sealed record FcmRequest(string Url, string? Authorization, string Token, JsonElement Message)
{
    public string Title => Message.GetProperty("notification").GetProperty("title").GetString()!;
    public string Body => Message.GetProperty("notification").GetProperty("body").GetString()!;
    public string Path => Message.GetProperty("data").GetProperty("path").GetString()!;
    public string? Data(string key) => Message.GetProperty("data").TryGetProperty(key, out var v) ? v.GetString() : null;
    public string? Channel => Message.GetProperty("android").GetProperty("notification").GetProperty("channel_id").GetString();
    public string? Tag => Message.GetProperty("android").GetProperty("notification").TryGetProperty("tag", out var v) ? v.GetString() : null;
}
