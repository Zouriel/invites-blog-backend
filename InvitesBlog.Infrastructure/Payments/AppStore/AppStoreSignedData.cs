using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using InvitesBlog.Application.Services.Billing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;

namespace InvitesBlog.Infrastructure.Payments.AppStore;

/// <summary>
/// The roots an App Store signature must chain to, and whether certificates are checked for revocation
/// online. Only code sets this, never configuration: production always trusts Apple's root alone.
/// Tests register their own.
/// </summary>
public sealed class AppStoreTrust(IReadOnlyList<X509Certificate2> roots, bool checkRevocation)
{
    public IReadOnlyList<X509Certificate2> Roots { get; } = roots;
    public bool CheckRevocation { get; } = checkRevocation;

    /// <summary>
    /// Apple Root CA - G3, from https://www.apple.com/certificateauthority/AppleRootCA-G3.cer.
    /// SHA-256 fingerprint 63:34:3A:BF:B8:9A:6A:03:EB:B5:7E:9B:3F:5F:A7:BE:7C:4F:5C:75:6F:30:17:B3:A8:C4:88:C3:65:3E:91:79
    /// (a test checks it). It's what Apple's own app-store-server-library is given as its root.
    /// </summary>
    public const string AppleRootCaG3Pem = """
        -----BEGIN CERTIFICATE-----
        MIICQzCCAcmgAwIBAgIILcX8iNLFS5UwCgYIKoZIzj0EAwMwZzEbMBkGA1UEAwwS
        QXBwbGUgUm9vdCBDQSAtIEczMSYwJAYDVQQLDB1BcHBsZSBDZXJ0aWZpY2F0aW9u
        IEF1dGhvcml0eTETMBEGA1UECgwKQXBwbGUgSW5jLjELMAkGA1UEBhMCVVMwHhcN
        MTQwNDMwMTgxOTA2WhcNMzkwNDMwMTgxOTA2WjBnMRswGQYDVQQDDBJBcHBsZSBS
        b290IENBIC0gRzMxJjAkBgNVBAsMHUFwcGxlIENlcnRpZmljYXRpb24gQXV0aG9y
        aXR5MRMwEQYDVQQKDApBcHBsZSBJbmMuMQswCQYDVQQGEwJVUzB2MBAGByqGSM49
        AgEGBSuBBAAiA2IABJjpLz1AcqTtkyJygRMc3RCV8cWjTnHcFBbZDuWmBSp3ZHtf
        TjjTuxxEtX/1H7YyYl3J6YRbTzBPEVoA/VhYDKX1DyxNB0cTddqXl5dvMVztK517
        IDvYuVTZXpmkOlEKMaNCMEAwHQYDVR0OBBYEFLuw3qFYM4iapIqZ3r6966/ayySr
        MA8GA1UdEwEB/wQFMAMBAf8wDgYDVR0PAQH/BAQDAgEGMAoGCCqGSM49BAMDA2gA
        MGUCMQCD6cHEFl4aXTQY2e3v9GwOAEZLuN+yRhHFD/3meoyhpmvOwgPUnPWTxnS4
        at+qIxUCMG1mihDK1A3UT82NQz60imOlM27jbdoXt2QfyFMm+YhidDkLF1vLUagM
        6BgD56KyKA==
        -----END CERTIFICATE-----
        """;

    public static AppStoreTrust Apple() =>
        new([X509Certificate2.CreateFromPem(AppleRootCaG3Pem)], checkRevocation: true);
}

/// <summary>
/// Reads JWS that Apple signed: StoreKit 2 transactions, renewal info and Server Notifications V2.
/// Checks what Apple's app-store-server-library's SignedDataVerifier checks: ES256; an <c>x5c</c> of
/// leaf, intermediate and root; the leaf marked with Apple's OID 1.2.840.113635.100.6.11.1 and the
/// intermediate with 1.2.840.113635.100.6.2.1; a chain from the leaf through that intermediate to a
/// trusted root (Apple Root CA - G3), valid now and, online, not revoked; the signature by the leaf's
/// key; and the payload's bundle id. The root in <c>x5c</c> is never trusted for itself.
///
/// <para>Never logs the JWS, or anything from inside one.</para>
/// </summary>
public sealed class AppStoreSignedData(AppStoreTrust trust, IConfiguration config, ILogger<AppStoreSignedData> logger) : IAppStoreSignedData
{
    public const string LeafOid = "1.2.840.113635.100.6.11.1";
    public const string IntermediateOid = "1.2.840.113635.100.6.2.1";
    private const int MaxLength = 64 * 1024;

