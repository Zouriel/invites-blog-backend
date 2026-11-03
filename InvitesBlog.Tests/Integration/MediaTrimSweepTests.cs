using System.Net;
using System.Net.Http.Json;
using InvitesBlog.Application.Plans;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using InvitesBlog.Infrastructure.Notifications;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InvitesBlog.Tests.Integration;

[CollectionDefinition("media-trim")]
public sealed class MediaTrimCollection : ICollectionFixture<PostgresApiFixture>;

/// <summary>
/// Owner, 2026-11-03: free albums are kept forever; a pass that isn't renewed goes back to Free and
/// everything above Free's space is removed, "with appropriate warnings in times for them to backup".
/// The sweep, run on real Postgres at the moments it would run, with its emails, pushes, files and
/// space read back.
/// </summary>
[Collection("media-trim")]
public sealed class MediaTrimSweepTests(PostgresApiFixture api)
{
    private const long Mb = PlanCatalog.Mb;

    private MediaRetentionService Sweeper(bool dryRun = false) => new(
        api.Services,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Urls:InviterBase"] = "http://localhost",
            ["Notifications:MediaRetention:DryRun"] = dryRun ? "true" : "false",
        }).Build(),
        NullLogger<MediaRetentionService>.Instance);

    [PostgresFact]
    public async Task A_free_event_is_never_warned_or_cut_however_old_or_full()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host, eventAt: DateTimeOffset.UtcNow.AddYears(-3));
        var album = await AlbumAsync(ev, host.Id);
        for (var i = 0; i < 4; i++) await PhotoAsync(ev, album, 200 * Mb, i);

        foreach (var days in new[] { 0, 40, 400, 4000 })
            await Sweeper().RunOnceAsync(DateTimeOffset.UtcNow.AddDays(days), default);

        Assert.Empty(MailFor(host));
        var c = await api.ReadAsync(db => db.Campaigns.SingleAsync(x => x.Id == ev));
        Assert.Equal(0, c.MediaNoticeStage);
        Assert.Null(c.MediaTrimAt);
        Assert.Equal(4, await LiveAsync(ev));
    }

    [PostgresFact]
    public async Task An_ended_pass_is_warned_three_times_then_cut_to_300_mb_and_kept_for_good()
    {
        var host = await UserAsync();
        var phone = await RegisterPhoneAsync(host);
        var ev = await EventAsync(host, eventAt: DateTimeOffset.UtcNow.AddYears(-1));
        await PassAsync(ev, EventPassKind.Gold, DateTimeOffset.UtcNow.AddHours(-1));
        var album = await AlbumAsync(ev, host.Id);
        var photos = new List<Guid>();
        for (var i = 0; i < 5; i++) photos.Add(await PhotoAsync(ev, album, 100 * Mb, i));
        var t = DateTimeOffset.UtcNow;

        // The day it ends: the 30-day notice, by email and push, with the date and the size.
        await Sweeper().RunOnceAsync(t, default);
        var first = Assert.Single(MailFor(host));
        Assert.Contains("goes back to Free on", first.Subject);
        Assert.Contains("200 MB", first.Html);
        var push = Assert.Single(api.Apns.To(phone));
        Assert.Contains("back on Free", push.Title);
        var c = await CampaignAsync(ev);
        Assert.Equal(1, c.MediaNoticeStage);
        Assert.True(c.MediaTrimAt >= t.AddDays(PlanCatalog.TrimNoticeDays).AddMinutes(-1));
        Assert.Equal(200 * Mb, c.MediaTrimBytes);
        var trimAt = c.MediaTrimAt!.Value;

        // Everyone can still look while it waits; nothing new can be added.
        var plan = await PlanAsync(ev);
        Assert.Equal(MediaPhase.UploadsClosed, plan.Phase);

        // Nothing more until a week before, then a day before; nothing removed meanwhile.
        await Sweeper().RunOnceAsync(t.AddDays(10), default);
        Assert.Single(MailFor(host));
        await Sweeper().RunOnceAsync(trimAt.AddDays(-6.5), default);
        Assert.Equal(2, MailFor(host).Count);
        Assert.Contains("One week left", MailFor(host).Last().Subject);
        await Sweeper().RunOnceAsync(trimAt.AddHours(-12), default);
        Assert.Equal(3, MailFor(host).Count);
        Assert.Contains("Tomorrow", MailFor(host).Last().Subject);
        Assert.Equal(3, api.Apns.To(phone).Count);
        Assert.Equal(5, await LiveAsync(ev));

        // The day: the two latest uploads go (files and space too); the earliest three stay.
        var report = await Sweeper().RunOnceAsync(trimAt.AddMinutes(5), default);
        Assert.Equal(1, report.Trimmed);
        Assert.Equal(200 * Mb, report.BytesRemoved);
        var live = await api.ReadAsync(db => db.EventPhotos.Where(p => p.CampaignId == ev && p.DeletedAt == null).Select(p => p.Id).ToListAsync());
        Assert.Equal(photos.Take(3).OrderBy(x => x), live.OrderBy(x => x));
        Assert.Equal(300 * Mb, await api.ReadAsync(db => db.MediaBuckets.Where(b => b.Id == album).Select(b => b.UsedBytes).SingleAsync()));
        foreach (var gone in photos.Skip(3))
            Assert.False(File.Exists(Path.Combine(api.StorageRoot, "buckets", album.ToString("N"), "media", $"{gone:N}.jpg")));
        foreach (var kept in photos.Take(3))
            Assert.True(File.Exists(Path.Combine(api.StorageRoot, "buckets", album.ToString("N"), "media", $"{kept:N}.jpg")));

        // From then on a free event: no end, no more warnings, nothing more removed.
        plan = await PlanAsync(ev);
        Assert.Equal(PlanKind.Free, plan.Kind);
        Assert.Null(plan.CoveredUntil);
        Assert.Equal(MediaPhase.Active, plan.Phase);
        await Sweeper().RunOnceAsync(t.AddYears(3), default);
        Assert.Equal(3, MailFor(host).Count);
        Assert.Equal(3, await LiveAsync(ev));
    }

    [PostgresFact]
    public async Task Nothing_is_removed_until_a_second_warning_has_gone_out()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host, eventAt: DateTimeOffset.UtcNow.AddYears(-1));
        await PassAsync(ev, EventPassKind.Platinum, DateTimeOffset.UtcNow.AddHours(-1));
        var album = await AlbumAsync(ev, host.Id);
        for (var i = 0; i < 4; i++) await PhotoAsync(ev, album, 150 * Mb, i);
        var t = DateTimeOffset.UtcNow;

        await Sweeper().RunOnceAsync(t, default);
        var trimAt = (await CampaignAsync(ev)).MediaTrimAt!.Value;

        // The sweeps in between never ran: past the date, the second warning goes out first, and it
        // promises at least a day.
        var late = trimAt.AddDays(2);
        await Sweeper().RunOnceAsync(late, default);
        Assert.Equal(4, await LiveAsync(ev));
        var c = await CampaignAsync(ev);
        Assert.Equal(2, c.MediaNoticeStage);
        Assert.True(c.MediaTrimAt >= late.AddDays(1).AddMinutes(-1));
        Assert.Equal(2, MailFor(host).Count);

        await Sweeper().RunOnceAsync(c.MediaTrimAt!.Value.AddMinutes(1), default);
        Assert.Equal(2, await LiveAsync(ev));
    }

    [PostgresFact]
    public async Task Extending_during_the_notice_calls_the_cut_off()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host, eventAt: DateTimeOffset.UtcNow.AddYears(-1));
        await PassAsync(ev, EventPassKind.Gold, DateTimeOffset.UtcNow.AddHours(-1));
        var album = await AlbumAsync(ev, host.Id);
        for (var i = 0; i < 4; i++) await PhotoAsync(ev, album, 150 * Mb, i);
        var t = DateTimeOffset.UtcNow;
        await Sweeper().RunOnceAsync(t, default);
        Assert.Equal(1, (await CampaignAsync(ev)).MediaNoticeStage);

        await api.WriteAsync(async db =>
        {
            var c = await db.Campaigns.SingleAsync(x => x.Id == ev);
            Assert.True(EventPasses.Extend(c, DateTimeOffset.UtcNow));
        });

        foreach (var days in new[] { 5, 29, 31, 60 })
            await Sweeper().RunOnceAsync(t.AddDays(days), default);
        var after = await CampaignAsync(ev);
        Assert.Equal(0, after.MediaNoticeStage);
        Assert.Null(after.MediaTrimAt);
        Assert.Equal(4, await LiveAsync(ev));
        Assert.Single(MailFor(host));
    }

    [PostgresFact]
    public async Task An_ended_premium_s_events_go_back_to_free_the_same_way()
    {
        var host = await UserAsync(SubscriptionTier.Premium, DateTimeOffset.UtcNow.AddHours(-2));
        var ev = await EventAsync(host, eventAt: DateTimeOffset.UtcNow.AddMonths(-8));
        var album = await AlbumAsync(ev, host.Id);
        for (var i = 0; i < 5; i++) await PhotoAsync(ev, album, 300 * Mb, i);
        var t = DateTimeOffset.UtcNow;

        await Sweeper().RunOnceAsync(t, default);
        var mail = Assert.Single(MailFor(host));
        Assert.Contains("Your Premium", mail.Html);
        Assert.Contains("subscribe to Premium again", mail.Html);
        var trimAt = (await CampaignAsync(ev)).MediaTrimAt!.Value;
        await Sweeper().RunOnceAsync(trimAt.AddDays(-6), default);
        await Sweeper().RunOnceAsync(trimAt.AddMinutes(1), default);
        Assert.Equal(1, await LiveAsync(ev));
    }

    [PostgresFact]
    public async Task A_pass_event_that_fits_in_free_simply_becomes_free_without_a_warning()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host, eventAt: DateTimeOffset.UtcNow.AddYears(-1));
        await PassAsync(ev, EventPassKind.Gold, DateTimeOffset.UtcNow.AddHours(-1));
        var album = await AlbumAsync(ev, host.Id);
        await PhotoAsync(ev, album, 120 * Mb, 0);

        await Sweeper().RunOnceAsync(DateTimeOffset.UtcNow, default);
        Assert.Empty(MailFor(host));
        Assert.Null((await PlanAsync(ev)).CoveredUntil);
        Assert.Equal(1, await LiveAsync(ev));
    }

    [PostgresFact]
    public async Task A_dry_run_reports_and_changes_nothing()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host, eventAt: DateTimeOffset.UtcNow.AddYears(-1));
        await PassAsync(ev, EventPassKind.Gold, DateTimeOffset.UtcNow.AddHours(-1));
        var album = await AlbumAsync(ev, host.Id);
        for (var i = 0; i < 4; i++) await PhotoAsync(ev, album, 150 * Mb, i);

        var report = await Sweeper(dryRun: true).RunOnceAsync(DateTimeOffset.UtcNow, default);
        Assert.True(report.Warned >= 1);
        Assert.Empty(MailFor(host));
        var c = await CampaignAsync(ev);
        Assert.Equal(0, c.MediaNoticeStage);
        Assert.Null(c.MediaTrimAt);
    }

    // ----- helpers -----------------------------------------------------------------------------

    private sealed record Person(Guid Id, string Email, string Token);

    private List<Application.Abstractions.EmailMessage> MailFor(Person p) =>
        api.Mail.Sent.Where(m => m.To == p.Email).OrderBy(m => m.Tags?.FirstOrDefault().Value).ToList();

    private Task<Campaign> CampaignAsync(Guid ev) => api.ReadAsync(db => db.Campaigns.SingleAsync(c => c.Id == ev));

    private Task<int> LiveAsync(Guid ev) => api.ReadAsync(db => db.EventPhotos.CountAsync(p => p.CampaignId == ev && p.DeletedAt == null));

    private async Task<EventPlan> PlanAsync(Guid ev)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IPlanService>().ForCampaignAsync(ev);
    }

    private async Task<Person> UserAsync(SubscriptionTier tier = SubscriptionTier.None, DateTimeOffset? endsAt = null)
    {
        var id = Guid.NewGuid();
        var mail = $"trim-{id:N}@example.com";
        await api.WriteAsync(async db =>
        {
            var roles = await db.Roles.Where(r => r.Name == Roles.Customer).Select(r => r.Id).ToListAsync();
            var user = new AppUser
            {
                Id = id, Email = mail, DisplayName = "Host", PasswordHash = "x", IsActive = true,
                SubscriptionTier = tier, SubscriptionEndsAt = endsAt, CreatedAt = DateTimeOffset.UtcNow,
            };
            foreach (var r in roles) user.UserRoles.Add(new UserRole { RoleId = r });
            db.Users.Add(user);
        });
        return new Person(id, mail, api.TokenFor(id, "email", mail, [Roles.Customer]));
    }

    private async Task<string> RegisterPhoneAsync(Person who)
    {
        var token = Convert.ToHexString(Guid.NewGuid().ToByteArray()) + Convert.ToHexString(Guid.NewGuid().ToByteArray());
        var r = await api.Client(who.Token).PostAsJsonAsync("/api/me/devices", new { token, platform = "ios" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return token;
    }

    private async Task<Guid> EventAsync(Person host, DateTimeOffset eventAt)
    {
        var id = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        await api.WriteAsync(async db =>
        {
            db.Templates.Add(new Template
            {
                Id = templateId, Name = "T", Slug = $"own-{id:N}", Version = "1.0.0", Category = "Imported", Description = "D", PreviewImageUrl = "",
                SceneJson = "{}", ManifestJson = "{}", PackageUrl = "", Visibility = TemplateVisibility.Imported,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.Campaigns.Add(new Campaign
            {
                Id = id, TemplateId = templateId, TemplateVersion = "1.0.0", TemplatePackageUrl = "",
                CreatedByUserId = host.Id, AccessTokenHash = Guid.NewGuid().ToString("N"),
                Title = "Trim test", Slug = $"e-{id:N}", Status = CampaignStatus.Dispatched,
                EventStartAt = eventAt, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await Task.CompletedTask;
        });
        return id;
    }

    private Task PassAsync(Guid ev, EventPassKind pass, DateTimeOffset until) =>
        api.WriteAsync(db => db.Campaigns.Where(c => c.Id == ev).ExecuteUpdateAsync(s => s
            .SetProperty(c => c.EventPass, pass).SetProperty(c => c.EventPassUntil, until)));

    private async Task<Guid> AlbumAsync(Guid ev, Guid owner)
    {
        var id = Guid.NewGuid();
        await api.WriteAsync(async db =>
        {
            db.MediaBuckets.Add(new MediaBucket { Id = id, CampaignId = ev, OwnerUserId = owner, EventDate = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            await Task.CompletedTask;
        });
        return id;
    }

    /// <summary>A stored photo counted at <paramref name="bytes"/>, uploaded <paramref name="order"/> minutes after the first.</summary>
    private async Task<Guid> PhotoAsync(Guid ev, Guid album, long bytes, int order)
    {
        var id = Guid.NewGuid();
        var url = await api.Storage.PutAsync($"buckets/{album:N}/media/{id:N}.jpg", [1], "image/jpeg");
        var thumb = await api.Storage.PutAsync($"buckets/{album:N}/media/{id:N}_t.jpg", [1], "image/jpeg");
        var original = await api.Storage.PutAsync($"buckets/{album:N}/media/{id:N}_o.jpg", [1], "image/jpeg");
        await api.WriteAsync(async db =>
        {
            db.EventPhotos.Add(new EventPhoto
            {
                Id = id, CampaignId = ev, BucketId = album, Url = url, ThumbUrl = thumb, OriginalUrl = original,
                ContentType = "image/jpeg", SizeBytes = bytes, CreatedAt = DateTimeOffset.UtcNow.AddDays(-300).AddMinutes(order),
            });
            await db.SaveChangesAsync();
            await db.MediaBuckets.Where(b => b.Id == album).ExecuteUpdateAsync(s => s.SetProperty(b => b.UsedBytes, b => b.UsedBytes + bytes));
        });
        return id;
    }
}
