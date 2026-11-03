using InvitesBlog.Application.Plans;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>What stays when a pass event goes back to Free: up to Free's space, in a fixed order.</summary>
public class MediaTrimTests
{
    private static readonly DateTimeOffset T0 = new(2027, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private const long Mb = PlanCatalog.Mb;

    private static MediaTrim.Item Item(int minute, long mb, bool picked = false) =>
        new(Guid.NewGuid(), mb * Mb, T0.AddMinutes(minute), picked);

    [Fact]
    public void Everything_that_fits_is_kept()
    {
        var items = new[] { Item(1, 100), Item(2, 100), Item(3, 100) };
        var plan = MediaTrim.For(items, [], 300 * Mb);
        Assert.Empty(plan.Remove);
        Assert.Equal(0, plan.RemoveBytes);
        Assert.Equal(300 * Mb, plan.KeepBytes);
    }

    [Fact]
    public void The_earliest_uploads_are_kept_and_the_rest_removed()
    {
        var first = Item(1, 200);
        var second = Item(2, 90);
        var late = Item(3, 50);
        var plan = MediaTrim.For([late, second, first], [], 300 * Mb);
        Assert.Equal([late.Id], plan.Remove);
        Assert.Equal(50 * Mb, plan.RemoveBytes);
        Assert.True(plan.KeepBytes <= 300 * Mb);
    }

    [Fact]
    public void Cover_photos_come_first_then_picked_ones_then_the_earliest()
    {
        var early = Item(1, 150);
        var picked = Item(5, 100, picked: true);
        var cover = Item(9, 100);
        var plan = MediaTrim.For([early, picked, cover], [cover.Id], 250 * Mb);
        // The cover and the picked one fill 200 MB; the early one no longer fits.
        Assert.Equal([early.Id], plan.Remove);
    }

    [Fact]
    public void Something_too_big_is_skipped_and_smaller_later_ones_still_fill_the_space()
    {
        var a = Item(1, 250);
        var video = Item(2, 400);
        var b = Item(3, 40);
        var plan = MediaTrim.For([a, video, b], [], 300 * Mb);
        Assert.Equal([video.Id], plan.Remove);
        Assert.Equal(290 * Mb, plan.KeepBytes);
    }

    [Fact]
    public void Never_keeps_more_than_the_budget()
    {
        var rng = new Random(7);
        var items = Enumerable.Range(0, 500).Select(i => Item(i, rng.Next(1, 40))).ToList();
        var plan = MediaTrim.For(items, [], 300 * Mb);
        var kept = items.Where(i => !plan.Remove.Contains(i.Id)).Sum(i => i.SizeBytes);
        Assert.True(kept <= 300 * Mb);
        Assert.Equal(kept, plan.KeepBytes);
        Assert.Equal(items.Sum(i => i.SizeBytes), plan.KeepBytes + plan.RemoveBytes);
    }
}

/// <summary>"Keep your photos" is for an event whose pass has ended; a free event is kept for good already.</summary>
public class KeepPhotosRuleTests
{
    [Fact]
    public void Keeping_is_refused_for_a_free_event_and_allowed_after_a_pass()
    {
        var now = DateTimeOffset.UtcNow;
        var free = new InvitesBlog.Domain.Entities.Campaign { Id = Guid.NewGuid(), Title = "Free", EventStartAt = now.AddMonths(-2) };
        var ex = Assert.Throws<InvitesBlog.Application.Exceptions.BusinessRuleException>(() =>
            InvitesBlog.Application.Services.Billing.BillingService.EnsureEventCanHave(InvitesBlog.Domain.Enums.PaymentKind.KeepPhotos, free, now));
        Assert.Contains("kept for good", ex.Message);

        var ended = new InvitesBlog.Domain.Entities.Campaign
        {
            Id = Guid.NewGuid(), Title = "Ended", EventStartAt = now.AddMonths(-13),
            EventPass = InvitesBlog.Domain.Enums.EventPassKind.Gold, EventPassUntil = now.AddDays(-2),
        };
        InvitesBlog.Application.Services.Billing.BillingService.EnsureEventCanHave(InvitesBlog.Domain.Enums.PaymentKind.KeepPhotos, ended, now);
    }
}
