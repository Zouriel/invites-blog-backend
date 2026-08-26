using InvitesBlog.Infrastructure.Storage;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>
/// Which objects may be cached forever and which may not. Getting this backwards is not a subtle
/// bug: it either serves a corrected poster stale for a day, or makes every guest re-download a
/// wedding's photographs on every scroll.
/// </summary>
public class StorageCacheTests
{
    [Theory]
    [InlineData("templates/gilded-hour@1.0.0/index.html")]
    [InlineData("/templates/a-love-story@1.1.0/index.html")]
    [InlineData("submissions/4f2c/manifest.json")]
    public void A_package_republished_at_the_same_url_must_revalidate(string key) =>
        Assert.Equal("no-cache, must-revalidate", StorageCache.For(key));

    [Theory]
    [InlineData("campaigns/8f2c/images/9ab.jpg")]
    [InlineData("campaigns/8f2c/photos/1de_t.jpg")]
    public void Content_addressed_campaign_files_are_immutable(string key) =>
        Assert.Equal("public, max-age=31536000, immutable", StorageCache.For(key));

    /// <summary>An unrecognised key takes the campaign rule, and every key we mint is GUID-named.</summary>
    [Fact]
    public void An_unknown_key_is_not_left_without_a_rule() =>
        Assert.False(string.IsNullOrWhiteSpace(StorageCache.For("something/else.bin")));
}
