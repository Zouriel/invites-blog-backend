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

    private static EventPlan Evaluate(
        SubscriptionTier tier = SubscriptionTier.None, DateTimeOffset? endsAt = null, DateTimeOffset? pass = null,
        long legacyBytes = 0, DateTimeOffset? legacyCover = null, DateTimeOffset? now = null, DateTimeOffset? night = null) =>
        PlanRules.Evaluate(now ?? Now, night ?? Night, tier, endsAt, pass, legacyBytes, legacyCover, null, Guid.NewGuid());

    [Fact]
    public void A_free_event_gets_500_mb_one_bucket_and_90_days()
    {
        var plan = Evaluate();

        Assert.Equal(PlanKind.Free, plan.Kind);
        Assert.Equal(500 * PlanCatalog.Mb, plan.EventBytes);
        Assert.Null(plan.AccountBytes);
        Assert.Equal(1, plan.MaxBuckets);
        Assert.Equal(1, plan.MaxWindowDays);
        Assert.Equal(Night.AddDays(90), plan.CoveredUntil);
        Assert.Equal(MediaPhase.Active, plan.Phase);
    }

    [Fact]
    public void Basic_gets_2_gb_per_event_and_20_gb_across_the_account()
    {
        var plan = Evaluate(SubscriptionTier.Basic);

        Assert.Equal(PlanKind.Basic, plan.Kind);
        Assert.Equal(2 * PlanCatalog.Gb, plan.EventBytes);
        Assert.Equal(20 * PlanCatalog.Gb, plan.AccountBytes);
        Assert.Equal(1, plan.MaxBuckets);
        Assert.Null(plan.CoveredUntil);
    }

    [Fact]
    public void Premium_gets_the_large_event_three_buckets_five_days_and_cheaper_invitations()
    {
        var plan = Evaluate(SubscriptionTier.Premium);

        Assert.Equal(PlanKind.Premium, plan.Kind);
        Assert.Equal(50 * PlanCatalog.Gb, plan.EventBytes);
        Assert.Equal(200 * PlanCatalog.Gb, plan.AccountBytes);
        Assert.Equal(MediaBucket.MaxPerCampaign, plan.MaxBuckets);
        Assert.Equal(5, plan.MaxWindowDays);
        Assert.Equal(20, plan.InviteBlockSize);
        Assert.Null(plan.CoveredUntil);
    }

    [Fact]
    public void A_pass_outranks_basic_and_covers_the_first_send()
    {
        var plan = Evaluate(SubscriptionTier.Basic, pass: Now.AddMonths(2));

        Assert.Equal(PlanKind.EventPass, plan.Kind);
        Assert.Equal(50 * PlanCatalog.Gb, plan.EventBytes);
        Assert.Null(plan.AccountBytes);
        Assert.True(plan.PassCoversFirstSend);
        Assert.Equal(10, plan.InviteBlockSize);
    }

    [Fact]
    public void An_expired_pass_counts_for_nothing_but_its_cover_date()
    {
        var ended = Night.AddDays(120);
        var plan = Evaluate(pass: ended, now: ended.AddDays(1));

        Assert.Equal(PlanKind.Free, plan.Kind);
        Assert.Equal(ended, plan.CoveredUntil);
        Assert.Equal(MediaPhase.UploadsClosed, plan.Phase);
    }

    [Fact]
    public void An_ended_subscription_covers_its_events_until_it_ended()
    {
        var ended = Night.AddDays(200);
        var plan = Evaluate(SubscriptionTier.Premium, endsAt: ended, now: ended.AddDays(40));

        Assert.Equal(PlanKind.Free, plan.Kind);
        Assert.Equal(ended, plan.CoveredUntil);
        Assert.Equal(MediaPhase.OrganiserOnly, plan.Phase);
    }

    /// <summary>
    /// The promise behind a subscription: a free event's photos, even ones already in their lapse,
    /// are kept for as long as the host stays subscribed, and the lapse only starts over when the
    /// subscription ends — with the full 90 days from then.
    /// </summary>
    [Fact]
    public void A_free_events_photos_are_kept_while_its_host_is_subscribed_and_get_the_full_lapse_after()
    {
        // Free: covered to day 90, and by day 150 only the organiser can look.
        Assert.Equal(MediaPhase.OrganiserOnly, Evaluate(now: Night.AddDays(150)).Phase);

        // Subscribes (no end date) on day 150: covered again, for as long as it lasts.
        var subscribed = Evaluate(SubscriptionTier.Basic, endsAt: null, now: Night.AddDays(900));
        Assert.Equal(PlanKind.Basic, subscribed.Kind);
        Assert.Null(subscribed.CoveredUntil);
        Assert.Equal(MediaPhase.Active, subscribed.Phase);
        Assert.True(subscribed.EventBytes >= 500 * PlanCatalog.Mb);

        // Ends on day 1000: the 90 days count from then, not from the event.
        var ended = Night.AddDays(1000);
        Assert.Equal(ended, Evaluate(SubscriptionTier.None, endsAt: ended, now: ended.AddDays(1)).CoveredUntil);
        Assert.Equal(MediaPhase.UploadsClosed, Evaluate(SubscriptionTier.None, endsAt: ended, now: ended.AddDays(1)).Phase);
        Assert.Equal(MediaPhase.OrganiserOnly, Evaluate(SubscriptionTier.None, endsAt: ended, now: ended.AddDays(89)).Phase);
        Assert.Equal(MediaPhase.Deleted, Evaluate(SubscriptionTier.None, endsAt: ended, now: ended.AddDays(90)).Phase);
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
        Assert.Equal(500 * PlanCatalog.Mb, lapsed.EventBytes);
    }

    [Fact]
    public void A_later_legacy_cover_date_wins()
    {
        var cover = Night.AddDays(300);
        Assert.Equal(cover, Evaluate(legacyCover: cover).CoveredUntil);
    }
}
