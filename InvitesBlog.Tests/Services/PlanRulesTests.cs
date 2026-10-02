using InvitesBlog.Application.Plans;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>What each plan lets an event do, and when its photos run out.</summary>
public class PlanRulesTests
{
    private static readonly DateTimeOffset Now = new(2027, 3, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Night = new(2027, 2, 1, 18, 0, 0, TimeSpan.Zero);
    private static readonly Guid VenueId = Guid.NewGuid();

    private static EventPlan Evaluate(
        EventPassKind pass = EventPassKind.None, DateTimeOffset? passUntil = null, DateTimeOffset? keep = null,
        bool? venueActive = null, DateTimeOffset? venueEnded = null,
        DateTimeOffset? legacyCover = null, DateTimeOffset? now = null, DateTimeOffset? night = null,
        (bool, DateTimeOffset?)? premium = null) =>
        PlanRules.Evaluate(
            now ?? Now, night ?? Night, pass, passUntil, keep,
            venueActive is { } active ? (VenueId, active, venueEnded) : null,
            legacyCover, null, Guid.NewGuid(), premium);

    [Fact]
    public void A_free_event_gets_1_gb_one_album_one_night_90_days_and_the_mark()
    {
        var plan = Evaluate();

        Assert.Equal(PlanKind.Free, plan.Kind);
        Assert.Equal(1 * PlanCatalog.Gb, plan.EventBytes);
        Assert.Null(plan.AccountBytes);
        Assert.Equal(1, plan.MaxBuckets);
        Assert.Equal(1, plan.MaxWindowDays);
        Assert.Equal(0, plan.IncludedInvites);
        Assert.False(plan.PrivateAlbums);
        Assert.True(plan.Branded);
        Assert.Equal(Night.AddDays(90), plan.CoveredUntil);
        Assert.Equal(MediaPhase.Active, plan.Phase);
    }

    [Fact]
    public void A_party_pass_gets_25_gb_two_albums_three_days_100_invitations_and_no_mark()
    {
        var until = PlanRules.PassUntil(Night, Now);
        var plan = Evaluate(EventPassKind.Party, until);

        Assert.Equal(PlanKind.PartyPass, plan.Kind);
        Assert.Equal(25 * PlanCatalog.Gb, plan.EventBytes);
        Assert.Equal(2, plan.MaxBuckets);
        Assert.Equal(3, plan.MaxWindowDays);
        Assert.Equal(100, plan.IncludedInvites);
        Assert.False(plan.PrivateAlbums);
        Assert.False(plan.Branded);
        Assert.Equal(until, plan.CoveredUntil);
    }

    [Fact]
    public void A_wedding_pass_gets_50_gb_five_albums_five_days_500_invitations_and_private_albums()
    {
        var plan = Evaluate(EventPassKind.Wedding, Now.AddMonths(6));

        Assert.Equal(PlanKind.WeddingPass, plan.Kind);
        Assert.Equal(50 * PlanCatalog.Gb, plan.EventBytes);
        Assert.Equal(MediaBucket.MaxPerCampaign, plan.MaxBuckets);
        Assert.Equal(5, plan.MaxWindowDays);
        Assert.Equal(500, plan.IncludedInvites);
        Assert.True(plan.PrivateAlbums);
        Assert.False(plan.Branded);
    }

    [Fact]
    public void Premium_gives_3_gb_no_mark_and_keeps_the_photos_while_it_lasts()
    {
        // Long after the free 90 days, the organiser still subscribed with no end date.
        var plan = Evaluate(now: Night.AddDays(400), premium: (true, null));

        Assert.Equal(PlanKind.Premium, plan.Kind);
        Assert.Equal(3 * PlanCatalog.Gb, plan.EventBytes);
        Assert.Equal(1, plan.MaxBuckets);
        Assert.Equal(1, plan.MaxWindowDays);
        Assert.Equal(0, plan.IncludedInvites);
        Assert.False(plan.Branded);
        Assert.Null(plan.CoveredUntil);
        Assert.Equal(MediaPhase.Active, plan.Phase);
    }

    [Fact]
    public void A_monthly_premium_covers_to_its_end_and_the_lapse_counts_from_there()
    {
        var ends = Night.AddDays(200);
        var running = Evaluate(now: Night.AddDays(150), premium: (true, ends));
        Assert.Equal(PlanKind.Premium, running.Kind);
        Assert.Equal(ends, running.CoveredUntil);

        // Ended: back to Free's space and the mark, and the photos lapse from when it ended.
        var ended = Evaluate(now: ends.AddDays(10), premium: (false, ends));
        Assert.Equal(PlanKind.Free, ended.Kind);
        Assert.Equal(1 * PlanCatalog.Gb, ended.EventBytes);
        Assert.True(ended.Branded);
        Assert.Equal(ends, ended.CoveredUntil);
        Assert.Equal(MediaPhase.UploadsClosed, ended.Phase);
    }

    [Fact]
    public void A_pass_outranks_premium()
    {
        var plan = Evaluate(EventPassKind.Party, PlanRules.PassUntil(Night, Now), premium: (true, null));
        Assert.Equal(PlanKind.PartyPass, plan.Kind);
        Assert.Equal(25 * PlanCatalog.Gb, plan.EventBytes);
        Assert.Null(plan.CoveredUntil);
    }

    [Fact]
    public void A_pass_runs_a_year_from_the_event_or_from_today_once_it_has_passed()
    {
        Assert.Equal(Night.AddMonths(12), PlanRules.PassUntil(Night, Night.AddDays(-30)));
        Assert.Equal(Now.AddMonths(12), PlanRules.PassUntil(Night, Now));
    }

    /// <summary>
    /// A venue no longer covers its events: it buys their passes (at the venue price), so an event it
    /// runs is on whatever pass it has, and Free without one.
    /// </summary>
    [Fact]
    public void A_venue_s_event_is_on_its_pass_not_on_the_venue()
    {
        var withPass = Evaluate(EventPassKind.Party, Now.AddMonths(2), venueActive: true);
        var without = Evaluate(venueActive: true);

        Assert.Equal(PlanKind.PartyPass, withPass.Kind);
        Assert.Equal(PlanCatalog.PartyEventBytes, withPass.EventBytes);
        Assert.Null(withPass.AccountBytes);
        Assert.Null(withPass.VenueId);
        Assert.Equal(PlanKind.Free, without.Kind);
        Assert.NotNull(without.CoveredUntil);
    }

    [Fact]
    public void An_ended_venue_covers_its_events_until_it_ended_then_the_full_lapse()
    {
        var ended = Night.AddDays(400);
        var plan = Evaluate(venueActive: false, venueEnded: ended, now: ended.AddDays(40));

        Assert.Equal(PlanKind.Free, plan.Kind);
        Assert.Null(plan.VenueId);
        Assert.Equal(ended, plan.CoveredUntil);
        Assert.Equal(MediaPhase.OrganiserOnly, plan.Phase);
    }

    [Fact]
    public void An_expired_pass_counts_for_nothing_but_its_cover_date()
    {
        var ended = Night.AddDays(120);
        var plan = Evaluate(EventPassKind.Wedding, ended, now: ended.AddDays(1));

        Assert.Equal(PlanKind.Free, plan.Kind);
        Assert.Equal(ended, plan.CoveredUntil);
        Assert.Equal(MediaPhase.UploadsClosed, plan.Phase);
    }

    /// <summary>"Keep your photos" keeps the album online without giving the event anything else.</summary>
    [Fact]
    public void Keeping_the_photos_moves_the_cover_and_nothing_else()
    {
        var keep = Night.AddMonths(30);
        var plan = Evaluate(keep: keep, now: Night.AddMonths(20));

        Assert.Equal(PlanKind.Free, plan.Kind);
        Assert.Equal(keep, plan.CoveredUntil);
        Assert.Equal(MediaPhase.Active, plan.Phase);
        Assert.Equal(1 * PlanCatalog.Gb, plan.EventBytes);
    }

    [Theory]
    [InlineData(0, MediaPhase.UploadsClosed)]
    [InlineData(29, MediaPhase.UploadsClosed)]
    [InlineData(30, MediaPhase.OrganiserOnly)]
    [InlineData(89, MediaPhase.OrganiserOnly)]
    [InlineData(90, MediaPhase.Deleted)]
    public void Photos_move_through_the_phases_after_the_cover_ends(int days, MediaPhase expected)
    {
        var end = Night.AddDays(90);
        Assert.Equal(expected, PlanRules.PhaseOf(end.AddDays(days).AddMinutes(1), end, null));
    }

    [Fact]
    public void An_event_from_before_the_plans_gets_its_plan_s_space_and_no_more()
    {
        // Covered by the pre-plans promise, and still sized by the plan: Free is 1 GB.
        var plan = Evaluate(legacyCover: Night.AddMonths(6), now: Night.AddDays(1));

        Assert.Equal(PlanCatalog.FreeEventBytes, plan.EventBytes);
        Assert.Equal(Night.AddMonths(6), plan.CoveredUntil);
    }

    [Fact]
    public void A_later_legacy_cover_date_wins()
    {
        var cover = Night.AddDays(300);
        Assert.Equal(cover, Evaluate(legacyCover: cover).CoveredUntil);
    }

    [Fact]
    public void Venue_passes_cost_half()
    {
        Assert.Equal(100m, Prices.Defaults.VenuePassPrice(EventPassKind.Party));
        Assert.Equal(350m, Prices.Defaults.VenuePassPrice(EventPassKind.Wedding));
    }

    [Fact]
    public void The_catalogue_shows_the_price_book_not_the_defaults()
    {
        var changed = Prices.Defaults with { PartyPass = 249m, SendingPerBlock = 60m, VenueDiscountPercent = 20, PremiumMonthly = 500m };
        var c = PlanCatalog.Describe(changed);
        var party = c.Plans.Single(p => p.Kind == "PartyPass");
        Assert.Equal(249m, party.Price);
        Assert.Equal(500m, c.Plans.Single(p => p.Kind == "Premium").Price);
        Assert.Equal(60m, c.Sending.PerBlock);
        Assert.Equal(20, c.VenueDiscountPercent);
        // Limits are not prices: they stay where the code enforces them.
        Assert.Equal(PlanCatalog.PartyEventBytes, party.EventBytes);
    }

    [Fact]
    public void Prices_that_make_no_sense_are_refused()
    {
        Assert.Empty(Prices.Defaults.Problems());
        Assert.NotEmpty((Prices.Defaults with { PartyPass = 0 }).Problems());
        Assert.NotEmpty((Prices.Defaults with { WeddingPass = 100 }).Problems());
        Assert.NotEmpty((Prices.Defaults with { PremiumMonthly = 0 }).Problems());
        Assert.NotEmpty((Prices.Defaults with { VenueDiscountPercent = 95 }).Problems());
    }

    [Fact]
    public void The_catalogue_lists_every_plan_in_rufiyaa()
    {
        var catalog = PlanCatalog.Describe();

        Assert.Equal("MVR", catalog.Currency);
        Assert.Equal(["Free", "PartyPass", "WeddingPass", "Premium", "Venue"], catalog.Plans.Select(p => p.Kind));
        // Premium and Venue are monthly subscriptions; a venue gets its events' passes at half price.
        Assert.Equal([0m, 199m, 699m, 450m, 2300m], catalog.Plans.Select(p => p.Price));
        Assert.Equal(["every event", "per event", "per event", "per month", "per month"], catalog.Plans.Select(p => p.Billing));
        Assert.Equal([1 * PlanCatalog.Gb, 25 * PlanCatalog.Gb, 50 * PlanCatalog.Gb, 3 * PlanCatalog.Gb, (long?)null],
            catalog.Plans.Select(p => p.EventBytes));
        Assert.Equal(50, catalog.VenueDiscountPercent);
        Assert.Equal(150m, catalog.KeepPhotos.Price);
        Assert.Equal(50m, catalog.Sending.PerBlock);
        Assert.Equal(100, catalog.Sending.BlockSize);
    }
}

/// <summary>Putting a pass on an event: the one rule the admin and a checkout share.</summary>
public class EventPassesTests
{
    private static readonly DateTimeOffset Now = new(2027, 3, 1, 12, 0, 0, TimeSpan.Zero);

