using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Plans;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using InvitesBlog.Infrastructure.Delivery;
using InvitesBlog.Infrastructure.Persistence;
using InvitesBlog.Infrastructure.Repositories;
using InvitesBlog.Tests.Support;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Integration;

/// <summary>
/// Emailed invitations included with subscriptions (owner, 2026-11-14): Premium 100, Photographer 500, Studio 500
/// a month per account, shared by its events, Maldives calendar month. An event uses its pass first, then the
/// organiser's month, then emails bought for it. Run on real Postgres, because the guarantee that two sends at
/// once can't go past the allowance rests on a Postgres lock.
/// </summary>
public sealed class MonthlyEmailAllowanceTests : IAsyncLifetime
{
    private string _conn = default!;
    private string _name = default!;
    private string _server = default!;

    /// <summary>15:00 in Malé on 14 November 2026.</summary>
    private static readonly DateTimeOffset MidNovember = new(2026, 11, 14, 10, 0, 0, TimeSpan.Zero);

    public async Task InitializeAsync()
    {
        _server = Environment.GetEnvironmentVariable(PostgresApiFixture.ServerVariable) ?? "";
        if (string.IsNullOrWhiteSpace(_server)) return;
        _name = $"ib_mail_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_server) { Database = "postgres" }.ToString()))
        {
            await admin.OpenAsync();
            await new NpgsqlCommand($"CREATE DATABASE {_name}", admin).ExecuteNonQueryAsync();
        }
        _conn = new NpgsqlConnectionStringBuilder(_server) { Database = _name }.ToString();
        await using var db = Db();
        await db.Database.MigrateAsync();
    }

