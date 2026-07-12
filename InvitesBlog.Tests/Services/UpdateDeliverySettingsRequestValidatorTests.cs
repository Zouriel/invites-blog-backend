using InvitesBlog.Application.Dtos.Campaigns;
using InvitesBlog.Application.Validation.Campaigns;
using Xunit;

namespace InvitesBlog.Tests.Services;

public class UpdateDeliverySettingsRequestValidatorTests
{
    private readonly UpdateDeliverySettingsRequestValidator _sut = new();

    [Theory]
    [InlineData("{\"channels\":[\"share\"]}")]
    [InlineData("{\"channels\":[\"email\"],\"fallbackChannel\":null}")]
    [InlineData("{\"channels\":[\"email\",\"share\"]}")]
    [InlineData("{}")]
    public void Accepts_allowed_channels(string json)
    {
        var result = _sut.Validate(new UpdateDeliverySettingsRequest(json));
        Assert.True(result.IsValid, string.Join("; ", result.Errors));
    }

    [Theory]
    [InlineData("{\"channels\":[\"viber\"]}")]
    [InlineData("{\"channels\":[\"whatsapp\"]}")]
    [InlineData("{\"channels\":[\"email\"],\"fallbackChannel\":\"sms\"}")]
    public void Rejects_disabled_channels(string json)
    {
        Assert.False(_sut.Validate(new UpdateDeliverySettingsRequest(json)).IsValid);
    }

    [Fact]
    public void Rejects_invalid_json()
    {
        Assert.False(_sut.Validate(new UpdateDeliverySettingsRequest("not json")).IsValid);
    }
}
