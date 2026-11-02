using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace InvitesBlog.Tests.Integration;

/// <summary>
/// One certificate chain shaped like Apple's: an EC P-256 root, an intermediate marked with Apple's
/// intermediate OID and a leaf marked with the receipt-signing OID, each optional so a test can leave one out.
/// </summary>
public sealed class StubChain : IDisposable
{
    public X509Certificate2 Root { get; }
    public X509Certificate2 Intermediate { get; }
    public X509Certificate2 Leaf { get; }
    private readonly ECDsa _leafKey;

    private readonly bool _ownsRoot;

    /// <param name="under">Build under that chain's root instead of a new one of its own.</param>
    public StubChain(string name, bool leafOid = true, bool intermediateOid = true, StubChain? under = null)
    {
        var from = DateTimeOffset.UtcNow.AddDays(-1);
        var to = DateTimeOffset.UtcNow.AddYears(2);

        if (under is not null)
        {
            Root = under.Root;
            to = new DateTimeOffset(Root.NotAfter.ToUniversalTime()).AddDays(-1);
        }
        else
        {
            using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var rootReq = new CertificateRequest($"CN={name} Root CA - G3, O=Not Apple", rootKey, HashAlgorithmName.SHA256);
            rootReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
            rootReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
            rootReq.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(rootReq.PublicKey, false));
            Root = rootReq.CreateSelfSigned(from, to);
            _ownsRoot = true;
        }

        var intKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var intReq = new CertificateRequest($"CN={name} Worldwide Developer Relations, O=Not Apple", intKey, HashAlgorithmName.SHA256);
        intReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        intReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
        intReq.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(intReq.PublicKey, false));
        intReq.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(Root, true, false));
        if (intermediateOid) intReq.CertificateExtensions.Add(new X509Extension("1.2.840.113635.100.6.2.1", [0x05, 0x00], false));
        using var intPublic = intReq.Create(Root, from, to, RandomNumberGenerator.GetBytes(8));
        Intermediate = intPublic.CopyWithPrivateKey(intKey);

        _leafKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var leafReq = new CertificateRequest($"CN={name} ECC App Store Receipt Signing, O=Not Apple", _leafKey, HashAlgorithmName.SHA256);
        leafReq.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        leafReq.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        leafReq.CertificateExtensions.Add(X509AuthorityKeyIdentifierExtension.CreateFromCertificate(Intermediate, true, false));
        if (leafOid) leafReq.CertificateExtensions.Add(new X509Extension("1.2.840.113635.100.6.11.1", [0x05, 0x00], false));
        Leaf = leafReq.Create(Intermediate, from, to.AddDays(-1), RandomNumberGenerator.GetBytes(8));
    }

    /// <summary>A JWS as Apple shapes them: ES256, the chain in <c>x5c</c>, standard base64 DER.</summary>
    public string Sign(object payload, string alg = "ES256", X509Certificate2? x5cRoot = null)
    {
        var header = new Dictionary<string, object>
        {
            ["alg"] = alg,
            ["x5c"] = new[] { Leaf, Intermediate, x5cRoot ?? Root }.Select(c => Convert.ToBase64String(c.RawData)).ToArray(),
        };
        var h = Base64UrlEncoder.Encode(JsonSerializer.SerializeToUtf8Bytes(header));
        var p = Base64UrlEncoder.Encode(JsonSerializer.SerializeToUtf8Bytes(payload));
        var sig = _leafKey.SignData(Encoding.ASCII.GetBytes($"{h}.{p}"), HashAlgorithmName.SHA256);
        return $"{h}.{p}.{Base64UrlEncoder.Encode(sig)}";
    }

    public void Dispose()
    {
        _leafKey.Dispose();
        if (_ownsRoot) Root.Dispose();
        Intermediate.Dispose();
        Leaf.Dispose();
    }
}

/// <summary>
/// Apple's App Store Server API, as far as our client can tell: a real HTTP server on the documented
/// paths (production under /prod, sandbox under /sandbox), refusing a request without a properly
/// signed API token, answering with JWS signed by <see cref="Chain"/>. It is not Apple.
/// </summary>
public sealed class AppStoreStub : IAsyncDisposable
{
    public const string Bundle = "blog.invites.app";
    public const string Issuer = "57246542-96fe-1a63-e053-0824d011072a";
    public const string KeyId = "STUBKEY123";
    public const long AppAppleId = 6700000001;

    private readonly ECDsa _apiKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private WebApplication _app = default!;
    public StubChain Chain { get; } = new("Stub");
    public string BaseUrl { get; private set; } = default!;
    public string ProductionUrl => $"{BaseUrl}/prod/";
    public string SandboxUrl => $"{BaseUrl}/sandbox/";

    /// <summary>Transaction payloads by transaction id, as Get Transaction Info answers.</summary>
    public ConcurrentDictionary<string, Dictionary<string, object?>> Transactions { get; } = new();
    /// <summary>Subscriptions by original transaction id: status, the latest transaction id, renewal info.</summary>
    public ConcurrentDictionary<string, (int Status, string Latest, Dictionary<string, object?> Renewal)> Subscriptions { get; } = new();
    /// <summary>Signed notifications in Apple's history, per environment.</summary>
    public ConcurrentBag<(bool Sandbox, string SignedPayload)> History { get; } = [];
    public ConcurrentQueue<(string Method, string Path)> Requests { get; } = new();
    public ConcurrentDictionary<string, int> FailNext { get; } = new();
    public int TokensRefused;
    public int ReadDelayMs;