    private static Campaign Event(DateTimeOffset night) => new() { Id = Guid.NewGuid(), Title = "Wedding", EventStartAt = night };

    [Fact]
    public void A_new_pass_runs_a_year_from_the_event()
    {
        var c = Event(Now.AddMonths(2));
        EventPasses.Apply(c, EventPassKind.Party, Now);

        Assert.Equal(EventPassKind.Party, c.EventPass);
        Assert.Equal(Now.AddMonths(2).AddMonths(12), c.EventPassUntil);
    }

    [Fact]
    public void The_same_pass_again_adds_a_year()
    {
        var c = Event(Now.AddMonths(2));
        EventPasses.Apply(c, EventPassKind.Wedding, Now);
        var first = c.EventPassUntil!.Value;
        EventPasses.Apply(c, EventPassKind.Wedding, Now);

        Assert.Equal(first.AddMonths(12), c.EventPassUntil);
    }

    [Fact]
    public void Moving_up_to_wedding_keeps_the_later_end_and_a_party_pass_never_replaces_a_wedding_one()
    {
        var c = Event(Now.AddMonths(2));
        EventPasses.Apply(c, EventPassKind.Party, Now);
        EventPasses.Apply(c, EventPassKind.Wedding, Now);
        Assert.Equal(EventPassKind.Wedding, c.EventPass);

        EventPasses.Apply(c, EventPassKind.Party, Now);
        Assert.Equal(EventPassKind.Wedding, c.EventPass);
    }

    [Fact]
    public void Taking_a_pass_away_ends_it_today_so_the_lapse_counts_from_now()
    {
        var c = Event(Now.AddMonths(2));
        EventPasses.Apply(c, EventPassKind.Wedding, Now);
        EventPasses.Apply(c, EventPassKind.None, Now);

        Assert.Equal(EventPassKind.None, c.EventPass);
        Assert.Equal(Now, c.EventPassUntil);
    }
}
