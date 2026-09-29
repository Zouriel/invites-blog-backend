using Microsoft.Extensions.Configuration;

namespace InvitesBlog.Application.Plans;

/// <summary>
/// The currency a card is actually charged in. Prices are set in rufiyaa (<see cref="PlanCatalog.Currency"/>)
/// and that is what production charges. A gateway account that only takes dollars (BML's UAT test
/// account does) is charged the same price in dollars, at the price book's rate, when
/// <c>Payments:ChargeCurrency</c> is <c>USD</c>. The buyer is shown that dollar amount before paying.
/// </summary>
public static class ChargeCurrency
{
    public static string Of(IConfiguration config) =>
        string.Equals(config["Payments:ChargeCurrency"], "USD", StringComparison.OrdinalIgnoreCase) ? "USD" : PlanCatalog.Currency;

    /// <summary>A rufiyaa price as it is charged: unchanged in MVR, or in dollars to the cent at <paramref name="mvrPerUsd"/>.</summary>
    public static (decimal Amount, string Currency) For(IConfiguration config, decimal mvr, decimal mvrPerUsd)
    {
        var currency = Of(config);
        return currency == "USD" && mvrPerUsd > 0
            ? (Math.Round(mvr / mvrPerUsd, 2, MidpointRounding.AwayFromZero), currency)
            : (mvr, PlanCatalog.Currency);
    }
}