    public int Count(string fragment) => Requests.Count(r => r.Path.Contains(fragment, StringComparison.Ordinal));
    public string PrivateKeyPem() => _apiKey.ExportPkcs8PrivateKeyPem();

    public async Task StartAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        _app = builder.Build();
        _app.Map("/{env}/inApps/v1/{**rest}", (Delegate)Handle);
        await _app.StartAsync();
        BaseUrl = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
    }

    private bool Authorised(HttpContext ctx)
    {
        var auth = ctx.Request.Headers.Authorization.ToString();
        if (!auth.StartsWith("Bearer ", StringComparison.Ordinal)) return false;
        try
        {
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var claims = handler.ValidateToken(auth["Bearer ".Length..], new TokenValidationParameters
            {
                ValidIssuer = Issuer,
                ValidAudience = "appstoreconnect-v1",
                IssuerSigningKey = new ECDsaSecurityKey(ECDsa.Create(_apiKey.ExportParameters(false))) { KeyId = KeyId },
                ValidAlgorithms = [SecurityAlgorithms.EcdsaSha256],
            }, out var token);
            var jwt = (JwtSecurityToken)token;
            return jwt.Header.Kid == KeyId && claims.FindFirst("bid")?.Value == Bundle && claims.FindFirst("iat") is not null
                   && jwt.ValidTo - DateTime.UtcNow <= TimeSpan.FromMinutes(61);
        }
        catch (Exception ex) when (ex is SecurityTokenException or ArgumentException)
        {
            return false;
        }
    }

    private async Task<IResult> Handle(HttpContext ctx, string env)
    {
        var raw = (ctx.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpRequestFeature>()?.RawTarget ?? "").Split('?')[0];
        Requests.Enqueue((ctx.Request.Method, raw));
        if (!Authorised(ctx))
        {
            Interlocked.Increment(ref TokensRefused);
            return Results.Json(new { errorCode = 4010000, errorMessage = "Unauthorized" }, statusCode: 401);
        }
        foreach (var (key, status) in FailNext)
            if (raw.Contains(key, StringComparison.Ordinal) && FailNext.TryRemove(key, out _))
                return Results.Json(new { errorCode = 5000000 }, statusCode: status);
        if (ctx.Request.Method == "GET" && ReadDelayMs > 0) await Task.Delay(ReadDelayMs);

        var sandbox = env == "sandbox";
        var wanted = sandbox ? "Sandbox" : "Production";
        var parts = raw[$"/{env}/inApps/v1/".Length..].Split('/');

        if (ctx.Request.Method == "GET" && parts is ["transactions", var id])
        {
            id = Uri.UnescapeDataString(id);
            if (!Transactions.TryGetValue(id, out var t) || (string?)t["environment"] != wanted)
                return Results.Json(new { errorCode = 4040010, errorMessage = "Transaction id not found." }, statusCode: 404);
            return Results.Json(new { signedTransactionInfo = Chain.Sign(t) });
        }
        if (ctx.Request.Method == "GET" && parts is ["subscriptions", var original])
        {
            original = Uri.UnescapeDataString(original);
            if (!Subscriptions.TryGetValue(original, out var s) || (string?)Transactions[s.Latest]["environment"] != wanted)
                return Results.Json(new { errorCode = 4040010, errorMessage = "Transaction id not found." }, statusCode: 404);
            return Results.Json(new
            {
                environment = wanted, bundleId = Bundle, appAppleId = AppAppleId,
                data = new[]
                {
                    new
                    {
                        subscriptionGroupIdentifier = "21000001",
                        lastTransactions = new[]
                        {
                            new { originalTransactionId = original, status = s.Status, signedTransactionInfo = Chain.Sign(Transactions[s.Latest]), signedRenewalInfo = Chain.Sign(s.Renewal) },
                        },
                    },
                },
            });
        }
        if (ctx.Request.Method == "POST" && parts is ["notifications", "history"])
        {
            using var body = await JsonDocument.ParseAsync(ctx.Request.Body);
            if (!body.RootElement.TryGetProperty("startDate", out _) || !body.RootElement.TryGetProperty("endDate", out _))
                return Results.Json(new { errorCode = 4000000 }, statusCode: 400);
            var page = ctx.Request.Query["paginationToken"].ToString();
            var all = History.Where(h => h.Sandbox == sandbox).Select(h => h.SignedPayload).ToList();
            // Two per page, so paging is exercised.
            var start = int.TryParse(page, out var p) ? p : 0;
            var slice = all.Skip(start).Take(2).ToList();
            var more = start + 2 < all.Count;
            return Results.Json(new
            {
                notificationHistory = slice.Select(s => new { signedPayload = s }).ToArray(),
                hasMore = more,
                paginationToken = more ? (start + 2).ToString() : null,
            });
        }
        return Results.Json(new { errorCode = 4040000, path = raw }, statusCode: 404);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync();
        await _app.DisposeAsync();
        _apiKey.Dispose();
        Chain.Dispose();
    }
}
