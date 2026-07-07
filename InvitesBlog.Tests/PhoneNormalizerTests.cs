using InvitesBlog.Application.Phones;
using Xunit;

namespace InvitesBlog.Tests;

public class PhoneNormalizerTests
{
    private readonly PhoneNormalizer _n = new();

    [Fact]
    public void Local_maldives_number_normalizes_with_default_country()
    {
        var r = _n.Normalize("7777777", "MV");
        Assert.True(r.IsUsable);
        Assert.Equal("+9607777777", r.E164);
    }

    [Fact]
    public void International_number_keeps_its_own_country_code()
    {
        var r = _n.Normalize("+14155552671", "MV");
        Assert.Equal(PhoneNormalizationOutcome.Valid, r.Outcome);
        Assert.Equal("+14155552671", r.E164);
    }

    [Fact]
    public void Impossible_number_is_flagged()
    {
        var r = _n.Normalize("123", "MV");
        Assert.Equal(PhoneNormalizationOutcome.Impossible, r.Outcome);
        Assert.Null(r.E164);
        Assert.False(r.IsUsable);
    }

    [Fact]
    public void Scientific_notation_is_recovered_with_warning()
    {
        var r = _n.Normalize("9.607777777E9", "MV");
        Assert.True(r.IsUsable);
        Assert.Equal("+9607777777", r.E164);
        Assert.NotNull(r.Warning);
        Assert.Contains("scientific", r.Warning!, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Empty_input_returns_empty_outcome()
    {
        var r = _n.Normalize("  ", "MV");
        Assert.Equal(PhoneNormalizationOutcome.Empty, r.Outcome);
    }

    [Fact]
    public void Same_number_two_formats_normalize_equal()
    {
        // Inbox matching relies on this (§4.4.4 point 5).
        var local = _n.Normalize("7777777", "MV").E164;
        var intl = _n.Normalize("+960 777 7777", "MV").E164;
        Assert.Equal(local, intl);
    }
}
