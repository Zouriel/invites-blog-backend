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
        var row = new AppSetting { Key = PriceBook.Key, ValueJson = """{"PartyPass":249,"SendingPerBlock":60}""" };
        var p = await Sut(row).CurrentAsync();
        Assert.Equal(249m, p.PartyPass);
        Assert.Equal(60m, p.SendingPerBlock);
        Assert.Equal(Prices.Defaults.WeddingPass, p.WeddingPass);
    }

    [Fact]
    public async Task Nonsense_is_refused_and_nothing_is_saved()
    {
        var book = Sut();
        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            book.SetAsync(Prices.Defaults with { WeddingPass = 50 }, Guid.NewGuid()));
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
