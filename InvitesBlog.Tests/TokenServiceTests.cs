using InvitesBlog.Application.Security;
using Xunit;

namespace InvitesBlog.Tests;

public class TokenServiceTests
{
    [Fact]
    public void GenerateToken_is_urlsafe_and_unique()
    {
        var a = TokenService.GenerateToken();
        var b = TokenService.GenerateToken();
        Assert.NotEqual(a, b);
        Assert.DoesNotContain('+', a);
        Assert.DoesNotContain('/', a);
        Assert.DoesNotContain('=', a);
        Assert.True(a.Length >= 40); // 32 bytes base64url
    }

    [Fact]
    public void Hash_is_deterministic_and_hex()
    {
        var token = TokenService.GenerateToken();
        var h1 = TokenService.Hash(token);
        var h2 = TokenService.Hash(token);
        Assert.Equal(h1, h2);
        Assert.Equal(64, h1.Length); // SHA-256 hex
        Assert.Matches("^[0-9a-f]{64}$", h1);
    }

    [Fact]
    public void Verify_matches_token_to_its_hash()
    {
        var token = TokenService.GenerateToken();
        var hash = TokenService.Hash(token);
        Assert.True(TokenService.Verify(token, hash));
        Assert.False(TokenService.Verify(TokenService.GenerateToken(), hash));
        Assert.False(TokenService.Verify("", hash));
    }

    [Fact]
    public void HashContact_normalizes_case_and_whitespace()
    {
        Assert.Equal(
            TokenService.HashContact("Aisha@Example.com"),
            TokenService.HashContact("  aisha@example.com "));
    }

    [Fact]
    public void GenerateNumericCode_has_requested_length()
    {
        var code = TokenService.GenerateNumericCode(6);
        Assert.Equal(6, code.Length);
        Assert.Matches("^[0-9]{6}$", code);
    }
}
