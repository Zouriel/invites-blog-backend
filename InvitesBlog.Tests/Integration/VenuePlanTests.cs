using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
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

[CollectionDefinition("venue-plan")]
public sealed class VenuePlanCollection : ICollectionFixture<PostgresApiFixture>;

/// <summary>
/// The Venue plan (owner, 2026-11-12) through the real API on real Postgres: what /api/plans says it is,
/// that nobody gets a venue price any more, that the people an event is for may buy its Platinum pass,
/// that the warnings when a venue's plan ends reach the venue and the couple, and that the tiles and
/// posts carry the venue's name and logo.
/// </summary>
[Collection("venue-plan")]
public sealed class VenuePlanTests(PostgresApiFixture api)
{
    private const long Mb = PlanCatalog.Mb;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // ----- The catalogue and the offer --------------------------------------------------------

    [PostgresFact]
    public async Task The_catalogue_describes_venue_as_10_gb_events_at_775_a_month_with_no_discount()
    {
        var catalog = await DataAsync(await api.Client().GetAsync("/api/plans"));
        var venue = catalog.GetProperty("plans").EnumerateArray().Single(p => p.GetProperty("kind").GetString() == "Venue");

        Assert.Equal((await PricesAsync()).VenueMonthly, venue.GetProperty("price").GetDecimal());
        Assert.Equal("per month", venue.GetProperty("billing").GetString());
        Assert.Equal(10 * PlanCatalog.Gb, venue.GetProperty("eventBytes").GetInt64());
        Assert.Equal(1, venue.GetProperty("maxBuckets").GetInt32());
        Assert.Equal(PlanCatalog.FreeWindowDays, venue.GetProperty("maxWindowDays").GetInt32());
        Assert.Equal(JsonValueKind.Null, venue.GetProperty("retentionDays").ValueKind);
        Assert.Equal(0, venue.GetProperty("includedInvites").GetInt32());
        Assert.False(venue.GetProperty("privateAlbums").GetBoolean());
        Assert.False(venue.GetProperty("branded").GetBoolean());
        Assert.Equal(0, catalog.GetProperty("venueDiscountPercent").GetInt32());
    }

    [PostgresFact]
    public async Task A_venue_s_event_is_offered_full_price_passes_and_is_on_the_venue_plan()
    {
        var (owner, venueId) = await VenueAsync(active: true);
        var ev = await EventAsync(owner, venueId);

        // Another test in this process may have saved its own price list (the price book is cached per
        // process), so the expected prices are the book's, not the defaults: no venue price either way.
        var p = await PricesAsync();
        var billing = await DataAsync(await api.Client(owner.Token).GetAsync($"/api/billing/events/{ev}"));
        var offer = billing.GetProperty("offer");
        Assert.Equal(p.GoldPass, offer.GetProperty("goldPass").GetDecimal());
        Assert.Equal(p.PlatinumPass, offer.GetProperty("platinumPass").GetDecimal());
        Assert.Equal(p.PlatinumExtension, offer.GetProperty("platinumExtension").GetDecimal());
        Assert.Equal(0, offer.GetProperty("venuePercent").GetInt32());
        Assert.Equal("Venue", billing.GetProperty("plan").GetString());

        var plan = await PlanAsync(ev);
        Assert.Equal(PlanKind.Venue, plan.Kind);
        Assert.Equal(10 * PlanCatalog.Gb, plan.EventBytes);
        Assert.Null(plan.CoveredUntil);
        Assert.True(PlanRules.IncludesFaceGrouping(plan));

        var overview = await DataAsync(await api.Client(owner.Token).GetAsync("/api/billing"));
        Assert.Equal(0, overview.GetProperty("prices").GetProperty("venueDiscountPercent").GetInt32());
        Assert.Equal(p.VenueMonthly, overview.GetProperty("prices").GetProperty("venueMonthly").GetDecimal());
    }

