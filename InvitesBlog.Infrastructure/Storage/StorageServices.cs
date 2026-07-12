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

/// <summary>S3-compatible storage (MinIO or AWS S3), selected when Storage:Provider=Minio/S3.</summary>
public sealed class S3StorageService : IStorageService
{
    private readonly IAmazonS3 _s3;
    private readonly string _bucket;
    private readonly string _publicBase;

    public S3StorageService(IConfiguration config)
    {
        _bucket = config["Storage:Bucket"] ?? "invites-assets";
        var endpoint = config["Storage:Endpoint"] ?? "http://localhost:9000";
        _publicBase = (config["Urls:AssetsBase"] ?? $"{endpoint}/{_bucket}").TrimEnd('/');
        _s3 = new AmazonS3Client(
            config["Storage:AccessKey"] ?? "minio",
            config["Storage:SecretKey"] ?? "minio_password",
            new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true });
    }

    public async Task<string> PutAsync(string key, byte[] content, string contentType, CancellationToken ct = default)
    {
        using var ms = new MemoryStream(content);
        // NB: no DisablePayloadSigning — the AWS SDK v4 rejects that over plain HTTP, and MinIO is
        // reached internally over http://. Normal SigV4 payload signing works fine against MinIO.
        await _s3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = _bucket,
            Key = key,
            InputStream = ms,
            ContentType = contentType
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
