using InvitesBlog.Application.Services.Billing;
using Xunit;

namespace InvitesBlog.Tests.Services;

public sealed class RenewalEmailsTimeZoneTests
{
    // 23:30 UTC on 31 October 2026 is still the 31st in London (GMT after the clocks went back on
    // 25 October) but already 1 November in the Maldives (+05).
    private static readonly DateTimeOffset LateOnHalloween = new(2026, 10, 31, 23, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Renewal_date_is_the_accounts_own_day()
    {
        Assert.Equal("31 October 2026", RenewalEmails.Day(LateOnHalloween, "Europe/London"));
        Assert.Equal("1 November 2026", RenewalEmails.Day(LateOnHalloween, "Indian/Maldives"));
    }

    [Fact]
    public void Accounts_without_a_zone_keep_Maldives_time()
    {
        Assert.Equal("1 November 2026", RenewalEmails.Day(LateOnHalloween, null));
    }

    [Fact]
    public void Renewed_email_names_the_day_in_the_accounts_zone()
    {
        var mail = RenewalEmails.Renewed("a@example.com", "Premium", 200m, "MVR", LateOnHalloween, "https://invites.blog/billing", "Europe/London");
        Assert.Contains("31 October 2026", mail.Html);
    }
}
