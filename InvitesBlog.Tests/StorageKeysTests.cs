using InvitesBlog.Infrastructure.Storage;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>A URL the storage handed out turns back into its key; nothing else does.</summary>
public class StorageKeysTests
{
    [Theory]
    [InlineData("/assets/buckets/abc/media/1_o.jpg", "buckets/abc/media/1_o.jpg")]
    [InlineData("/assets/avatars/x/y.jpg?v=2", "avatars/x/y.jpg")]
    public void Its_own_urls_map_back_to_their_keys(string url, string key) =>
        Assert.Equal(key, StorageKeys.KeyFor("/assets", url));

    [Theory]
    [InlineData("https://lh3.googleusercontent.com/a/x")]
    [InlineData("/templates/x/index.html")]
    [InlineData("/assets/../secrets")]
    [InlineData("/assets/")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_is_not_ours_to_delete(string? url) =>
        Assert.Null(StorageKeys.KeyFor("/assets", url));
}
