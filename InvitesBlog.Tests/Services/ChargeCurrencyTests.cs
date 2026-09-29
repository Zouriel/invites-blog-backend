using InvitesBlog.Application.Plans;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>
/// Prices are in rufiyaa and production charges rufiyaa. A gateway account that only takes dollars
/// (BML's UAT, on staging) is charged the same price in dollars, to the cent, at the price book's rate.
/// </summary>
public class ChargeCurrencyTests
{
    private static IConfiguration Config(string? currency) => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["Payments:ChargeCurrency"] = currency }).Build();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("MVR")]
    [InlineData("something else")]
    public void Anything_but_USD_charges_the_rufiyaa_price_unchanged(string? setting)
    {
        Assert.Equal((699m, "MVR"), ChargeCurrency.For(Config(setting), 699m, 15.42m));
    }

    [Theory]
    [InlineData(699, 45.33)]
    [InlineData(450, 29.18)]
    [InlineData(199, 12.91)]
    public void USD_charges_the_same_price_in_dollars_to_the_cent(decimal mvr, decimal usd)
    {
        Assert.Equal((usd, "USD"), ChargeCurrency.For(Config("usd"), mvr, 15.42m));
    }
}
