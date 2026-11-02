using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace InvitesBlog.Tests.Integration;

/// <summary>
/// Stands in for Apple's push service. Records every request (host, device token, headers, JSON) and
/// answers 200, unless a token has been told to answer otherwise or the whole service is "down".
/// </summary>
public sealed class ApnsStub
{
    public const string KeyId = "TESTKEY123";
    public const string TeamId = "TESTTEAM45";

    private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    public ConcurrentQueue<ApnsRequest> Requests { get; } = new();

    /// <summary>Per token: the status and Apple reason to answer with.</summary>
    public ConcurrentDictionary<string, (HttpStatusCode Status, string Reason)> Refuse { get; } = new();

    /// <summary>When set, every request fails as if Apple couldn't be reached.</summary>
    public volatile bool Down;

    /// <summary>The key as the server's .env holds it: one line, newlines written as \n.</summary>
    public string PrivateKeyOneLine => _key.ExportPkcs8PrivateKeyPem().Replace("\r", "").Replace("\n", "\\n");

    public ECDsa PublicKey
    {
        get
        {
            var k = ECDsa.Create();
            k.ImportParameters(_key.ExportParameters(includePrivateParameters: false));
            return k;
        }
    }

    public IReadOnlyList<ApnsRequest> To(string token) => Requests.Where(r => r.Token == token).ToList();

    private async Task<HttpResponseMessage> AnswerAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var uri = request.RequestUri!;
        if (Down) throw new HttpRequestException($"Connection refused ({uri})");

        var token = Uri.UnescapeDataString(uri.AbsolutePath["/3/device/".Length..]);
        var body = await request.Content!.ReadAsStringAsync(ct);
        Requests.Enqueue(new ApnsRequest(
            uri.Host, token, request.Version,
            request.Headers.Authorization?.Scheme, request.Headers.Authorization?.Parameter,
            Header(request, "apns-topic"), Header(request, "apns-push-type"), JsonDocument.Parse(body).RootElement.Clone()));

        if (Refuse.TryGetValue(token, out var refusal))
            return new HttpResponseMessage(refusal.Status)
            {
                Content = new StringContent($"{{\"reason\":\"{refusal.Reason}\"}}", Encoding.UTF8, "application/json"),
            };
        return new HttpResponseMessage(HttpStatusCode.OK);
    }

    private static string? Header(HttpRequestMessage r, string name) =>
        r.Headers.TryGetValues(name, out var v) ? v.FirstOrDefault() : null;

    /// <summary>One per client the factory builds; the stub itself outlives them all.</summary>
    public sealed class Handler(ApnsStub stub) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            stub.AnswerAsync(request, ct);
    }
}

public sealed record ApnsRequest(
    string Host, string Token, Version Version, string? AuthScheme, string? Jwt, string? Topic, string? PushType, JsonElement Payload)
{
    public string Title => Payload.GetProperty("aps").GetProperty("alert").GetProperty("title").GetString()!;
    public string Body => Payload.GetProperty("aps").GetProperty("alert").GetProperty("body").GetString()!;
    public string Path => Payload.GetProperty("path").GetString()!;
}
