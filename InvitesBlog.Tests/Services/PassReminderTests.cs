using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Plans;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using InvitesBlog.Infrastructure.Persistence;
using InvitesBlog.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>A month before a pass ends, then a week before: one email each, never twice, never for a draft.</summary>
public class PassReminderTests
{
    [Fact]
    public async Task A_month_then_a_week_before_once_each()
    {
        using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"reminders-{Guid.NewGuid()}").Options);
        var now = new DateTimeOffset(2026, 10, 1, 8, 0, 0, TimeSpan.Zero);
        var host = new AppUser { Id = Guid.NewGuid(), Email = "host@x.mv", DisplayName = "Host", IsActive = true };
        db.Users.Add(host);
        Campaign Event(DateTimeOffset until, CampaignStatus status = CampaignStatus.Dispatched)
        {
            var c = TestData.Campaign();
            c.Status = status;
            c.CreatedByUserId = host.Id;
            c.EventPass = EventPassKind.Wedding;
            c.EventPassUntil = until;
            db.Campaigns.Add(c);
            return c;
        }
        var inMonth = Event(now.AddDays(20));
        var inWeek = Event(now.AddDays(5));
        var later = Event(now.AddDays(90));
        var draft = Event(now.AddDays(5), CampaignStatus.Draft);
        await db.SaveChangesAsync();

        var sent = new List<EmailMessage>();
        var mail = Substitute.For<IEmailSender>();
        mail.SendAsync(Arg.Do<EmailMessage>(sent.Add), Arg.Any<CancellationToken>()).Returns(DeliveryResult.Ok("x"));
        var offers = Substitute.For<IPassOfferService>();
        offers.ForCampaignAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new PassOfferDto(199, 699, 199, 699, 99, 349));
        var sut = new PassReminderService(new CampaignRepository(db), new BaseRepository<AppUser>(db), offers, mail,
            new ConfigurationBuilder().Build(), new UnitOfWork(db));

        Assert.Equal(2, await sut.RunOnceAsync(now));
        Assert.All(sent, m => Assert.Contains("349", m.Html));
        Assert.Equal(1, (await db.Campaigns.FindAsync(inMonth.Id))!.PassNoticeStage);
        Assert.Equal(2, (await db.Campaigns.FindAsync(inWeek.Id))!.PassNoticeStage);
        Assert.Equal(0, (await db.Campaigns.FindAsync(later.Id))!.PassNoticeStage);
        Assert.Equal(0, (await db.Campaigns.FindAsync(draft.Id))!.PassNoticeStage);

        // Nothing again the next sweep; the month-notice event gets its week notice when it's due.
        Assert.Equal(0, await sut.RunOnceAsync(now.AddHours(6)));
        Assert.Equal(1, await sut.RunOnceAsync(now.AddDays(14)));
    }
}
