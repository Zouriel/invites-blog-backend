using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using InvitesBlog.Application.Abstractions;
using Microsoft.Extensions.Configuration;

namespace InvitesBlog.Infrastructure.Storage;

/// <summary>
/// Local-filesystem storage for dev: writes objects under a root the API serves statically at
/// <c>/assets</c>. Lets the whole pipeline run without MinIO/S3 (§7.1 "MinIO locally").
/// </summary>
public sealed class LocalFileStorageService : IStorageService
{
    private readonly string _root;
    private readonly string _publicBase;

    public LocalFileStorageService(IConfiguration config)
    {
        _root = config["Storage:LocalRoot"]
                ?? Path.Combine(AppContext.BaseDirectory, "wwwroot", "assets");
        _publicBase = (config["Urls:AssetsBase"] ?? "http://localhost:8080/assets").TrimEnd('/');
        Directory.CreateDirectory(_root);
    }

    public async Task<string> PutAsync(string key, byte[] content, string contentType, CancellationToken ct = default)
    {
        var path = Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        await File.WriteAllBytesAsync(path, content, ct);
        return PublicUrl(key);
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken ct = default)
    {
        var path = Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(path) ? await File.ReadAllBytesAsync(path, ct) : null;
    }

    public string PublicUrl(string key) => $"{_publicBase}/{key.TrimStart('/')}";
}

/// <summary>
/// S3-compatible storage, selected when <c>Storage:Provider</c> is Minio, S3 or R2. One class for all
/// three because they are one protocol: what differs is the endpoint, the region, and whether object
/// ACLs exist — all configuration.
///
/// <para><b>Cloudflare R2.</b> Point <c>Storage:Endpoint</c> at
/// <c>https://&lt;accountid&gt;.r2.cloudflarestorage.com</c> and set <c>Storage:Region=auto</c>, which
/// is the only region R2 accepts. Public reads come from a public bucket URL, a custom domain, or a
/// Worker — R2 has <b>no per-object ACLs</b>, so nothing here asks for one, and the MinIO deployment's
/// anonymous-download bucket policy has no R2 equivalent to port.</para>
///
/// <para><b>The URLs it returns are relative in production</b> (<c>Urls:AssetsBase=/assets</c>), so the
/// database holds <c>/assets/campaigns/…</c> and never a host. That is the single property that makes
/// swapping MinIO for R2 a config change rather than a data migration. Do not "improve" this into
/// absolute URLs.</para>
/// </summary>
public sealed class S3StorageService : IStorageService
{
    private readonly IAmazonS3 _s3;
    private readonly string _bucket;
    private readonly string _publicBase;
    private readonly bool _unsignedPayload;

    public S3StorageService(IConfiguration config)
    {
        _bucket = config["Storage:Bucket"] ?? "invites-assets";
        var endpoint = config["Storage:Endpoint"] ?? "http://localhost:9000";
        _publicBase = (config["Urls:AssetsBase"] ?? $"{endpoint}/{_bucket}").TrimEnd('/');

        var s3Config = new AmazonS3Config
        {
            ServiceURL = endpoint,
            // R2's account endpoint is path-style, and so is MinIO. Left configurable for the one
            // backend that isn't — a bucket reached through its own custom domain.
            ForcePathStyle = !bool.TryParse(config["Storage:VirtualHostStyle"], out var vhost) || !vhost,
        };

        // SigV4 needs a region to sign with even when the backend has only one. R2 requires the
        // literal "auto"; MinIO ignores it. AuthenticationRegion rather than RegionEndpoint because a
        // custom ServiceURL leaves the SDK nothing to infer from, and the resulting mis-signed request
        // fails with a signature error that reads exactly like bad credentials.
        var region = config["Storage:Region"];
        if (!string.IsNullOrWhiteSpace(region)) s3Config.AuthenticationRegion = region;

        // AWS SDK v4 adds a CRC32 trailer to uploads by default, which makes the request
        // `STREAMING-AWS4-HMAC-SHA256-PAYLOAD-TRAILER`. **R2 does not implement that** and rejects
        // every PutObject with exactly that string as the message — so this line is what stands
        // between a working bucket and a storage backend where nothing can be written at all.
        //
        // WHEN_REQUIRED still sends a checksum for the operations that mandate one; it only stops the
        // SDK volunteering one where the protocol does not ask. Harmless against MinIO and real S3,
        // which both accept either form.
        s3Config.RequestChecksumCalculation = RequestChecksumCalculation.WHEN_REQUIRED;
        s3Config.ResponseChecksumValidation = ResponseChecksumValidation.WHEN_REQUIRED;

        // R2 implements neither form of streaming payload signature — not
        // STREAMING-AWS4-HMAC-SHA256-PAYLOAD-TRAILER (killed by the two lines above) and not plain
        // STREAMING-AWS4-HMAC-SHA256-PAYLOAD either. The only body encoding it accepts is
        // UNSIGNED-PAYLOAD, which is what DisablePayloadSigning switches to.
        //
        // Conditional on the scheme, and that is not a detail: the SDK REFUSES to send an unsigned
        // payload over plain http, and MinIO is reached internally over http://. Setting it
        // unconditionally would fix R2 and break the backend we are migrating away from — including
        // the rollback. TLS is what makes the signature redundant, so keying on it is also correct
        // rather than merely convenient.
        _unsignedPayload = endpoint.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

        _s3 = new AmazonS3Client(
            config["Storage:AccessKey"] ?? "minio",
            config["Storage:SecretKey"] ?? "minio_password",
            s3Config);
    }

    public async Task<string> PutAsync(string key, byte[] content, string contentType, CancellationToken ct = default)
    {
        using var ms = new MemoryStream(content);
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            InputStream = ms,
            ContentType = contentType,
            // See the constructor: required by R2, impossible over MinIO's plain http.
            DisablePayloadSigning = _unsignedPayload,
            // Carried by the object, not by whatever proxy happens to be in front of it (see
            // StorageCache). This is what survives /assets/* moving off Caddy onto an R2 domain.
            Headers = { CacheControl = StorageCache.For(key) }
        }, ct);
        return PublicUrl(key);
    }

    public async Task<byte[]?> GetAsync(string key, CancellationToken ct = default)
    {
        try
        {
            using var res = await _s3.GetObjectAsync(new GetObjectRequest { BucketName = _bucket, Key = key }, ct);
            using var ms = new MemoryStream();
            await res.ResponseStream.CopyToAsync(ms, ct);
            return ms.ToArray();
        }
        catch (AmazonS3Exception ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }
    }

    public string PublicUrl(string key) => $"{_publicBase}/{key.TrimStart('/')}";
}