    /// <summary>An owner moved to Premium (after ending Venue): the venue no longer covers the event.</summary>
    [PostgresFact]
    public async Task A_venue_whose_owner_moved_to_premium_no_longer_covers_its_events()
    {
        var (owner, venueId) = await VenueAsync(active: true);
        var ev = await EventAsync(await UserAsync(), venueId);
        await api.WriteAsync(db => db.Users.Where(u => u.Id == owner.Id).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.SubscriptionTier, SubscriptionTier.Premium)
            .SetProperty(u => u.SubscriptionEndsAt, DateTimeOffset.UtcNow.AddDays(20))));

        var plan = await PlanAsync(ev);
        Assert.Equal(PlanKind.Free, plan.Kind);
        Assert.Null(plan.VenueId);
        Assert.Null(plan.CoveredUntil);
    }

    // ----- Who may buy ------------------------------------------------------------------------

    [PostgresFact]
    public async Task A_celebrant_may_buy_a_platinum_pass_for_the_event_and_a_stranger_may_not()
    {
        var (owner, venueId) = await VenueAsync(active: true);
        var ev = await EventAsync(owner, venueId);
        var couple = await UserAsync();
        var stranger = await UserAsync();
        await CelebrantAsync(ev, couple.Email, canManage: false);

        var quote = await api.Client(couple.Token).PostAsJsonAsync("/api/billing/quote", new { item = "platinum-pass", campaignId = ev });
        Assert.Equal(HttpStatusCode.OK, quote.StatusCode);
        Assert.Equal((await PricesAsync()).PlatinumPass, (await DataAsync(quote)).GetProperty("amount").GetDecimal());
        Assert.Equal(HttpStatusCode.OK, (await api.Client(couple.Token).GetAsync($"/api/billing/events/{ev}")).StatusCode);

        var refused = await api.Client(stranger.Token).PostAsJsonAsync("/api/billing/quote", new { item = "platinum-pass", campaignId = ev });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client(stranger.Token).GetAsync($"/api/billing/events/{ev}")).StatusCode);

        // Emailed invitations stay the organiser's to buy.
        var sending = await api.Client(couple.Token).PostAsJsonAsync("/api/billing/quote", new { item = "sending", campaignId = ev, quantity = 1 });
        Assert.Equal(HttpStatusCode.Forbidden, sending.StatusCode);
    }

    // ----- When the venue's plan ends --------------------------------------------------------

    [PostgresFact]
    public async Task When_the_venue_s_plan_ends_the_venue_its_staff_and_the_couple_are_warned_and_offered_platinum()
    {
        var (owner, venueId) = await VenueAsync(active: true);
        var staff = await UserAsync();
        await api.WriteAsync(async db =>
        {
            db.VenueStaff.Add(new VenueStaff { Id = Guid.NewGuid(), VenueId = venueId, Email = staff.Email, CreatedAt = DateTimeOffset.UtcNow });
            await Task.CompletedTask;
        });
        var organiser = await UserAsync();
        var ev = await EventAsync(organiser, venueId, eventAt: DateTimeOffset.UtcNow.AddYears(-1));
        var couple = await UserAsync();
        await CelebrantAsync(ev, couple.Email, canManage: false);
        var album = await AlbumAsync(ev, organiser.Id);
        for (var i = 0; i < 4; i++) await PhotoAsync(ev, album, 150 * Mb, i);
        var ownerPhone = await RegisterPhoneAsync(owner);
        var couplePhone = await RegisterPhoneAsync(couple);

        // While the plan lasts: covered, no warnings.
        await Sweeper().RunOnceAsync(DateTimeOffset.UtcNow, default);
        Assert.Empty(MailFor(couple));

        // The plan ends (renewal stopped, the month ran out).
        await api.WriteAsync(db => db.Users.Where(u => u.Id == owner.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.SubscriptionEndsAt, DateTimeOffset.UtcNow.AddMinutes(-5))));
        var plan = await PlanAsync(ev);
        Assert.Equal(PlanKind.Free, plan.Kind);
        Assert.Equal(MediaPhase.UploadsClosed, plan.Phase);

        await Sweeper().RunOnceAsync(DateTimeOffset.UtcNow, default);
        var platinum = $"MVR {(await PricesAsync()).PlatinumPass:0}";
        foreach (var who in new[] { owner, staff, organiser, couple })
        {
            var mail = Assert.Single(MailFor(who));
            Assert.Contains("goes back to Free on", mail.Subject);
            Assert.Contains("Crown Hall", mail.Html);
            Assert.Contains("Platinum pass", mail.Html);
            Assert.Contains(platinum, mail.Html);
            Assert.Contains($"/dashboard/{ev}", mail.Html);
        }
        foreach (var phone in new[] { ownerPhone, couplePhone })
        {
            var push = Assert.Single(api.Apns.To(phone));
            Assert.Contains("back on Free", push.Title);
            // No selling in a push.
            Assert.DoesNotContain("pass", push.Body, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("MVR", push.Body);
        }
        Assert.Equal(1, (await api.ReadAsync(db => db.Campaigns.SingleAsync(c => c.Id == ev))).MediaNoticeStage);

        // The couple buys Platinum: the event is covered again and the cut is off.
        var payment = Guid.NewGuid();
        await api.WriteAsync(async db =>
        {
            db.Payments.Add(new Payment
            {
                Id = payment, CampaignId = ev, UserId = couple.Id, Kind = PaymentKind.PlatinumPass, Quantity = 1, Amount = 499, Currency = "MVR",
                Status = PaymentStatus.Paid, Provider = "test", CreatedAt = DateTimeOffset.UtcNow, PaidAt = DateTimeOffset.UtcNow,
            });
            await Task.CompletedTask;
        });
        using (var scope = api.Services.CreateScope())
            await scope.ServiceProvider.GetRequiredService<Application.Services.Billing.IBillingService>().FulfilAsync(payment);
        plan = await PlanAsync(ev);
        Assert.Equal(PlanKind.PlatinumPass, plan.Kind);
        Assert.Equal(MediaPhase.Active, plan.Phase);
        await Sweeper().RunOnceAsync(DateTimeOffset.UtcNow, default);
        var c = await api.ReadAsync(db => db.Campaigns.SingleAsync(x => x.Id == ev));
        Assert.Equal(0, c.MediaNoticeStage);
        Assert.Null(c.MediaTrimAt);
    }

    // ----- The badge --------------------------------------------------------------------------

    [PostgresFact]
    public async Task Tiles_and_posts_carry_the_venue_s_name_and_logo_and_null_without_a_venue()
    {
        var (owner, venueId) = await VenueAsync(active: true, logo: "https://cdn.example.com/venues/crown.png");
        var host = await UserAsync();
        var atVenue = await EventAsync(host, venueId);
        var plain = await EventAsync(host, null);
        var guest = await UserAsync();
        await GuestAsync(atVenue, guest.Email);
        await GuestAsync(plain, guest.Email);

        // Me (hosting): /api/me/campaigns
        var mine = (await DataAsync(await api.Client(host.Token).GetAsync("/api/me/campaigns"))).EnumerateArray().ToList();
        AssertBadge(mine.Single(t => t.GetProperty("id").GetGuid() == atVenue));
        AssertNoBadge(mine.Single(t => t.GetProperty("id").GetGuid() == plain));

        // Inbox: /api/me/invites
        var inbox = (await DataAsync(await api.Client(guest.Token).GetAsync("/api/me/invites"))).EnumerateArray().ToList();
        AssertBadge(inbox.Single(t => t.GetProperty("campaignId").GetGuid() == atVenue));
        AssertNoBadge(inbox.Single(t => t.GetProperty("campaignId").GetGuid() == plain));

        // Feed: /api/me/feed and one post
        var feed = (await DataAsync(await api.Client(host.Token).GetAsync("/api/me/feed"))).GetProperty("items").EnumerateArray().ToList();
        AssertBadge(feed.Single(t => t.GetProperty("campaignId").GetGuid() == atVenue));
        AssertNoBadge(feed.Single(t => t.GetProperty("campaignId").GetGuid() == plain));
        AssertBadge(await DataAsync(await api.Client(guest.Token).GetAsync($"/api/me/feed/{atVenue}")));
    }

    private static void AssertBadge(JsonElement tile)
    {
        Assert.Equal("Crown Hall", tile.GetProperty("venueName").GetString());
        Assert.Equal("https://cdn.example.com/venues/crown.png", tile.GetProperty("venueLogoUrl").GetString());
    }

    private static void AssertNoBadge(JsonElement tile)
    {
        Assert.Equal(JsonValueKind.Null, tile.GetProperty("venueName").ValueKind);
        Assert.Equal(JsonValueKind.Null, tile.GetProperty("venueLogoUrl").ValueKind);
    }

    // ----- helpers ----------------------------------------------------------------------------

    private sealed record Person(Guid Id, string Email, string Token);

    private MediaRetentionService Sweeper() => new(
        api.Services,
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Urls:InviterBase"] = "http://localhost",
            ["Notifications:MediaRetention:DryRun"] = "false",
        }).Build(),
        NullLogger<MediaRetentionService>.Instance);

    private static async Task<JsonElement> DataAsync(HttpResponseMessage res)
    {
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").Clone();
    }

    private async Task<Prices> PricesAsync()
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IPriceBook>().CurrentAsync();
    }

    private List<Application.Abstractions.EmailMessage> MailFor(Person p) =>
        api.Mail.Sent.Where(m => m.To == p.Email).ToList();

    private async Task<EventPlan> PlanAsync(Guid ev)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IPlanService>().ForCampaignAsync(ev);
    }

    private async Task<Person> UserAsync(SubscriptionTier tier = SubscriptionTier.None, DateTimeOffset? endsAt = null)
    {
        var id = Guid.NewGuid();
        var mail = $"venue-{id:N}@example.com";
        await api.WriteAsync(async db =>
        {
            var roles = await db.Roles.Where(r => r.Name == Roles.Customer).Select(r => r.Id).ToListAsync();
            var user = new AppUser
            {
                Id = id, Email = mail, DisplayName = "Person", PasswordHash = "x", IsActive = true,
                SubscriptionTier = tier, SubscriptionEndsAt = endsAt, CreatedAt = DateTimeOffset.UtcNow,
            };
            foreach (var r in roles) user.UserRoles.Add(new UserRole { RoleId = r });
            db.Users.Add(user);
        });
        return new Person(id, mail, api.TokenFor(id, "email", mail, [Roles.Customer]));
    }

    private async Task<(Person Owner, Guid VenueId)> VenueAsync(bool active, string? logo = null)
    {
        var owner = await UserAsync(SubscriptionTier.Venue, active ? DateTimeOffset.UtcNow.AddDays(20) : DateTimeOffset.UtcNow.AddDays(-1));
        var venueId = Guid.NewGuid();
        await api.WriteAsync(async db =>
        {
            db.Venues.Add(new Venue
            {
                Id = venueId, OwnerUserId = owner.Id, Name = "Crown Hall", LogoUrl = logo,
                Code = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(), CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await Task.CompletedTask;
        });
        return (owner, venueId);
    }

    private async Task<Guid> EventAsync(Person host, Guid? venueId, DateTimeOffset? eventAt = null)
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
                CreatedByUserId = host.Id, AccessTokenHash = Guid.NewGuid().ToString("N"), VenueId = venueId,
                Title = "Venue test", Slug = $"e-{id:N}", Status = CampaignStatus.Dispatched,
                EventStartAt = eventAt ?? DateTimeOffset.UtcNow.AddDays(10), CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await Task.CompletedTask;
        });
        return id;
    }

    private Task CelebrantAsync(Guid ev, string email, bool canManage) =>
        api.WriteAsync(async db =>
        {
            db.CampaignCelebrants.Add(new CampaignCelebrant
            {
                Id = Guid.NewGuid(), CampaignId = ev, Name = "The couple", Email = email, CanManage = canManage, CreatedAt = DateTimeOffset.UtcNow,
            });
            await Task.CompletedTask;
        });

    private Task GuestAsync(Guid ev, string email) =>
        api.WriteAsync(async db =>
        {
            db.Guests.Add(new Guest { Id = Guid.NewGuid(), CampaignId = ev, Name = "Guest", Email = email, CreatedAt = DateTimeOffset.UtcNow });
            await Task.CompletedTask;
        });

    private async Task<string> RegisterPhoneAsync(Person who)
    {
        var token = Convert.ToHexString(Guid.NewGuid().ToByteArray()) + Convert.ToHexString(Guid.NewGuid().ToByteArray());
        var r = await api.Client(who.Token).PostAsJsonAsync("/api/me/devices", new { token, platform = "ios" });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return token;
    }

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

    private async Task PhotoAsync(Guid ev, Guid album, long bytes, int order)
    {
        var id = Guid.NewGuid();
        var url = await api.Storage.PutAsync($"buckets/{album:N}/media/{id:N}.jpg", [1], "image/jpeg");
        await api.WriteAsync(async db =>
        {
            db.EventPhotos.Add(new EventPhoto
            {
                Id = id, CampaignId = ev, BucketId = album, Url = url, ThumbUrl = url, OriginalUrl = url,
                ContentType = "image/jpeg", SizeBytes = bytes, CreatedAt = DateTimeOffset.UtcNow.AddDays(-300).AddMinutes(order),
            });
            await db.SaveChangesAsync();
            await db.MediaBuckets.Where(b => b.Id == album).ExecuteUpdateAsync(s => s.SetProperty(b => b.UsedBytes, b => b.UsedBytes + bytes));
        });
    }
}
