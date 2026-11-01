using InvitesBlog.Application.Abstractions;
using InvitesBlog.Infrastructure.Storage;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace InvitesBlog.Tests;

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
