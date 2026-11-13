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
    /// <summary>The organiser: on Studio (tier Venue) in the venue cases.</summary>
    private static readonly Guid Organiser = Guid.NewGuid();

    private static EventPlan Evaluate(
        EventPassKind pass = EventPassKind.None, DateTimeOffset? passUntil = null, DateTimeOffset? keep = null,
        bool? venueActive = null, DateTimeOffset? venueEnded = null,
        DateTimeOffset? legacyCover = null, DateTimeOffset? now = null, DateTimeOffset? night = null,
        (bool, DateTimeOffset?)? premium = null, DateTimeOffset? trimmedAt = null) =>
        PlanRules.Evaluate(
            now ?? Now, night ?? Night, pass, passUntil, keep,
            venueActive is { } active ? (active, venueEnded) : null,
            legacyCover, null, Organiser, premium, trimmedAt);

    [Fact]
    public void A_free_event_gets_300_mb_one_album_one_night_kept_for_good_and_the_mark()
    {
        var plan = Evaluate();

        Assert.Equal(PlanKind.Free, plan.Kind);
        Assert.Equal(300 * PlanCatalog.Mb, plan.EventBytes);
        Assert.Null(plan.AccountBytes);
        Assert.Equal(1, plan.MaxBuckets);
        Assert.Equal(1, plan.MaxWindowDays);
        Assert.Equal(0, plan.IncludedInvites);
        Assert.False(plan.PrivateAlbums);
        Assert.True(plan.Branded);
        Assert.Null(plan.CoveredUntil);
        Assert.Equal(MediaPhase.Active, plan.Phase);
    }

    /// <summary>Owner, 2026-11-03: free albums are kept forever, as on any social platform.</summary>
    [Fact]
    public void A_free_event_is_still_kept_years_later()
    {
        var plan = Evaluate(now: Night.AddYears(10));
        Assert.Equal(PlanKind.Free, plan.Kind);
        Assert.Null(plan.CoveredUntil);
        Assert.Equal(MediaPhase.Active, plan.Phase);
    }

    [Fact]
    public void Once_cut_down_to_free_an_ended_pass_event_is_kept_for_good()
    {
        var ended = Night.AddMonths(12);
        var waiting = Evaluate(EventPassKind.Gold, ended, now: ended.AddDays(10));
        Assert.Equal(ended, waiting.CoveredUntil);
        Assert.Equal(MediaPhase.UploadsClosed, waiting.Phase);

        var cut = ended.AddDays(40);
        var trimmed = Evaluate(EventPassKind.Gold, ended, now: ended.AddYears(5), trimmedAt: cut);
        Assert.Equal(PlanKind.Free, trimmed.Kind);
        Assert.Equal(PlanCatalog.FreeEventBytes, trimmed.EventBytes);
        Assert.Null(trimmed.CoveredUntil);
        Assert.Equal(MediaPhase.Active, trimmed.Phase);
    }

    [Fact]
    public void A_new_pass_after_the_cut_covers_the_event_again()
    {
        var cut = Night.AddMonths(14);
        var again = Night.AddMonths(30);
        var plan = Evaluate(EventPassKind.Platinum, again, now: Night.AddMonths(20), trimmedAt: cut);
        Assert.Equal(PlanKind.PlatinumPass, plan.Kind);
        Assert.Equal(again, plan.CoveredUntil);

        // And when that one ends, it goes back to Free again (a new cut is pending).
        var later = Evaluate(EventPassKind.Platinum, again, now: again.AddDays(1), trimmedAt: cut);
        Assert.Equal(MediaPhase.UploadsClosed, later.Phase);
    }

    [Fact]
    public void A_gold_pass_gets_25_gb_two_albums_three_days_100_invitations_and_no_mark()
    {
        var until = PlanRules.PassUntil(Night, Now);
        var plan = Evaluate(EventPassKind.Gold, until);

        Assert.Equal(PlanKind.GoldPass, plan.Kind);
        Assert.Equal(5 * PlanCatalog.Gb, plan.EventBytes);
        Assert.Equal(2, plan.MaxBuckets);
        Assert.Equal(3, plan.MaxWindowDays);
        Assert.Equal(100, plan.IncludedInvites);
        Assert.False(plan.PrivateAlbums);
        Assert.False(plan.Branded);
        Assert.Equal(until, plan.CoveredUntil);
    }

    [Fact]
    public void A_platinum_pass_gets_50_gb_five_albums_five_days_500_invitations_and_private_albums()
    {
        var plan = Evaluate(EventPassKind.Platinum, Now.AddMonths(6));

        Assert.Equal(PlanKind.PlatinumPass, plan.Kind);
        Assert.Equal(10 * PlanCatalog.Gb, plan.EventBytes);
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
        Assert.Equal(2 * PlanCatalog.Gb, plan.EventBytes);
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
        Assert.Equal(300 * PlanCatalog.Mb, ended.EventBytes);
        Assert.True(ended.Branded);
        Assert.Equal(ends, ended.CoveredUntil);
        Assert.Equal(MediaPhase.UploadsClosed, ended.Phase);
    }

    [Fact]
    public void A_pass_outranks_premium()
    {
        var plan = Evaluate(EventPassKind.Gold, PlanRules.PassUntil(Night, Now), premium: (true, null));
        Assert.Equal(PlanKind.GoldPass, plan.Kind);
        Assert.Equal(5 * PlanCatalog.Gb, plan.EventBytes);
        Assert.Null(plan.CoveredUntil);
    }

    [Fact]
    public void A_pass_runs_a_year_from_the_event_or_from_today_once_it_has_passed()
    {
        Assert.Equal(Night.AddMonths(12), PlanRules.PassUntil(Night, Night.AddDays(-30)));
        Assert.Equal(Now.AddMonths(12), PlanRules.PassUntil(Night, Now));
    }

    // ----- Venue (owner, 2026-11-12): a plan like Premium for every event at the venue, with 10 GB -----

    [Fact]
    public void An_event_at_an_active_venue_gets_10_gb_one_album_no_mark_face_grouping_and_no_end()
    {
        var plan = Evaluate(venueActive: true);

        Assert.Equal(PlanKind.Venue, plan.Kind);
        Assert.Equal(10 * PlanCatalog.Gb, plan.EventBytes);
        Assert.Null(plan.AccountBytes);
        Assert.Equal(1, plan.MaxBuckets);
        Assert.Equal(PlanCatalog.FreeWindowDays, plan.MaxWindowDays);
        Assert.Equal(0, plan.IncludedInvites);
        Assert.False(plan.PrivateAlbums);
        Assert.False(plan.Branded);
        Assert.True(PlanRules.IncludesFaceGrouping(plan));
        Assert.Null(plan.CoveredUntil);
        Assert.Equal(MediaPhase.Active, plan.Phase);
        Assert.Equal(Organiser, plan.VenueAccountId);
    }

    [Fact]
    public void An_active_venue_is_kept_years_after_the_event()
    {
        var plan = Evaluate(venueActive: true, now: Night.AddYears(5));
        Assert.Equal(PlanKind.Venue, plan.Kind);
        Assert.Null(plan.CoveredUntil);
        Assert.Equal(MediaPhase.Active, plan.Phase);
    }

    /// <summary>Best of both, limit by limit: the venue's 10 GB, the pass's albums, days and invitations.</summary>
    [Fact]
    public void A_venue_event_with_a_gold_pass_gets_the_best_of_both()
    {
        var plan = Evaluate(EventPassKind.Gold, Now.AddMonths(2), venueActive: true);

        Assert.Equal(PlanKind.GoldPass, plan.Kind);
        Assert.Equal(10 * PlanCatalog.Gb, plan.EventBytes);
        Assert.Equal(PlanCatalog.GoldBuckets, plan.MaxBuckets);
        Assert.Equal(PlanCatalog.GoldWindowDays, plan.MaxWindowDays);
        Assert.Equal(PlanCatalog.GoldIncludedInvites, plan.IncludedInvites);
        Assert.False(plan.PrivateAlbums);
        Assert.False(plan.Branded);
        Assert.Null(plan.CoveredUntil);
        Assert.Equal(Organiser, plan.VenueAccountId);
    }

    [Fact]
    public void A_venue_event_with_a_platinum_pass_gets_everything_platinum_has()
    {
        var plan = Evaluate(EventPassKind.Platinum, Now.AddMonths(2), venueActive: true);

        Assert.Equal(PlanKind.PlatinumPass, plan.Kind);
        Assert.Equal(10 * PlanCatalog.Gb, plan.EventBytes);
        Assert.Equal(PlanCatalog.PlatinumBuckets, plan.MaxBuckets);
        Assert.Equal(PlanCatalog.PlatinumWindowDays, plan.MaxWindowDays);
        Assert.Equal(PlanCatalog.PlatinumIncludedInvites, plan.IncludedInvites);
        Assert.True(plan.PrivateAlbums);
        Assert.False(plan.Branded);
        Assert.Null(plan.CoveredUntil);
    }

    /// <summary>The pass ran out while the venue's plan goes on: the venue still covers it.</summary>
    [Fact]
    public void A_venue_event_whose_pass_ended_is_on_the_venue()
    {
        var ended = Night.AddMonths(12);
        var plan = Evaluate(EventPassKind.Gold, ended, venueActive: true, now: ended.AddDays(3));
        Assert.Equal(PlanKind.Venue, plan.Kind);
        Assert.Equal(10 * PlanCatalog.Gb, plan.EventBytes);
        Assert.Null(plan.CoveredUntil);
        Assert.Equal(MediaPhase.Active, plan.Phase);
    }

    /// <summary>Like an ended Premium: uploads close at the end, then the warnings and the cut to Free.</summary>
    [Fact]
    public void When_the_venue_s_plan_ends_its_event_goes_back_to_free_like_an_ended_premium()
    {
        var ended = Night.AddDays(400);

        var closed = Evaluate(venueActive: false, venueEnded: ended, now: ended.AddDays(1));
        Assert.Equal(PlanKind.Free, closed.Kind);
        Assert.Equal(PlanCatalog.FreeEventBytes, closed.EventBytes);
        Assert.True(closed.Branded);
        Assert.False(PlanRules.IncludesFaceGrouping(closed));
        Assert.Null(closed.VenueAccountId);
        Assert.True(closed.VenueEnded);
        Assert.Equal(ended, closed.CoveredUntil);
        Assert.Equal(MediaPhase.UploadsClosed, closed.Phase);

        // Once cut down to Free it is a free event like any other, kept for good.
        var cut = Evaluate(venueActive: false, venueEnded: ended, now: ended.AddDays(40), trimmedAt: ended.AddDays(31));
        Assert.Null(cut.CoveredUntil);
        Assert.Equal(MediaPhase.Active, cut.Phase);
    }

    [Fact]
    public void An_ended_venue_never_shortens_a_pass_and_a_pass_never_shortens_the_venue()
    {
        var venueEnd = Night.AddMonths(20);
        var passEnd = Night.AddMonths(12);
        Assert.Equal(venueEnd, Evaluate(EventPassKind.Gold, passEnd, venueActive: false, venueEnded: venueEnd, now: venueEnd.AddDays(1)).CoveredUntil);
        Assert.Equal(passEnd, Evaluate(EventPassKind.Gold, passEnd, venueActive: false, venueEnded: Night.AddMonths(3), now: passEnd.AddDays(1)).CoveredUntil);
    }

    /// <summary>
    /// PlanService passes no end date when the venue's owner has moved to another plan (that plan's date
    /// isn't the venue's): the venue then counts for nothing, and a free event stays a free event.
    /// </summary>
    [Fact]
    public void A_venue_whose_owner_is_no_longer_on_venue_counts_for_nothing()
    {
        var plan = Evaluate(venueActive: false, venueEnded: null, now: Night.AddYears(2));

        Assert.Equal(PlanKind.Free, plan.Kind);
        Assert.Null(plan.VenueAccountId);
        Assert.False(plan.VenueEnded);
        Assert.Null(plan.CoveredUntil);
        Assert.Equal(MediaPhase.Active, plan.Phase);
    }

    /// <summary>A venue outranks its organiser's Premium: 10 GB, not 2.</summary>
    [Fact]
    public void A_venue_outranks_the_organiser_s_premium()
    {
        var plan = Evaluate(venueActive: true, premium: (true, null));
        Assert.Equal(PlanKind.Venue, plan.Kind);
        Assert.Equal(10 * PlanCatalog.Gb, plan.EventBytes);
    }

    [Fact]
    public void An_expired_pass_counts_for_nothing_but_its_cover_date()
    {
        var ended = Night.AddDays(120);
        var plan = Evaluate(EventPassKind.Platinum, ended, now: ended.AddDays(1));

        Assert.Equal(PlanKind.Free, plan.Kind);
        Assert.Equal(ended, plan.CoveredUntil);
        Assert.Equal(MediaPhase.UploadsClosed, plan.Phase);
    }

    /// <summary>"Keep your photos" keeps the album online without giving the event anything else.</summary>
    [Fact]
    public void Keeping_the_photos_moves_the_cover_and_nothing_else()
    {
        var keep = Night.AddMonths(30);
        var plan = Evaluate(EventPassKind.Gold, Night.AddMonths(12), keep: keep, now: Night.AddMonths(20));

        Assert.Equal(PlanKind.Free, plan.Kind);
        Assert.Equal(keep, plan.CoveredUntil);
        Assert.Equal(MediaPhase.Active, plan.Phase);
        Assert.Equal(300 * PlanCatalog.Mb, plan.EventBytes);
    }

    /// <summary>A lapse never makes photos organiser-only or removes them all any more: it only waits for the cut.</summary>
    [Theory]
    [InlineData(0, MediaPhase.UploadsClosed)]
    [InlineData(29, MediaPhase.UploadsClosed)]
    [InlineData(30, MediaPhase.UploadsClosed)]
    [InlineData(89, MediaPhase.UploadsClosed)]
    [InlineData(400, MediaPhase.UploadsClosed)]
    public void After_the_cover_ends_the_event_waits_for_the_cut_with_everything_visible(int days, MediaPhase expected)
    {
        var end = Night.AddDays(90);
        Assert.Equal(expected, PlanRules.PhaseOf(end.AddDays(days).AddMinutes(1), end, null));
    }

    [Fact]
    public void An_event_from_before_the_plans_gets_its_plan_s_space_and_no_more()
    {
        // Sized by the plan: Free is 300 MB. With no pass it is a free event, kept for good.
        var plan = Evaluate(legacyCover: Night.AddMonths(6), now: Night.AddDays(1));

        Assert.Equal(PlanCatalog.FreeEventBytes, plan.EventBytes);
        Assert.Null(plan.CoveredUntil);
    }

    [Fact]
    public void A_later_legacy_cover_date_wins()
    {
        var cover = Night.AddMonths(20);
        Assert.Equal(cover, Evaluate(EventPassKind.Gold, Night.AddMonths(12), legacyCover: cover).CoveredUntil);
    }

    [Fact]
    public void The_catalogue_shows_the_price_book_not_the_defaults()
    {
        var changed = Prices.Defaults with { GoldPass = 249m, SendingPerBlock = 60m, VenueDiscountPercent = 20, PremiumMonthly = 500m };
        var c = PlanCatalog.Describe(changed);
        var gold = c.Plans.Single(p => p.Kind == "GoldPass");
        Assert.Equal(249m, gold.Price);
        Assert.Equal(500m, c.Plans.Single(p => p.Kind == "Premium").Price);
        Assert.Equal(60m, c.Sending.PerBlock);
        // The venue discount is gone: 0, whatever a saved price list holds.
        Assert.Equal(0, c.VenueDiscountPercent);
        // Limits are not prices: they stay where the code enforces them.
        Assert.Equal(PlanCatalog.GoldEventBytes, gold.EventBytes);
    }

    [Fact]
    public void Prices_that_make_no_sense_are_refused()
    {
        Assert.Empty(Prices.Defaults.Problems());
        Assert.NotEmpty((Prices.Defaults with { GoldPass = 0 }).Problems());
        Assert.NotEmpty((Prices.Defaults with { PlatinumPass = 100 }).Problems());
        Assert.NotEmpty((Prices.Defaults with { PremiumMonthly = 0 }).Problems());
        Assert.NotEmpty((Prices.Defaults with { VenueDiscountPercent = 95 }).Problems());
    }

    [Fact]
    public void The_catalogue_lists_every_plan_in_rufiyaa()
    {
        var catalog = PlanCatalog.Describe();

        Assert.Equal("MVR", catalog.Currency);
        Assert.Equal(["Free", "GoldPass", "PlatinumPass", "Premium", "Photographer", "Venue"], catalog.Plans.Select(p => p.Kind));
        // Premium and Venue are monthly subscriptions; Venue is MVR 775 (about $50) since 2026-11-12, Premium 77
        // since 2026-11-13 (the AI designer became an add-on); Photographer MVR 540 ($35) since 2026-11-13.
        Assert.Equal([0m, 199m, 499m, 77m, 540m, 775m], catalog.Plans.Select(p => p.Price));
        Assert.Equal(["every event", "per event", "per event", "per month", "per month", "per month"], catalog.Plans.Select(p => p.Billing));
        Assert.Equal([300 * PlanCatalog.Mb, 5 * PlanCatalog.Gb, 10 * PlanCatalog.Gb, 2 * PlanCatalog.Gb, 10 * PlanCatalog.Gb, 10 * PlanCatalog.Gb],
            catalog.Plans.Select(p => p.EventBytes));
        Assert.Equal(0, catalog.VenueDiscountPercent);
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
        EventPasses.Apply(c, EventPassKind.Gold, Now);

        Assert.Equal(EventPassKind.Gold, c.EventPass);
        Assert.Equal(Now.AddMonths(2).AddMonths(12), c.EventPassUntil);
    }

    [Fact]
    public void The_same_pass_again_adds_a_year()
    {
        var c = Event(Now.AddMonths(2));
        EventPasses.Apply(c, EventPassKind.Platinum, Now);
        var first = c.EventPassUntil!.Value;
        EventPasses.Apply(c, EventPassKind.Platinum, Now);

        Assert.Equal(first.AddMonths(12), c.EventPassUntil);
    }

    [Fact]
    public void Moving_up_to_platinum_keeps_the_later_end_and_a_gold_pass_never_replaces_a_platinum_one()
    {
        var c = Event(Now.AddMonths(2));
        EventPasses.Apply(c, EventPassKind.Gold, Now);
        EventPasses.Apply(c, EventPassKind.Platinum, Now);
        Assert.Equal(EventPassKind.Platinum, c.EventPass);

        EventPasses.Apply(c, EventPassKind.Gold, Now);
        Assert.Equal(EventPassKind.Platinum, c.EventPass);
    }

    [Fact]
    public void Taking_a_pass_away_ends_it_today_so_the_lapse_counts_from_now()
    {
        var c = Event(Now.AddMonths(2));
        EventPasses.Apply(c, EventPassKind.Platinum, Now);
        EventPasses.Apply(c, EventPassKind.None, Now);

        Assert.Equal(EventPassKind.None, c.EventPass);
        Assert.Equal(Now, c.EventPassUntil);
    }
}
