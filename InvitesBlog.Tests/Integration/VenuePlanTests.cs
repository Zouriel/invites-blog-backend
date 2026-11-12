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
/// Studio (tier and kind "Venue" inside; owner, 2026-11-12) through the real API on real Postgres: it is a plan on
/// the ORGANISER's account, like Premium. What /api/plans says it is, that every event its subscriber organises is
/// covered, that the people an event is for may buy its Platinum pass, that the warnings when Studio ends reach the
/// organiser and the couple, that tiles and posts carry the organiser's own name and picture, and that the old
/// venue page, codes and staff are gone.
/// </summary>
[Collection("venue-plan")]
public sealed class VenuePlanTests(PostgresApiFixture api)
{
    private const long Mb = PlanCatalog.Mb;
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // ----- The catalogue and the offer --------------------------------------------------------

    [PostgresFact]
    public async Task The_catalogue_describes_studio_as_10_gb_events_at_775_a_month_with_no_discount()
    {
        var catalog = await DataAsync(await api.Client().GetAsync("/api/plans"));
        var venue = catalog.GetProperty("plans").EnumerateArray().Single(p => p.GetProperty("kind").GetString() == "Venue");

        Assert.Equal("Studio", venue.GetProperty("name").GetString());
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
    public async Task An_event_a_studio_account_organises_is_on_studio_and_offered_full_price_passes()
    {
        var owner = await StudioAsync(active: true);
        var ev = await EventAsync(owner);

        var p = await PricesAsync();
        var billing = await DataAsync(await api.Client(owner.Token).GetAsync($"/api/billing/events/{ev}"));
        var offer = billing.GetProperty("offer");
        Assert.Equal(p.GoldPass, offer.GetProperty("goldPass").GetDecimal());
        Assert.Equal(p.PlatinumPass, offer.GetProperty("platinumPass").GetDecimal());
        Assert.Equal(0, offer.GetProperty("venuePercent").GetInt32());
        Assert.Equal("Venue", billing.GetProperty("plan").GetString());
        Assert.True(billing.GetProperty("atVenue").GetBoolean());
        Assert.False(billing.GetProperty("venueEnded").GetBoolean());

        var plan = await PlanAsync(ev);
        Assert.Equal(PlanKind.Venue, plan.Kind);
        Assert.Equal(10 * PlanCatalog.Gb, plan.EventBytes);
        Assert.False(plan.Branded);
        Assert.Null(plan.CoveredUntil);
        Assert.True(PlanRules.IncludesFaceGrouping(plan));
        Assert.Equal(owner.Id, plan.VenueAccountId);

        var overview = await DataAsync(await api.Client(owner.Token).GetAsync("/api/billing"));
        Assert.Equal(p.VenueMonthly, overview.GetProperty("prices").GetProperty("venueMonthly").GetDecimal());
    }

    /// <summary>An organiser switched to Premium (the admin switch): their events are on Premium now.</summary>
    [PostgresFact]
    public async Task An_organiser_switched_to_premium_has_premium_events()
    {
        var owner = await StudioAsync(active: true);
        var ev = await EventAsync(owner);
        await api.WriteAsync(db => db.Users.Where(u => u.Id == owner.Id).ExecuteUpdateAsync(s => s
            .SetProperty(u => u.SubscriptionTier, SubscriptionTier.Premium)
            .SetProperty(u => u.SubscriptionEndsAt, DateTimeOffset.UtcNow.AddDays(20))));

        var plan = await PlanAsync(ev);
        Assert.Equal(PlanKind.Premium, plan.Kind);
        Assert.Null(plan.VenueAccountId);
        Assert.False(plan.VenueEnded);
    }

    // ----- Who may buy ------------------------------------------------------------------------

    [PostgresFact]
    public async Task A_celebrant_may_buy_a_platinum_pass_for_the_event_and_a_stranger_may_not()
    {
        var owner = await StudioAsync(active: true);
        var ev = await EventAsync(owner);
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

    // ----- When Studio ends --------------------------------------------------------------------

    [PostgresFact]
    public async Task When_studio_ends_the_organiser_and_the_couple_are_warned_and_offered_platinum()
    {
        var owner = await StudioAsync(active: true);
        var ev = await EventAsync(owner, eventAt: DateTimeOffset.UtcNow.AddYears(-1));
        var couple = await UserAsync();
        var managing = await UserAsync();
        await CelebrantAsync(ev, couple.Email, canManage: false);
        await CelebrantAsync(ev, managing.Email, canManage: true);
        var album = await AlbumAsync(ev, owner.Id);
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
        Assert.True(plan.VenueEnded);

        await Sweeper().RunOnceAsync(DateTimeOffset.UtcNow, default);
        var platinum = $"MVR {(await PricesAsync()).PlatinumPass:0}";
        foreach (var who in new[] { owner, couple, managing })
        {
            var mail = Assert.Single(MailFor(who));
            Assert.Contains("goes back to Free on", mail.Subject);
            Assert.Contains("Crown Hall's Studio plan", mail.Html);
            Assert.DoesNotContain("Venue", mail.Html);
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
            Assert.DoesNotContain("Venue", push.Title + push.Body);
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

    // ----- Billing lists the events you're in ------------------------------------------------

    [PostgresFact]
    public async Task Billing_lists_a_studio_event_for_its_couple_with_platinum_once_studio_ends_and_not_for_a_stranger()
    {
        var organiser = await StudioAsync(active: true);
        var ev = await EventAsync(organiser, eventAt: DateTimeOffset.UtcNow.AddYears(-1));
        var couple = await UserAsync();
        var stranger = await UserAsync();
        await CelebrantAsync(ev, couple.Email, canManage: false);

        // While Studio lasts: the couple sees the event under "for you", on the organiser's Studio plan.
        var row = ForYou(await DataAsync(await api.Client(couple.Token).GetAsync("/api/billing")), ev);
        Assert.Equal("celebrant", row.GetProperty("role").GetString());
        Assert.Equal("Venue", row.GetProperty("plan").GetString());
        Assert.False(row.GetProperty("venueEnded").GetBoolean());

        // Studio ends: back to Free, flagged, with the Platinum pass on offer.
        await api.WriteAsync(db => db.Users.Where(u => u.Id == organiser.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.SubscriptionEndsAt, DateTimeOffset.UtcNow.AddMinutes(-5))));
        var overview = await DataAsync(await api.Client(couple.Token).GetAsync("/api/billing"));
        row = ForYou(overview, ev);
        Assert.Equal("Free", row.GetProperty("plan").GetString());
        Assert.Equal("UploadsClosed", row.GetProperty("phase").GetString());
        Assert.True(row.GetProperty("venueEnded").GetBoolean());
        Assert.True(row.GetProperty("atVenue").GetBoolean());
        Assert.Equal((await PricesAsync()).PlatinumPass, row.GetProperty("offer").GetProperty("platinumPass").GetDecimal());
        Assert.DoesNotContain(overview.GetProperty("events").EnumerateArray(), e => e.GetProperty("campaignId").GetGuid() == ev);
        var quote = await api.Client(couple.Token).PostAsJsonAsync("/api/billing/quote", new { item = "platinum-pass", campaignId = ev });
        Assert.Equal(HttpStatusCode.OK, quote.StatusCode);

        // A stranger sees it nowhere.
        var theirs = await DataAsync(await api.Client(stranger.Token).GetAsync("/api/billing"));
        Assert.Empty(theirs.GetProperty("forYou").EnumerateArray());

        // The organiser sees it in their own list only.
        var mine = await DataAsync(await api.Client(organiser.Token).GetAsync("/api/billing"));
        var own = Assert.Single(mine.GetProperty("events").EnumerateArray(), e => e.GetProperty("campaignId").GetGuid() == ev);
        Assert.Equal("organiser", own.GetProperty("role").GetString());
    }

    /// <summary>
    /// Staff are gone (owner, 2026-11-12): a leftover staff row or venue row gives no access, no Billing row and no
    /// purchase rights, and an old venue link on an event counts for nothing.
    /// </summary>
    [PostgresFact]
    public async Task Leftover_venue_staff_and_venue_owners_get_no_rights_and_billing_lists_only_celebrants()
    {
        var venueOwner = await StudioAsync(active: true);
        var staff = await UserAsync();
        var oldVenue = await LeftoverVenueAsync(venueOwner, staff.Email);
        var organiser = await UserAsync();
        var linked = await EventAsync(organiser, leftoverVenue: oldVenue);
        var elsewhere = await EventAsync(organiser);
        var cancelled = await EventAsync(organiser);
        await api.WriteAsync(db => db.Campaigns.Where(c => c.Id == cancelled)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CampaignStatus.Cancelled)));
        var partner = await UserAsync();
        await CelebrantAsync(elsewhere, partner.Email, canManage: true);
        await CelebrantAsync(cancelled, partner.Email, canManage: true);

        var only = Assert.Single((await DataAsync(await api.Client(partner.Token).GetAsync("/api/billing"))).GetProperty("forYou").EnumerateArray());
        Assert.Equal(elsewhere, only.GetProperty("campaignId").GetGuid());
        Assert.Equal("manager", only.GetProperty("role").GetString());

        foreach (var who in new[] { staff, venueOwner })
        {
            Assert.Empty((await DataAsync(await api.Client(who.Token).GetAsync("/api/billing"))).GetProperty("forYou").EnumerateArray());
            Assert.Equal(HttpStatusCode.Forbidden, (await api.Client(who.Token).GetAsync($"/api/billing/events/{linked}")).StatusCode);
            var buy = await api.Client(who.Token).PostAsJsonAsync("/api/billing/quote", new { item = "platinum-pass", campaignId = linked });
            Assert.Equal(HttpStatusCode.Forbidden, buy.StatusCode);
        }

        // The linked event is its own organiser's: Free, no badge from the old venue row.
        var plan = await PlanAsync(linked);
        Assert.Equal(PlanKind.Free, plan.Kind);
        Assert.Null(plan.VenueAccountId);
        var tile = (await DataAsync(await api.Client(organiser.Token).GetAsync("/api/me/campaigns"))).EnumerateArray()
            .Single(t => t.GetProperty("id").GetGuid() == linked);
        AssertNoBadge(tile);
        Assert.Equal("Free", tile.GetProperty("plan").GetString());
    }

    // ----- The venue page is gone --------------------------------------------------------------

    [PostgresFact]
    public async Task The_venue_page_endpoints_are_gone_and_at_venue_is_always_false()
    {
        var owner = await StudioAsync(active: true);
        var ev = await EventAsync(owner);
        await LeftoverVenueAsync(owner, $"staff-{Guid.NewGuid():N}@example.com");
        var client = api.Client(owner.Token);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/venue")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync("/api/venue", new { name = "X" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PostAsJsonAsync("/api/venue/staff", new { email = "a@b.c" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/venue/events")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync($"/api/campaigns/{ev}/venue-link")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.PutAsJsonAsync($"/api/campaigns/{ev}/venue-link", new { code = "ABC123" })).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.DeleteAsync($"/api/campaigns/{ev}/venue-link")).StatusCode);

        // Kept in the account for shipped apps, always false: even an owner of an old venue row.
        var me = await DataAsync(await client.GetAsync("/api/auth/me"));
        Assert.False(me.GetProperty("atVenue").GetBoolean());
        Assert.Equal("Venue", me.GetProperty("subscriptionTier").GetString());
    }

    /// <summary>The batched plans Billing uses are exactly the one-at-a-time ones, whatever covers the event.</summary>
    [PostgresFact]
    public async Task Plans_worked_out_for_a_page_match_plans_worked_out_one_at_a_time()
    {
        var live = await StudioAsync(active: true);
        var ended = await StudioAsync(active: false);
        var premium = await UserAsync(SubscriptionTier.Premium, DateTimeOffset.UtcNow.AddDays(30));
        var plain = await UserAsync();
        var ids = new List<Guid>
        {
            await EventAsync(live),
            await EventAsync(ended, eventAt: DateTimeOffset.UtcNow.AddYears(-1)),
            await EventAsync(premium),
            await EventAsync(plain),
        };
        var withPass = await EventAsync(live);
        await api.WriteAsync(db => db.Campaigns.Where(c => c.Id == withPass).ExecuteUpdateAsync(s => s
            .SetProperty(c => c.EventPass, EventPassKind.Platinum)
            .SetProperty(c => c.EventPassUntil, DateTimeOffset.UtcNow.AddMonths(6))));
        ids.Add(withPass);
        await AlbumAsync(ids[3], plain.Id);

        using var scope = api.Services.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IPlanService>();
        var list = await api.ReadAsync(db => db.Campaigns.AsNoTracking().Where(c => ids.Contains(c.Id)).ToListAsync());
        var batch = await service.ForCampaignsAsync(list);
        Assert.Equal(ids.Count, batch.Count);
        var kinds = new HashSet<PlanKind>();
        foreach (var id in ids)
        {
            var single = await PlanAsync(id);
            Assert.Equal(single, batch[id]);
            kinds.Add(single.Kind);
        }
        Assert.Superset(new HashSet<PlanKind> { PlanKind.Venue, PlanKind.Free, PlanKind.Premium, PlanKind.PlatinumPass }, kinds);
        Assert.Equal(4, kinds.Count);
    }

    private static JsonElement ForYou(JsonElement overview, Guid ev) =>
        Assert.Single(overview.GetProperty("forYou").EnumerateArray(), e => e.GetProperty("campaignId").GetGuid() == ev);

    // ----- The badge --------------------------------------------------------------------------

    /// <summary>The badge is the organiser's own display name and profile picture, while they are on Studio.</summary>
    [PostgresFact]
    public async Task Tiles_and_posts_carry_the_organiser_s_name_and_picture_only_while_on_studio()
    {
        const string avatar = "https://cdn.example.com/avatars/crown.webp";
        var host = await StudioAsync(active: true, avatar: avatar);
        var other = await UserAsync();
        var studioEvent = await EventAsync(host);
        var plain = await EventAsync(other);
        var guest = await UserAsync();
        await GuestAsync(studioEvent, guest.Email);
        await GuestAsync(plain, guest.Email);
        await CelebrantAsync(plain, host.Email, canManage: true);

        // Me (hosting): /api/me/campaigns
        var mine = (await DataAsync(await api.Client(host.Token).GetAsync("/api/me/campaigns"))).EnumerateArray().ToList();
        var tile = mine.Single(t => t.GetProperty("id").GetGuid() == studioEvent);
        AssertBadge(tile, "Crown Hall", avatar);
        Assert.Equal("Venue", tile.GetProperty("plan").GetString());
        // An event they're only in (someone else organised it) has no badge: it is the organiser's plan that counts.
        AssertNoBadge(mine.Single(t => t.GetProperty("id").GetGuid() == plain));

        // Inbox: /api/me/invites
        var inbox = (await DataAsync(await api.Client(guest.Token).GetAsync("/api/me/invites"))).EnumerateArray().ToList();
        AssertBadge(inbox.Single(t => t.GetProperty("campaignId").GetGuid() == studioEvent), "Crown Hall", avatar);
        AssertNoBadge(inbox.Single(t => t.GetProperty("campaignId").GetGuid() == plain));

        // Feed: /api/me/feed and one post
        var feed = (await DataAsync(await api.Client(host.Token).GetAsync("/api/me/feed"))).GetProperty("items").EnumerateArray().ToList();
        AssertBadge(feed.Single(t => t.GetProperty("campaignId").GetGuid() == studioEvent), "Crown Hall", avatar);
        AssertBadge(await DataAsync(await api.Client(guest.Token).GetAsync($"/api/me/feed/{studioEvent}")), "Crown Hall", avatar);

        // A new name on Profile shows on the badge straight away.
        Assert.Equal(HttpStatusCode.OK, (await api.Client(host.Token).PutAsJsonAsync("/api/auth/me/name", new { displayName = "Coral Bay Resort" })).StatusCode);
        AssertBadge(await DataAsync(await api.Client(guest.Token).GetAsync($"/api/me/feed/{studioEvent}")), "Coral Bay Resort", avatar);

        // No picture: the name alone (the clients show its first letter).
        await api.WriteAsync(db => db.Users.Where(u => u.Id == host.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.AvatarUrl, (string?)null)));
        var noPicture = await DataAsync(await api.Client(guest.Token).GetAsync($"/api/me/feed/{studioEvent}"));
        Assert.Equal("Coral Bay Resort", noPicture.GetProperty("venueName").GetString());
        Assert.Equal(JsonValueKind.Null, noPicture.GetProperty("venueLogoUrl").ValueKind);

        // Studio ends: no badge anywhere.
        await api.WriteAsync(db => db.Users.Where(u => u.Id == host.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.SubscriptionEndsAt, DateTimeOffset.UtcNow.AddMinutes(-5))));
        AssertNoBadge(await DataAsync(await api.Client(guest.Token).GetAsync($"/api/me/feed/{studioEvent}")));
        AssertNoBadge((await DataAsync(await api.Client(guest.Token).GetAsync("/api/me/invites"))).EnumerateArray()
            .Single(t => t.GetProperty("campaignId").GetGuid() == studioEvent));
    }

    private static void AssertBadge(JsonElement tile, string name, string? picture)
    {
        Assert.Equal(name, tile.GetProperty("venueName").GetString());
        Assert.Equal(picture, tile.GetProperty("venueLogoUrl").GetString());
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

    private async Task<Person> UserAsync(SubscriptionTier tier = SubscriptionTier.None, DateTimeOffset? endsAt = null,
        string name = "Person", string? avatar = null)
    {
        var id = Guid.NewGuid();
        var mail = $"venue-{id:N}@example.com";
        await api.WriteAsync(async db =>
        {
            var roles = await db.Roles.Where(r => r.Name == Roles.Customer).Select(r => r.Id).ToListAsync();
            var user = new AppUser
            {
                Id = id, Email = mail, DisplayName = name, AvatarUrl = avatar, PasswordHash = "x", IsActive = true,
                SubscriptionTier = tier, SubscriptionEndsAt = endsAt, CreatedAt = DateTimeOffset.UtcNow,
            };
            foreach (var r in roles) user.UserRoles.Add(new UserRole { RoleId = r });
            db.Users.Add(user);
        });
        return new Person(id, mail, api.TokenFor(id, "email", mail, [Roles.Customer]));
    }

    /// <summary>An account on Studio (tier Venue): in force for 20 more days, or ended yesterday.</summary>
    private Task<Person> StudioAsync(bool active, string? avatar = null) =>
        UserAsync(SubscriptionTier.Venue, active ? DateTimeOffset.UtcNow.AddDays(20) : DateTimeOffset.UtcNow.AddDays(-1), "Crown Hall", avatar);

    /// <summary>A row left from the old venue page, with a staff member: nothing may read either any more.</summary>
    private async Task<Guid> LeftoverVenueAsync(Person owner, string staffEmail)
    {
        var venueId = Guid.NewGuid();
        await api.WriteAsync(async db =>
        {
            db.Venues.Add(new Venue
            {
                Id = venueId, OwnerUserId = owner.Id, Name = "Old Venue Row", LogoUrl = "https://cdn.example.com/venues/old.png",
                Code = Guid.NewGuid().ToString("N")[..6].ToUpperInvariant(), CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.VenueStaff.Add(new VenueStaff { Id = Guid.NewGuid(), VenueId = venueId, Email = staffEmail, CreatedAt = DateTimeOffset.UtcNow });
            await Task.CompletedTask;
        });
        return venueId;
    }

    private async Task<Guid> EventAsync(Person host, DateTimeOffset? eventAt = null, Guid? leftoverVenue = null)
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
                CreatedByUserId = host.Id, AccessTokenHash = Guid.NewGuid().ToString("N"), VenueId = leftoverVenue,
                Title = "Studio test", Slug = $"e-{id:N}", Status = CampaignStatus.Dispatched,
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
