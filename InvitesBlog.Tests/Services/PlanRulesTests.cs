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
        long legacyBytes = 0, DateTimeOffset? legacyCover = null, DateTimeOffset? now = null, DateTimeOffset? night = null) =>
        PlanRules.Evaluate(
            now ?? Now, night ?? Night, pass, passUntil, keep,
            venueActive is { } active ? (VenueId, active, venueEnded) : null,
            legacyBytes, legacyCover, null, Guid.NewGuid());

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
    public void A_party_pass_gets_10_gb_two_albums_three_days_100_invitations_and_no_mark()
    {
        var until = PlanRules.PassUntil(Night, Now);
        var plan = Evaluate(EventPassKind.Party, until);

        Assert.Equal(PlanKind.PartyPass, plan.Kind);
        Assert.Equal(10 * PlanCatalog.Gb, plan.EventBytes);
        Assert.Equal(2, plan.MaxBuckets);
        Assert.Equal(3, plan.MaxWindowDays);
        Assert.Equal(100, plan.IncludedInvites);
        Assert.False(plan.PrivateAlbums);
        Assert.False(plan.Branded);
        Assert.Equal(until, plan.CoveredUntil);
    }

    [Fact]
    public void A_wedding_pass_gets_100_gb_five_albums_five_days_500_invitations_and_private_albums()
    {
        var plan = Evaluate(EventPassKind.Wedding, Now.AddMonths(6));

        Assert.Equal(PlanKind.WeddingPass, plan.Kind);
        Assert.Equal(100 * PlanCatalog.Gb, plan.EventBytes);
        Assert.Equal(MediaBucket.MaxPerCampaign, plan.MaxBuckets);
        Assert.Equal(5, plan.MaxWindowDays);
        Assert.Equal(500, plan.IncludedInvites);
        Assert.True(plan.PrivateAlbums);
        Assert.False(plan.Branded);
    }

    [Fact]
    public void A_pass_runs_a_year_from_the_event_or_from_today_once_it_has_passed()
    {
        Assert.Equal(Night.AddMonths(12), PlanRules.PassUntil(Night, Night.AddDays(-30)));
        Assert.Equal(Now.AddMonths(12), PlanRules.PassUntil(Night, Now));
    }

    [Fact]
    public void A_venue_outranks_a_pass_and_covers_its_events_while_it_runs()
    {
        var plan = Evaluate(EventPassKind.Party, Now.AddMonths(2), venueActive: true);

        Assert.Equal(PlanKind.Venue, plan.Kind);
        Assert.Equal(100 * PlanCatalog.Gb, plan.EventBytes);
        Assert.Equal(PlanCatalog.VenueAccountBytes, plan.AccountBytes);
        Assert.Equal(VenueId, plan.VenueId);
        Assert.True(plan.PrivateAlbums);
        Assert.False(plan.Branded);
        Assert.Null(plan.CoveredUntil);
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
    public void Buckets_from_before_the_plans_keep_their_space_for_six_months_after_the_event()
    {
        var kept = Evaluate(legacyBytes: 20 * PlanCatalog.Gb, now: Night.AddMonths(5));
        var lapsed = Evaluate(legacyBytes: 20 * PlanCatalog.Gb, now: Night.AddMonths(7));

        Assert.Equal(20 * PlanCatalog.Gb, kept.EventBytes);
        Assert.Equal(1 * PlanCatalog.Gb, lapsed.EventBytes);
    }

    [Fact]
    public void A_later_legacy_cover_date_wins()
    {
        var cover = Night.AddDays(300);
        Assert.Equal(cover, Evaluate(legacyCover: cover).CoveredUntil);
    }

    [Fact]
    public void Studio_passes_cost_30_percent_less()
    {
        Assert.Equal(139m, PlanCatalog.StudioPassPrice(EventPassKind.Party));
        Assert.Equal(489m, PlanCatalog.StudioPassPrice(EventPassKind.Wedding));
    }

    [Fact]
    public void The_catalogue_lists_every_plan_in_rufiyaa()
    {
        var catalog = PlanCatalog.Describe();

        Assert.Equal("MVR", catalog.Currency);
        Assert.Equal(["Free", "PartyPass", "WeddingPass", "Studio", "Venue"], catalog.Plans.Select(p => p.Kind));
        Assert.Equal([0m, 199m, 699m, 450m, 2300m], catalog.Plans.Select(p => p.Price));
        Assert.Equal(150m, catalog.KeepPhotos.Price);
        Assert.Equal(50m, catalog.Sending.PerBlock);
        Assert.Equal(100, catalog.Sending.BlockSize);
    }
}

/// <summary>Putting a pass on an event: the one rule the admin, a checkout and a Studio share.</summary>
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
