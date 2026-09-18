using InvitesBlog.Application.Services.Designs;
using Xunit;

namespace InvitesBlog.Tests.Services;

public class TemplateVersionTests
{
    [Theory]
    [InlineData("1.0.0", "1.0.1")]
    [InlineData("2.7.9", "2.7.10")]
    [InlineData("nonsense", "1.0.1")]
    public void Version_bump_increments_the_patch_component(string current, string expected) =>
        Assert.Equal(expected, TemplateVersion.Next(current));
}
