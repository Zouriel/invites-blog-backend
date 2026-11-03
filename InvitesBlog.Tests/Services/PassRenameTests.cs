using System.Text.Json;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Plans;
using InvitesBlog.Application.Services.Billing;
using InvitesBlog.Domain.Enums;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>
/// Party became Gold and Wedding became Platinum in name only. What's stored as a number must keep its
/// number, and what older clients still send must keep working.
/// </summary>
public class PassRenameTests
{
    [Fact]
    public void Stored_numbers_did_not_move()
    {
        Assert.Equal(0, (int)EventPassKind.None);
        Assert.Equal(1, (int)EventPassKind.Gold);
        Assert.Equal(2, (int)EventPassKind.Platinum);

        Assert.Equal(0, (int)PaymentKind.Initial);
        Assert.Equal(1, (int)PaymentKind.TopUp);
        Assert.Equal(2, (int)PaymentKind.GoldPass);
        Assert.Equal(3, (int)PaymentKind.PlatinumPass);
        Assert.Equal(4, (int)PaymentKind.KeepPhotos);
        Assert.Equal(5, (int)PaymentKind.Sending);
        Assert.Equal(6, (int)PaymentKind.PremiumMonthly);
        Assert.Equal(7, (int)PaymentKind.StudioYearly);
        Assert.Equal(8, (int)PaymentKind.StudioGoldCredits);
        Assert.Equal(9, (int)PaymentKind.StudioPlatinumCredits);
        Assert.Equal(10, (int)PaymentKind.GoldExtension);
        Assert.Equal(11, (int)PaymentKind.PlatinumExtension);
    }

    [Theory]
    [InlineData("gold-pass", PaymentKind.GoldPass)]
    [InlineData("platinum-pass", PaymentKind.PlatinumPass)]
    [InlineData("gold-extension", PaymentKind.GoldExtension)]
    [InlineData("platinum-extension", PaymentKind.PlatinumExtension)]
    // What a tab from before the deploy posts.
    [InlineData("party-pass", PaymentKind.GoldPass)]
    [InlineData("wedding-pass", PaymentKind.PlatinumPass)]
    [InlineData("party-extension", PaymentKind.GoldExtension)]
    [InlineData("wedding-extension", PaymentKind.PlatinumExtension)]
    [InlineData(" PARTY-PASS ", PaymentKind.GoldPass)]
    public void Billing_keys_old_and_new_buy_the_same_thing(string item, PaymentKind kind) =>
        Assert.Equal(kind, BillingService.ParseItem(item));

    [Fact]
    public void Only_the_new_keys_are_handed_out()
    {
        Assert.Equal("gold-pass", BillingService.ItemName(PaymentKind.GoldPass));
        Assert.Equal("platinum-pass", BillingService.ItemName(PaymentKind.PlatinumPass));
        Assert.Equal("gold-extension", BillingService.ItemName(PaymentKind.GoldExtension));
        Assert.Equal("platinum-extension", BillingService.ItemName(PaymentKind.PlatinumExtension));
    }

    [Fact]
    public void Anything_else_is_still_refused() =>
        Assert.Throws<BusinessRuleException>(() => BillingService.ParseItem("silver-pass"));

    [Fact]
    public void An_offer_answers_under_both_names_for_tabs_from_before_the_deploy()
    {
        var json = JsonSerializer.SerializeToElement(new PassOfferDto(1, 2, 3, 4, 5, 6), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        foreach (var (now, before, value) in new[]
        {
            ("goldPass", "partyPass", 1m), ("platinumPass", "weddingPass", 2m),
            ("fullGoldPass", "fullPartyPass", 3m), ("fullPlatinumPass", "fullWeddingPass", 4m),
            ("goldExtension", "partyExtension", 5m), ("platinumExtension", "weddingExtension", 6m),
        })
        {
            Assert.Equal(value, json.GetProperty(now).GetDecimal());
            Assert.Equal(value, json.GetProperty(before).GetDecimal());
        }
    }

    [Fact]
    public void The_catalogue_names_the_new_plans_with_the_same_limits_and_prices()
    {
        var plans = PlanCatalog.Describe().Plans;
        var gold = plans.Single(p => p.Kind == "GoldPass");
        var platinum = plans.Single(p => p.Kind == "PlatinumPass");
        Assert.Equal("Gold pass", gold.Name);
        Assert.Equal("Platinum pass", platinum.Name);
        Assert.Equal((199m, 99m, 5 * PlanCatalog.Gb, 2, 3, 100), (gold.Price, gold.ExtensionPrice!.Value, gold.EventBytes!.Value, gold.MaxBuckets!.Value, gold.MaxWindowDays!.Value, gold.IncludedInvites));
        Assert.Equal((499m, 349m, 10 * PlanCatalog.Gb, 5, 5, 500), (platinum.Price, platinum.ExtensionPrice!.Value, platinum.EventBytes!.Value, platinum.MaxBuckets!.Value, platinum.MaxWindowDays!.Value, platinum.IncludedInvites));
    }
}
