using InvitesBlog.Application.Plans;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>
/// Pricing 2026-11: every plan's card bullets come from the server, the same on the website and in both apps, and
/// the AI designer is an add-on to Premium, offered beside the plans.
/// </summary>
public class PlanPerksTests
{
    private static PlanDto Plan(string kind) => PlanCatalog.Describe().Plans.Single(p => p.Kind == kind);

    [Fact]
    public void Every_plan_has_its_perks_written_by_the_server()
    {
        foreach (var plan in PlanCatalog.Describe().Plans)
        {
            Assert.NotNull(plan.Perks);
            Assert.NotEmpty(plan.Perks!);
            // Plain sentences with no prices, so the apps can show them under the store rules.
            Assert.DoesNotContain(plan.Perks!, p => p.Contains("MVR") || p.Contains('$'));
        }
    }

    [Fact]
    public void Premium_lists_every_paid_perk_but_not_the_ai_designer()
    {
        var premium = Plan("Premium");
        Assert.Equal("Premium", premium.Name);
        Assert.Equal(77m, premium.Price);
        Assert.Equal(
        [
            "2 GB for every event you organise",
            "100 emailed invitations a month, across your events",
            "No \"Made with invites.blog\" on your events",
            "Everything kept while you're subscribed",
            "Group photos by face on your events",
            "Download all, or more than 10 at once, in any album you can see",
            "Download all your Favourites at once",
            "Your name on the invitations you design",
        ], premium.Perks);
        Assert.DoesNotContain(premium.Perks!, p => p.Contains("AI", StringComparison.Ordinal));
    }

    [Fact]
    public void Studio_has_the_ai_designer_included_and_download_all()
    {
        var studio = Plan("Venue").Perks!;
        Assert.Contains(PlanPerks.DesignerAiIncluded, studio);
        Assert.Contains(PlanPerks.DownloadAnywhere, studio);
        Assert.Contains("10 GB for every event you organise", studio);
    }

    [Fact]
    public void Subscriptions_include_emailed_invitations_a_month_and_passes_keep_theirs_per_event()
    {
        // Owner, 2026-11-14 (Studio capped at 500 the same day).
        Assert.Equal((100, 500, 500), (Plan("Premium").MonthlyEmails, Plan("Photographer").MonthlyEmails, Plan("Venue").MonthlyEmails));
        Assert.Contains("500 emailed invitations a month, across your events", Plan("Photographer").Perks!);
        Assert.Contains("500 emailed invitations a month, across your events", Plan("Venue").Perks!);
        Assert.Contains("100 emailed invitations", Plan("GoldPass").Perks!);
        Assert.Contains("500 emailed invitations", Plan("PlatinumPass").Perks!);
        Assert.Equal((0, 0, 0), (Plan("Free").MonthlyEmails, Plan("GoldPass").MonthlyEmails, Plan("PlatinumPass").MonthlyEmails));
    }

    [Fact]
    public void Free_is_ten_at_a_time_and_passes_download_all_for_their_event()
    {
        Assert.Contains("Download or share up to 10 at a time", Plan("Free").Perks!);
        Assert.Contains("Download all, for everyone in the event", Plan("GoldPass").Perks!);
        Assert.Contains("Download all, for everyone in the event", Plan("PlatinumPass").Perks!);
        Assert.Contains("5 GB for photos and videos", Plan("GoldPass").Perks!);
        Assert.Contains("1 album with 300 MB for photos and videos", Plan("Free").Perks!);
    }

    [Fact]
    public void The_ai_designer_is_offered_as_an_add_on_to_premium_at_the_price_books_price()
    {
        var catalog = PlanCatalog.Describe(Prices.Defaults with { DesignerAiMonthly = 80m });
        var addOn = Assert.Single(catalog.AddOns!);
        Assert.Equal(("designer-ai", "AI designer", 80m, "per month", 5m), (addOn.Key, addOn.Name, addOn.Price, addOn.Billing, addOn.Usd));
        Assert.Equal(["Premium"], addOn.ForKinds);
        Assert.Equal(10, catalog.DownloadLimit);
    }
}