    private string? BundleId => config["AppStore:BundleId"] is { Length: > 0 } b ? b : null;

    public AppStoreTransaction Transaction(string jws)
    {
        using var doc = Verify(jws);
        var t = ReadTransaction(doc.RootElement);
        if (t.BundleId != BundleId) throw Refused("That purchase is for another app.", "app_store_wrong_app");
        return t;
    }

    public AppStoreRenewalInfo RenewalInfo(string jws)
    {
        using var doc = Verify(jws);
        var j = doc.RootElement;
        return new AppStoreRenewalInfo(Long(j, "autoRenewStatus") == 1, Ms(j, "gracePeriodExpiresDate"), Str(j, "autoRenewProductId"));
    }

    public AppStoreNotification Notification(string jws)
    {
        using var doc = Verify(jws);
        var j = doc.RootElement;
        var type = Str(j, "notificationType") ?? throw Refused("That notification can't be read.", "app_store_bad_notification");
        if (!j.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            return new AppStoreNotification(type, Str(j, "subtype"), null, null, null, null);
        var bundle = Str(data, "bundleId");
        if (bundle != BundleId) throw Refused("That notification is for another app.", "app_store_wrong_app");
        // The transaction inside is signed on its own, and checked on its own.
        var transaction = Str(data, "signedTransactionInfo") is { Length: > 0 } inner ? Transaction(inner) : null;
        return new AppStoreNotification(type, Str(j, "subtype"), bundle, Str(data, "environment"), Long(data, "appAppleId"), transaction);
    }

    // ----- The check -----------------------------------------------------------------------------

    private JsonDocument Verify(string jws)
    {
        if (string.IsNullOrEmpty(jws) || jws.Length > MaxLength) throw Bad();
        var parts = jws.Split('.');
        if (parts.Length != 3 || parts.Any(p => p.Length == 0)) throw Bad();

        string[] x5c;
        try
        {
            using var header = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(parts[0]));
            var h = header.RootElement;
            // Only ES256: no "none", no algorithm the leaf's key wasn't made for.
            if (Str(h, "alg") != "ES256") throw Bad();
            if (!h.TryGetProperty("x5c", out var chain) || chain.ValueKind != JsonValueKind.Array) throw Bad();
            x5c = chain.EnumerateArray().Select(c => c.GetString() ?? "").ToArray();
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException or InvalidOperationException)
        {
            throw Bad();
        }
        if (x5c.Length != 3) throw Bad();

        X509Certificate2 leaf, intermediate;
        try
        {
            leaf = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(x5c[0]));
            intermediate = X509CertificateLoader.LoadCertificate(Convert.FromBase64String(x5c[1]));
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            throw Bad();
        }
        using (leaf)
        using (intermediate)
        {
            if (leaf.Extensions[LeafOid] is null || intermediate.Extensions[IntermediateOid] is null)
                throw Refused("That purchase isn't signed by Apple.", "app_store_bad_signature");
            CheckChain(leaf, intermediate);

            using var key = leaf.GetECDsaPublicKey() ?? throw Refused("That purchase isn't signed by Apple.", "app_store_bad_signature");
            if (key.KeySize != 256) throw Refused("That purchase isn't signed by Apple.", "app_store_bad_signature");
            byte[] signature;
            try { signature = Base64UrlEncoder.DecodeBytes(parts[2]); }
            catch (FormatException) { throw Bad(); }
            var signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
            if (signature.Length != 64
                || !key.VerifyData(signed, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation))
                throw Refused("That purchase isn't signed by Apple.", "app_store_bad_signature");
        }

