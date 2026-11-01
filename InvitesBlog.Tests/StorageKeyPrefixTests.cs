using InvitesBlog.Application.Abstractions;
using InvitesBlog.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>Skipped, not passed, without an S3 server to run against.</summary>
public sealed class S3FactAttribute : FactAttribute
{
    public S3FactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("IB_TEST_S3"))) Skip = "Set IB_TEST_S3 to run against an S3 server.";
    }
}

public class StorageKeyPrefixTests
{
    [Theory]
    [InlineData("buckets/0123abc/", true)]
    [InlineData("templates/my-design@", true)]
    [InlineData("", false)]
    [InlineData("/", false)]
    [InlineData("buckets/", false)]
    [InlineData("templates/@", false)]
    [InlineData("buckets", false)]
    [InlineData("buckets/abc", false)]
    [InlineData("/buckets/abc/", false)]
    [InlineData("buckets/../", false)]
    public void Only_a_named_folder_or_a_templates_versions_can_be_removed_whole(string prefix, bool safe) =>
        Assert.Equal(safe, StorageKeyPrefix.IsSafe(prefix));

    /// <summary>The path production takes. Needs an S3 server: set IB_TEST_S3 to its endpoint (key and secret in IB_TEST_S3_KEY, IB_TEST_S3_SECRET).</summary>
    [S3Fact]
    public async Task On_s3_removing_a_prefix_lists_and_removes_only_what_starts_with_it()
    {
        var endpoint = Environment.GetEnvironmentVariable("IB_TEST_S3");
        var key = Environment.GetEnvironmentVariable("IB_TEST_S3_KEY");
        var secret = Environment.GetEnvironmentVariable("IB_TEST_S3_SECRET");
        var bucket = "t" + Guid.NewGuid().ToString("N");
        using var admin = new Amazon.S3.AmazonS3Client(key, secret, new Amazon.S3.AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true });
        await admin.PutBucketAsync(bucket);
        var store = new S3StorageService(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Storage:Endpoint"] = endpoint, ["Storage:AccessKey"] = key, ["Storage:SecretKey"] = secret,
            ["Storage:Bucket"] = bucket, ["Storage:Region"] = "us-east-1",
        }).Build());
        for (var i = 0; i < 1005; i++) await store.PutAsync($"buckets/one/media/{i}.jpg", [1], "image/jpeg");
        await store.PutAsync("buckets/onetwo/keep.jpg", [1], "image/jpeg");

        await store.DeletePrefixAsync("buckets/one/");

        var left = await admin.ListObjectsV2Async(new Amazon.S3.Model.ListObjectsV2Request { BucketName = bucket });
        Assert.Equal("buckets/onetwo/keep.jpg", Assert.Single(left.S3Objects).Key);
    }

    [Fact]
    public async Task Removing_a_prefix_takes_only_what_starts_with_it()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var store = new LocalFileStorageService(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { ["Storage:LocalRoot"] = root }).Build());
            await store.PutAsync("templates/ab@1.0.0/index.html", [1], "text/html");
            await store.PutAsync("templates/ab@2.0.0/index.html", [1], "text/html");
            await store.PutAsync("templates/abc@1.0.0/index.html", [1], "text/html");
            await store.PutAsync("buckets/one/media/a.jpg", [1], "image/jpeg");
            await store.PutAsync("buckets/one/qr/b.png", [1], "image/png");
            await store.PutAsync("buckets/onetwo/media/c.jpg", [1], "image/jpeg");

            await store.DeletePrefixAsync("templates/ab@");
            await store.DeletePrefixAsync("buckets/one/");

            Assert.Null(await store.GetAsync("templates/ab@1.0.0/index.html"));
            Assert.Null(await store.GetAsync("templates/ab@2.0.0/index.html"));
            Assert.NotNull(await store.GetAsync("templates/abc@1.0.0/index.html"));
            Assert.Null(await store.GetAsync("buckets/one/media/a.jpg"));
            Assert.Null(await store.GetAsync("buckets/one/qr/b.png"));
            Assert.NotNull(await store.GetAsync("buckets/onetwo/media/c.jpg"));

            await Assert.ThrowsAsync<ArgumentException>(() => store.DeletePrefixAsync("buckets/"));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