    public async Task DisposeAsync()
    {
        if (string.IsNullOrWhiteSpace(_server)) return;
        NpgsqlConnection.ClearAllPools();
        await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(_server) { Database = "postgres" }.ToString());
        await admin.OpenAsync();
        await new NpgsqlCommand($"DROP DATABASE IF EXISTS {_name} WITH (FORCE)", admin).ExecuteNonQueryAsync();
    }

    private AppDbContext Db() => new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(_conn).Options);

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static PlanService Plans(AppDbContext db) =>
        new(new CampaignRepository(db), new BaseRepository<AppUser>(db), new BaseRepository<MediaBucket>(db));

    private static EmailAllowanceGate Gate(AppDbContext db, TimeProvider clock) => new(db, Plans(db), clock);

    private static SendingAllowanceService Allowance(AppDbContext db, TimeProvider clock) =>
        new(new CampaignRepository(db), new InviteRepository(db), new BaseRepository<AppUser>(db),
            new BaseRepository<MonthlyEmailSend>(db), Plans(db), clock);

    private async Task<Guid> AccountAsync(SubscriptionTier tier, DateTimeOffset? endsAt = null)
    {
        await using var db = Db();
        var user = new AppUser
        {
            Id = Guid.NewGuid(), Email = $"{Guid.NewGuid():N}@example.com", DisplayName = "Organiser",
            SubscriptionTier = tier, SubscriptionEndsAt = endsAt,
        };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    /// <summary>An event organised by <paramref name="owner"/>, with <paramref name="guests"/> guests who have an email.</summary>
    private async Task<(Guid Campaign, List<Guid> Guests)> EventAsync(
        Guid owner, int guests, EventPassKind pass = EventPassKind.None, int bought = 0)
    {
        await using var db = Db();
        var now = DateTimeOffset.UtcNow;
        var template = new Template
        {
            Id = Guid.NewGuid(), Name = "T", Slug = $"t-{Guid.NewGuid():N}", Version = "1.0.0", Category = "Birthday",
            Description = "d", PreviewImageUrl = "p", SceneJson = "{}", ManifestJson = "{}", PackageUrl = "/assets/x/",
            IsActive = true, Visibility = TemplateVisibility.Public,
        };
        var campaign = new Campaign
        {
            Id = Guid.NewGuid(), TemplateId = template.Id, TemplateVersion = "1.0.0", AccessTokenHash = Guid.NewGuid().ToString("N"),
            Title = "Party", Slug = "party", Status = CampaignStatus.Dispatched, CreatedByUserId = owner,
            DeliverySettingsJson = "{\"channels\":[\"email\"]}", EventStartAt = now.AddDays(20),
            EventPass = pass, EventPassUntil = pass == EventPassKind.None ? null : now.AddYears(1),
            PaidInviteCapacity = bought, CreatedAt = now, UpdatedAt = now,
        };
        db.Templates.Add(template);
        db.Campaigns.Add(campaign);
        var ids = new List<Guid>();
        for (var i = 0; i < guests; i++)
        {
            var g = new Guest { Id = Guid.NewGuid(), CampaignId = campaign.Id, Name = $"Guest {i}", Email = $"g{i}@example.com", CreatedAt = now };
            db.Guests.Add(g);
            ids.Add(g.Id);
        }
        await db.SaveChangesAsync();
        return (campaign.Id, ids);
    }

    /// <summary>Marks <paramref name="count"/> emails as already taken from the account's month.</summary>
    private async Task SpendMonthAsync(Guid account, int count, DateTimeOffset at)
    {
        await using var db = Db();
        for (var i = 0; i < count; i++)
            db.MonthlyEmailSends.Add(new MonthlyEmailSend
            {
                Id = Guid.NewGuid(), AccountId = account, Month = EmailMonth.Of(at), CampaignId = Guid.NewGuid(),
                InviteId = Guid.NewGuid(), CreatedAt = at,
            });
        await db.SaveChangesAsync();
    }

    /// <summary>Marks the event's first <paramref name="count"/> guests as already emailed from the event (its pass).</summary>
    private async Task SpendEventAsync(Guid campaign, IEnumerable<Guid> guests)
    {
        await using var db = Db();
        foreach (var g in guests)
            db.Invites.Add(NewInvite(campaign, g, DateTimeOffset.UtcNow));
        await db.SaveChangesAsync();
    }

    private static Invite NewInvite(Guid campaign, Guid guest, DateTimeOffset? emailedAt = null) => new()
    {
        Id = Guid.NewGuid(), CampaignId = campaign, GuestId = guest, TokenHash = Guid.NewGuid().ToString("N"),
        Status = emailedAt is null ? InviteStatus.Queued : InviteStatus.Sent, RsvpStatus = RsvpStatus.NoResponse,
        FirstEmailedAt = emailedAt, EmailPaidBy = EmailPaidBy.Event, CreatedAt = DateTimeOffset.UtcNow,
    };

    /// <summary>One charge for a new guest of the event, through its own context like a separate request.</summary>
    private async Task<EmailPaidBy?> ChargeAsync(Guid campaign, Guid guest, TimeProvider clock)
    {
        await using var db = Db();
        var c = await db.Campaigns.FirstAsync(x => x.Id == campaign);
        var invite = await db.Invites.FirstOrDefaultAsync(i => i.GuestId == guest);
        if (invite is null) db.Invites.Add(invite = NewInvite(campaign, guest));
        var charge = await Gate(db, clock).ChargeAsync(c, invite);
        if (charge is null) await db.SaveChangesAsync();
        return charge?.PaidBy;
    }

    private async Task<int> MonthlyRowsAsync(Guid account)
    {
        await using var db = Db();
        return await db.MonthlyEmailSends.CountAsync(m => m.AccountId == account);
    }

    [PostgresFact]
    public async Task An_event_uses_its_pass_first_then_the_organisers_month_then_bought_emails()
    {
        var clock = new FixedClock(MidNovember);
        var premium = await AccountAsync(SubscriptionTier.Premium);
        await SpendMonthAsync(premium, PlanCatalog.PremiumMonthlyEmails - 1, MidNovember);

        // A Gold pass event with room on its pass: the pass pays, the month is left alone.
        var (gold, goldGuests) = await EventAsync(premium, 1, EventPassKind.Gold);
        Assert.Equal(EmailPaidBy.Event, await ChargeAsync(gold, goldGuests[0], clock));
        Assert.Equal(PlanCatalog.PremiumMonthlyEmails - 1, await MonthlyRowsAsync(premium));

        // An event with no pass and one bought email: the month's last one first, then the bought one, then nothing.
        var (party, guests) = await EventAsync(premium, 3, bought: 1);
        Assert.Equal(EmailPaidBy.Monthly, await ChargeAsync(party, guests[0], clock));
        Assert.Equal(PlanCatalog.PremiumMonthlyEmails, await MonthlyRowsAsync(premium));
        Assert.Equal(EmailPaidBy.Event, await ChargeAsync(party, guests[1], clock));
        Assert.Null(await ChargeAsync(party, guests[2], clock));

        await using var db = Db();
        var shown = await Allowance(db, clock).ForCampaignAsync(party);
        Assert.Equal((0, 1, 1, 100, 100, 0), (shown.Included, shown.Extra, shown.EventUsed, shown.Monthly, shown.MonthlyUsed, shown.Left));
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.FromHours(5)), shown.MonthlyResetsAt);
    }

    [PostgresFact]
    public async Task A_pass_events_own_emails_come_before_the_month_until_they_run_out()
    {
        var clock = new FixedClock(MidNovember);
        var photographer = await AccountAsync(SubscriptionTier.Photographer);
        var (gold, guests) = await EventAsync(photographer, PlanCatalog.GoldIncludedInvites + 1, EventPassKind.Gold);
        await SpendEventAsync(gold, guests.Take(PlanCatalog.GoldIncludedInvites - 1));

        Assert.Equal(EmailPaidBy.Event, await ChargeAsync(gold, guests[PlanCatalog.GoldIncludedInvites - 1], clock));
        Assert.Equal(0, await MonthlyRowsAsync(photographer));
        Assert.Equal(EmailPaidBy.Monthly, await ChargeAsync(gold, guests[PlanCatalog.GoldIncludedInvites], clock));
        Assert.Equal(1, await MonthlyRowsAsync(photographer));
    }

    [PostgresFact]
    public async Task Two_sends_at_once_cannot_go_past_the_monthly_allowance()
    {
        var clock = new FixedClock(MidNovember);
        var studio = await AccountAsync(SubscriptionTier.Venue);
        await SpendMonthAsync(studio, PlanCatalog.VenueMonthlyEmails - 2, MidNovember);
        // Two events of the same account, sending at the same moment: the allowance is shared.
        var (a, aGuests) = await EventAsync(studio, 6);
        var (b, bGuests) = await EventAsync(studio, 6);

        var start = new TaskCompletionSource();
        var sends = aGuests.Select(g => (a, g)).Concat(bGuests.Select(g => (b, g)))
            .Select(x => Task.Run(async () => { await start.Task; return await ChargeAsync(x.Item1, x.g, clock); }))
            .ToList();
        start.SetResult();
        var results = await Task.WhenAll(sends);

        Assert.Equal(2, results.Count(r => r == EmailPaidBy.Monthly));
        Assert.Equal(10, results.Count(r => r is null));
        Assert.Equal(PlanCatalog.VenueMonthlyEmails, await MonthlyRowsAsync(studio));
    }

    [PostgresFact]
    public async Task The_month_starts_again_on_the_1st_in_Maldives_time_and_is_not_carried_over()
    {
        // 23:59 on 30 November in Malé is 18:59 UTC; a minute later it is December there.
        var clock = new FixedClock(new DateTimeOffset(2026, 11, 30, 18, 59, 0, TimeSpan.Zero));
        var premium = await AccountAsync(SubscriptionTier.Premium);
        await SpendMonthAsync(premium, PlanCatalog.PremiumMonthlyEmails, clock.Now);
        var (party, guests) = await EventAsync(premium, 2);

        Assert.Null(await ChargeAsync(party, guests[0], clock));

        clock.Now = new DateTimeOffset(2026, 11, 30, 19, 0, 0, TimeSpan.Zero);
        Assert.Equal(EmailPaidBy.Monthly, await ChargeAsync(party, guests[0], clock));
        await using var db = Db();
        var shown = await Allowance(db, clock).ForCampaignAsync(party);
        // A whole new month: nothing unused from November is added.
        Assert.Equal((100, 1, 99), (shown.Monthly, shown.MonthlyUsed, shown.MonthlyLeft));
    }

    [PostgresFact]
    public async Task A_lapsed_subscription_has_no_monthly_allowance()
    {
        var clock = new FixedClock(MidNovember);
        var lapsed = await AccountAsync(SubscriptionTier.Photographer, MidNovember.AddDays(-1));
        var (party, guests) = await EventAsync(lapsed, 1);

        Assert.Null(await ChargeAsync(party, guests[0], clock));
        await using var db = Db();
        Assert.Equal(0, (await Allowance(db, clock).ForCampaignAsync(party)).Monthly);

        var running = await AccountAsync(SubscriptionTier.Photographer, MidNovember.AddDays(1));
        var (other, otherGuests) = await EventAsync(running, 1);
        Assert.Equal(EmailPaidBy.Monthly, await ChargeAsync(other, otherGuests[0], clock));
    }

    [PostgresFact]
    public async Task Only_emails_actually_sent_count_and_sending_again_counts_against_the_month()
    {
        var premium = await AccountAsync(SubscriptionTier.Premium);
        var (party, guests) = await EventAsync(premium, 1);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Urls:InviteeBase"] = "https://me.invites.blog" }).Build();

        async Task<bool> ResendAsync(bool succeeds)
        {
            var provider = Substitute.For<IInviteDeliveryProvider>();
            provider.Channel.Returns("email");
            provider.SendAsync(Arg.Any<InviteDeliveryMessage>(), Arg.Any<CancellationToken>())
                .Returns(succeeds ? DeliveryResult.Ok("m") : DeliveryResult.Fail("bounced before sending"));
            await using var db = Db();
            var dispatch = new DispatchService(db, [provider], config, NullLogger<DispatchService>.Instance,
                Gate(db, TimeProvider.System), new RecordingPushQueue());
            return await dispatch.ResendAsync(guests[0]);
        }

        Assert.False(await ResendAsync(succeeds: false));
        Assert.Equal(0, await MonthlyRowsAsync(premium));
        await using (var db = Db())
            Assert.Null((await db.Invites.SingleAsync(i => i.GuestId == guests[0])).FirstEmailedAt);

        Assert.True(await ResendAsync(succeeds: true));
        Assert.True(await ResendAsync(succeeds: true));
        Assert.Equal(2, await MonthlyRowsAsync(premium));
    }
}
