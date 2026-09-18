using InvitesBlog.Application.Common;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>
/// A missing Urls key means local development — never production. A few readers used to fall back
/// to the live hosts, so a dev box without the key sent people to the real site.
/// </summary>
public class AppUrlsTests
{
    private static IConfiguration Config(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();

    [Fact]
    public void Missing_keys_fall_back_to_the_local_dev_servers()
    {
        var config = Config(new());

        Assert.Equal("http://localhost:4200", config.InviterBase());
        Assert.Equal("http://localhost:4201", config.InviteeBase());
    }

    [Fact]
    public void Configured_keys_win_without_a_trailing_slash()
    {
        var config = Config(new()
        {
            ["Urls:InviterBase"] = "https://invites.blog/",
            ["Urls:InviteeBase"] = "https://me.invites.blog",
        });

        Assert.Equal("https://invites.blog", config.InviterBase());
        Assert.Equal("https://me.invites.blog", config.InviteeBase());
    }
}
