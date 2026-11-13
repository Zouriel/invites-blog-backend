using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Plans;
using InvitesBlog.Domain.Entities;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>
/// Prices an admin saved are what everything charges; a saved row missing a price (one added to
/// code later) falls back to that price's default, and nonsense is refused before it is stored.
/// </summary>
[Collection("PriceBook")]
public class PriceBookTests
{
    private readonly IRepository<AppSetting> _settings = Substitute.For<IRepository<AppSetting>>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();

    public PriceBookTests() => PriceBook.ClearCache();

    private PriceBook Sut(params AppSetting[] rows)
    {
        _settings.Query(Arg.Any<bool>()).Returns(_ => rows.AsAsyncQueryable());
        return new PriceBook(_settings, TestData.Empty<AuditLog>(), _uow);
    }

    [Fact]
    public async Task Without_a_saved_row_the_defaults_apply()
    {
        Assert.Equal(Prices.Defaults, await Sut().CurrentAsync());
    }

    [Fact]
    public async Task A_saved_price_wins_and_missing_ones_fall_back()
    {
        var row = new AppSetting { Key = PriceBook.Key, ValueJson = """{"GoldPass":249,"SendingPerBlock":60}""" };
        var p = await Sut(row).CurrentAsync();
        Assert.Equal(249m, p.GoldPass);
        Assert.Equal(60m, p.SendingPerBlock);
        Assert.Equal(Prices.Defaults.PlatinumPass, p.PlatinumPass);
    }

    [Fact]
    public async Task A_row_saved_before_the_ai_designer_add_on_reads_it_at_77_and_a_saved_one_wins()
    {
        var before = new AppSetting { Key = PriceBook.Key, ValueJson = """{"GoldPass":249,"PremiumMonthly":77}""" };
        Assert.Equal(77m, (await Sut(before).CurrentAsync()).DesignerAiMonthly);
        PriceBook.ClearCache();

        var saved = new AppSetting { Key = PriceBook.Key, ValueJson = """{"PremiumMonthly":80,"DesignerAiMonthly":90}""" };
        var p = await Sut(saved).CurrentAsync();
        Assert.Equal((80m, 90m), (p.PremiumMonthly, p.DesignerAiMonthly));
    }

    [Fact]
    public void An_ai_designer_price_of_nothing_cannot_be_saved()
    {
        Assert.Contains("The AI designer a month must be more than 0.", (Prices.Defaults with { DesignerAiMonthly = 0m }).Problems());
    }

    // ----- A row saved before Party and Wedding became Gold and Platinum --------------------------

    private const string OldRow = """
        {"PartyPass":249,"WeddingPass":799,"KeepPhotosYearly":160,"PremiumMonthly":210,"VenueDiscountPercent":40,
         "SendingPerBlock":60,"MvrPerUsd":15.5,"PartyExtension":120,"WeddingExtension":400,"VenueMonthly":2500}
        """;

    [Fact]
    public async Task A_row_with_the_old_names_keeps_every_custom_price()
    {
        var p = await Sut(new AppSetting { Key = PriceBook.Key, ValueJson = OldRow }).CurrentAsync();
        Assert.Equal(new Prices(249m, 799m, 160m, 210m, 40, 60m, 15.5m, 120m, 400m, 2500m), p);
    }

    [Fact]
    public async Task A_row_with_the_old_names_in_camel_case_keeps_them_too()
    {
        var row = new AppSetting { Key = PriceBook.Key, ValueJson = """{"partyPass":249,"weddingPass":799,"partyExtension":120,"weddingExtension":400}""" };
        var p = await Sut(row).CurrentAsync();
        Assert.Equal((249m, 799m, 120m, 400m), (p.GoldPass, p.PlatinumPass, p.GoldExtension, p.PlatinumExtension));
    }

    [Fact]
    public async Task When_a_row_has_both_the_new_name_wins()
    {
        var row = new AppSetting { Key = PriceBook.Key, ValueJson = """{"PartyPass":249,"GoldPass":300}""" };
        Assert.Equal(300m, (await Sut(row).CurrentAsync()).GoldPass);
    }

    [Fact]
    public async Task Saving_over_an_old_row_writes_the_new_names_and_keeps_the_prices_not_changed()
    {
        var row = new AppSetting { Key = PriceBook.Key, ValueJson = OldRow };
        var book = Sut(row);
        var before = await book.CurrentAsync();
        await book.SetAsync(before with { KeepPhotosYearly = 170m }, Guid.NewGuid());

        Assert.DoesNotContain("Party", row.ValueJson);
        Assert.DoesNotContain("Wedding", row.ValueJson);
        PriceBook.ClearCache();
        var after = await Sut(row).CurrentAsync();
        Assert.Equal(before with { KeepPhotosYearly = 170m }, after);
    }

    [Fact]
    public async Task Nonsense_is_refused_and_nothing_is_saved()
    {
        var book = Sut();
        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            book.SetAsync(Prices.Defaults with { PlatinumPass = 50 }, Guid.NewGuid()));
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Saving_is_read_back_straight_away()
    {
        var book = Sut();
        var changed = Prices.Defaults with { KeepPhotosYearly = 175m };
        await book.SetAsync(changed, Guid.NewGuid());
        Assert.Equal(175m, (await book.CurrentAsync()).KeepPhotosYearly);
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