        try { return JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(parts[1])); }
        catch (Exception ex) when (ex is JsonException or FormatException) { throw Bad(); }
    }

    private void CheckChain(X509Certificate2 leaf, X509Certificate2 intermediate)
    {
        using var chain = new X509Chain();
        var policy = chain.ChainPolicy;
        policy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        foreach (var root in trust.Roots) policy.CustomTrustStore.Add(root);
        policy.ExtraStore.Add(intermediate);
        // Nothing fetched from addresses inside the certificates: only what was sent, against our roots.
        policy.DisableCertificateDownloads = true;
        policy.RevocationMode = trust.CheckRevocation ? X509RevocationMode.Online : X509RevocationMode.NoCheck;
        policy.RevocationFlag = X509RevocationFlag.ExcludeRoot;
        policy.VerificationTime = DateTime.Now;
        policy.UrlRetrievalTimeout = TimeSpan.FromSeconds(10);

        var built = chain.Build(leaf);
        var problems = chain.ChainStatus.Aggregate(X509ChainStatusFlags.NoError, (all, s) => all | s.Status);
        const X509ChainStatusFlags couldntAsk = X509ChainStatusFlags.RevocationStatusUnknown | X509ChainStatusFlags.OfflineRevocation;
        if (!built && (problems & ~couldntAsk) == X509ChainStatusFlags.NoError && problems != X509ChainStatusFlags.NoError)
        {
            // A good chain whose revocation couldn't be checked: not a "no", but not a "yes" either.
            logger.LogWarning("Apple's certificate revocation check didn't answer.");
            throw new AppStoreUnavailableException("We couldn't check this with Apple just now. Your purchase is safe, and we'll try again later.", "app_store_unreachable");
        }
        if (!built)
        {
            logger.LogWarning("An App Store signature was refused: {Problems}.", problems);
            throw Refused("That purchase isn't signed by Apple.", "app_store_bad_signature");
        }

        // Exactly leaf, the intermediate that was sent, and one of our roots.
        var elements = chain.ChainElements;
        if (elements.Count != 3
            || !elements[1].Certificate.RawDataMemory.Span.SequenceEqual(intermediate.RawDataMemory.Span)
            || !trust.Roots.Any(r => r.RawDataMemory.Span.SequenceEqual(elements[2].Certificate.RawDataMemory.Span)))
            throw Refused("That purchase isn't signed by Apple.", "app_store_bad_signature");
    }

    // ----- Reading -------------------------------------------------------------------------------

    internal static AppStoreTransaction ReadTransaction(JsonElement j)
    {
        var id = Str(j, "transactionId");
        var original = Str(j, "originalTransactionId") ?? id;
        var product = Str(j, "productId");
        if (string.IsNullOrEmpty(id) || string.IsNullOrEmpty(original) || string.IsNullOrEmpty(product))
            throw Refused("That purchase can't be read.", "app_store_bad_transaction", HttpStatusCode.BadRequest);
        Guid? account = Guid.TryParse(Str(j, "appAccountToken"), out var a) ? a : null;
        // Milliunits of the storefront currency (price 15990 = 15.99).
        decimal? price = Long(j, "price") is { } milli && milli >= 0 ? milli / 1000m : null;
        return new AppStoreTransaction(id, original, product, Str(j, "bundleId") ?? "", account,
            Str(j, "environment") ?? "", Str(j, "type"),
            Ms(j, "purchaseDate"), Ms(j, "expiresDate"), Ms(j, "revocationDate"),
            price, Str(j, "currency"));
    }

    private static string? Str(JsonElement j, string name) =>
        j.TryGetProperty(name, out var v) ? v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        } : null;

    private static long? Long(JsonElement j, string name) =>
        j.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var l) ? l
        : j.TryGetProperty(name, out var s) && s.ValueKind == JsonValueKind.String
          && long.TryParse(s.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : null;

    private static DateTimeOffset? Ms(JsonElement j, string name) =>
        Long(j, name) is { } ms && ms > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;

    private static AppStoreRefusedException Bad() =>
        Refused("That purchase can't be read.", "app_store_bad_transaction", HttpStatusCode.BadRequest);

    private static AppStoreRefusedException Refused(string message, string code, HttpStatusCode status = HttpStatusCode.Forbidden) =>
        new(message, code, status);
}
