using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Security;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using InvitesBlog.Infrastructure.Notifications;
using InvitesBlog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>
/// Who hears that new photos arrived, and how often.
///
/// <para>The failure this guards against is not a crash — it is a phone buzzing fifty times during
/// someone's wedding, or telling a guest about the photo they took themselves thirty seconds ago.
/// Those are rules about people, so they are pinned here rather than left to a reading of the sweep.</para>
/// </summary>
public class PhotoDigestServiceTests
{
    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"digest-{Guid.NewGuid()}")
            .Options);

    private static PhotoDigestService Service(bool enabled = true, int windowHours = 6)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Notifications:PhotoDigest:Enabled"] = enabled ? "true" : "false",
            ["Notifications:PhotoDigest:WindowHours"] = windowHours.ToString(),
            ["Urls:InviteeBase"] = "https://me.invites.blog",
            ["Urls:InviterBase"] = "https://invites.blog",
        }).Build();

        return new PhotoDigestService(
            Substitute.For<IServiceProvider>(), config,
            NullLogger<PhotoDigestService>.Instance);
    }

    /// <summary>A sender that records every address it was handed.</summary>
    private static (IEmailSender Sender, List<string> Sent) Recorder()
    {
        var sent = new List<string>();
        var sender = Substitute.For<IEmailSender>();
        sender.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => { sent.Add(call.ArgAt<string>(0)); return Task.FromResult(DeliveryResult.Ok("m")); });
        return (sender, sent);
    }

    private static Campaign SeedCampaign(AppDbContext db, Guid? inviterId = null)
    {
        var campaign = new Campaign
        {
            Id = Guid.NewGuid(),
            TemplateId = Guid.NewGuid(),
            Title = "Raniya's birthday",
            Status = CampaignStatus.Dispatched,
            InviterId = inviterId,
            Slug = $"s-{Guid.NewGuid():N}",
            AccessTokenHash = "hash",
            TemplateVersion = "1.0.0",
        };
        db.Campaigns.Add(campaign);
        return campaign;
    }

    private static Guest SeedGuest(AppDbContext db, Guid campaignId, string? email, bool optedOut = false)
    {
        var guest = new Guest
        {
            Id = Guid.NewGuid(), CampaignId = campaignId, Email = email, OptedOut = optedOut,
        };
        db.Guests.Add(guest);
        return guest;
    }

    private static void SeedPhoto(AppDbContext db, Guid campaignId, Guid? guestId,
        DateTimeOffset createdAt, DateTimeOffset? deletedAt = null)
    {
        db.EventPhotos.Add(new EventPhoto
        {
            Id = Guid.NewGuid(), CampaignId = campaignId, GuestId = guestId,
            OriginalUrl = "o", Url = "u", ThumbUrl = "t", ContentType = "image/jpeg",
            SizeBytes = 1, Width = 1, Height = 1,
            CreatedAt = createdAt, DeletedAt = deletedAt,
        });
    }

    [Fact]
    public async Task Tells_every_addressable_guest_about_new_photos()
    {
        using var db = NewDb();
        var campaign = SeedCampaign(db);
        SeedGuest(db, campaign.Id, "a@example.com");
        SeedGuest(db, campaign.Id, "b@example.com");
        SeedPhoto(db, campaign.Id, Guid.NewGuid(), DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();

        var (sender, sent) = Recorder();
        await Service().SweepAsync(db, sender, default);

        Assert.Equal(["a@example.com", "b@example.com"], sent.Order());
    }

    /// <summary>
    /// The photos are all one person's, so that person is the only one with nothing to be told.
    /// </summary>
    [Fact]
    public async Task Does_not_tell_the_sole_uploader_about_their_own_photos()
    {
        using var db = NewDb();
        var campaign = SeedCampaign(db);
        var shooter = SeedGuest(db, campaign.Id, "shooter@example.com");
        SeedGuest(db, campaign.Id, "other@example.com");
        SeedPhoto(db, campaign.Id, shooter.Id, DateTimeOffset.UtcNow);
        SeedPhoto(db, campaign.Id, shooter.Id, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();

        var (sender, sent) = Recorder();
        await Service().SweepAsync(db, sender, default);

        Assert.Equal(["other@example.com"], sent);
    }

    /// <summary>
    /// Contributing one of several is not the same as being the only one — there are still other
    /// people's photos to hear about.
    /// </summary>
    [Fact]
    public async Task Still_tells_a_contributor_when_someone_else_also_uploaded()
    {
        using var db = NewDb();
        var campaign = SeedCampaign(db);
        var one = SeedGuest(db, campaign.Id, "one@example.com");
        var two = SeedGuest(db, campaign.Id, "two@example.com");
        SeedPhoto(db, campaign.Id, one.Id, DateTimeOffset.UtcNow);
        SeedPhoto(db, campaign.Id, two.Id, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();

        var (sender, sent) = Recorder();
        await Service().SweepAsync(db, sender, default);

        Assert.Equal(["one@example.com", "two@example.com"], sent.Order());
    }

    [Fact]
    public async Task Skips_the_opted_out_the_suppressed_and_those_with_no_address()
    {
        using var db = NewDb();
        var campaign = SeedCampaign(db);
        SeedGuest(db, campaign.Id, "kept@example.com");
        SeedGuest(db, campaign.Id, "left@example.com", optedOut: true);
        SeedGuest(db, campaign.Id, null);
        SeedGuest(db, campaign.Id, "gone@example.com");
        db.SuppressionList.Add(new SuppressionEntry
        {
            Id = Guid.NewGuid(),
            ContactHash = TokenService.HashContact("gone@example.com"),
            ContactType = "email",
            CreatedAt = DateTimeOffset.UtcNow,
        });
        SeedPhoto(db, campaign.Id, Guid.NewGuid(), DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();

        var (sender, sent) = Recorder();
        await Service().SweepAsync(db, sender, default);

        Assert.Equal(["kept@example.com"], sent);
    }

    /// <summary>
    /// The whole point of the feature: a hundred photos is one email, not a hundred, and the next
    /// batch waits for the quiet period rather than going out immediately behind it.
    /// </summary>
    [Fact]
    public async Task Collapses_many_photos_into_one_email_and_then_stays_quiet()
    {
        using var db = NewDb();
        var campaign = SeedCampaign(db);
        SeedGuest(db, campaign.Id, "guest@example.com");
        for (var i = 0; i < 100; i++)
            SeedPhoto(db, campaign.Id, Guid.NewGuid(), DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();

        var (sender, sent) = Recorder();
        var service = Service();
        await service.SweepAsync(db, sender, default);
        Assert.Single(sent);

        // More arrive straight away; the quiet period has not passed, so nothing goes out.
        SeedPhoto(db, campaign.Id, Guid.NewGuid(), DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();
        await service.SweepAsync(db, sender, default);
        Assert.Single(sent);
    }

    /// <summary>Once the quiet period has passed, what arrived during it is reported.</summary>
    [Fact]
    public async Task Reports_again_once_the_quiet_period_has_passed()
    {
        using var db = NewDb();
        var campaign = SeedCampaign(db);
        campaign.PhotosNotifiedAt = DateTimeOffset.UtcNow.AddHours(-7);
        SeedGuest(db, campaign.Id, "guest@example.com");
        SeedPhoto(db, campaign.Id, Guid.NewGuid(), DateTimeOffset.UtcNow.AddHours(-1));
        await db.SaveChangesAsync();

        var (sender, sent) = Recorder();
        await Service(windowHours: 6).SweepAsync(db, sender, default);

        Assert.Single(sent);
    }

    [Fact]
    public async Task Ignores_deleted_photos_and_cancelled_campaigns()
    {
        using var db = NewDb();
        var moderated = SeedCampaign(db);
        SeedGuest(db, moderated.Id, "moderated@example.com");
        SeedPhoto(db, moderated.Id, Guid.NewGuid(), DateTimeOffset.UtcNow, deletedAt: DateTimeOffset.UtcNow);

        var cancelled = SeedCampaign(db);
        cancelled.Status = CampaignStatus.Cancelled;
        SeedGuest(db, cancelled.Id, "cancelled@example.com");
        SeedPhoto(db, cancelled.Id, Guid.NewGuid(), DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();

        var (sender, sent) = Recorder();
        await Service().SweepAsync(db, sender, default);

        Assert.Empty(sent);
    }

    /// <summary>The host is not on the guest list, and is told at their own dashboard link.</summary>
    [Fact]
    public async Task Tells_the_host_at_their_dashboard()
    {
        using var db = NewDb();
        var inviter = new Inviter
        {
            Id = Guid.NewGuid(), Name = "Host", PhoneE164 = "+9607777777", Email = "host@example.com",
        };
        db.Inviters.Add(inviter);
        var campaign = SeedCampaign(db, inviter.Id);
        SeedPhoto(db, campaign.Id, Guid.NewGuid(), DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();

        var bodies = new List<string>();
        var sender = Substitute.For<IEmailSender>();
        sender.SendAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => { bodies.Add(call.ArgAt<string>(2)); return Task.FromResult(DeliveryResult.Ok("m")); });

        await Service().SweepAsync(db, sender, default);

        Assert.Contains($"https://invites.blog/dashboard/{campaign.Id}", Assert.Single(bodies));
    }

    /// <summary>
    /// A campaign nobody can be mailed about must still be marked, or every sweep for the rest of
    /// time re-examines the same photos.
    /// </summary>
    [Fact]
    public async Task Marks_the_campaign_even_when_there_is_nobody_to_tell()
    {
        using var db = NewDb();
        var campaign = SeedCampaign(db);
        SeedPhoto(db, campaign.Id, Guid.NewGuid(), DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();

        var (sender, sent) = Recorder();
        await Service().SweepAsync(db, sender, default);

        Assert.Empty(sent);
        Assert.NotNull(db.Campaigns.Single(c => c.Id == campaign.Id).PhotosNotifiedAt);
    }
}
