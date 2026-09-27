using InvitesBlog.Application.Common;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>A QR code's link, kept so the host can copy it again, readable only with the server's secret.</summary>
public class TokenSealTests
{
    private const string Secret = "a-long-server-secret-0123456789-abcdefghij";

    [Fact]
    public void A_sealed_token_opens_with_the_same_secret()
    {
        var sealedText = TokenSeal.Seal("SbA5r1eWQwTb9ErLO4uB", Secret, "qr-token-v1");

        Assert.DoesNotContain("SbA5r1eWQwTb9ErLO4uB", sealedText);
        Assert.Equal("SbA5r1eWQwTb9ErLO4uB", TokenSeal.Open(sealedText, Secret, "qr-token-v1"));
    }

    /// <summary>A copy of the database alone is useless: without the server's secret nothing opens.</summary>
    [Fact]
    public void Another_secret_or_purpose_or_a_tampered_value_opens_nothing()
    {
        var sealedText = TokenSeal.Seal("token", Secret, "qr-token-v1");
        var tampered = Convert.ToBase64String(Convert.FromBase64String(sealedText).Select((b, i) => i == 20 ? (byte)(b ^ 1) : b).ToArray());

        Assert.Null(TokenSeal.Open(sealedText, "some-other-secret-0123456789", "qr-token-v1"));
        Assert.Null(TokenSeal.Open(sealedText, Secret, "another-purpose"));
        Assert.Null(TokenSeal.Open(tampered, Secret, "qr-token-v1"));
        Assert.Null(TokenSeal.Open("not base64!", Secret, "qr-token-v1"));
        Assert.Null(TokenSeal.Open(null, Secret, "qr-token-v1"));
    }

    [Fact]
    public void The_same_token_seals_differently_each_time()
    {
        Assert.NotEqual(TokenSeal.Seal("token", Secret, "p"), TokenSeal.Seal("token", Secret, "p"));
    }
}
