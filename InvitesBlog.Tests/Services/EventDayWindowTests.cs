using InvitesBlog.Application.Events;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>
/// The one definition of "it is the night". Worth its own tests because three surfaces now depend on
/// it — whether a guest is offered the camera, whether a bucket accepts anything, and whether the
/// page a QR opens shows an upload control at all — and they are only consistent because they ask
/// the same function.
/// </summary>
public class EventDayWindowTests
{
    /// <summary>A party at 20:00 Malé on 28 August 2026 — 15:00 UTC.</summary>
    private static readonly DateTimeOffset Start = new(2026, 8, 28, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Opens_at_the_start_of_the_day_in_Male_not_in_UTC()
    {
        // Midnight in Malé is 19:00 UTC the day before. Taking UTC's day would open it at 05:00 local,
        // five hours into a day the guest has been living in since midnight.
        Assert.True(EventDayWindow.IsOpen(Start, new DateTimeOffset(2026, 8, 27, 19, 0, 0, TimeSpan.Zero)));
        Assert.False(EventDayWindow.IsOpen(Start, new DateTimeOffset(2026, 8, 27, 18, 59, 59, TimeSpan.Zero)));
    }

    /// <summary>
    /// A full day after it begins, so the morning after belongs to the same night — the photographs
    /// somebody adds on the way home or over breakfast are not late.
    /// </summary>
    [Fact]
    public void Closes_a_full_day_after_it_begins()
    {
        Assert.True(EventDayWindow.IsOpen(Start, Start.AddHours(13)));
        Assert.True(EventDayWindow.IsOpen(Start, Start.AddHours(24)));
        Assert.False(EventDayWindow.IsOpen(Start, Start.AddHours(24).AddSeconds(1)));
    }

    [Fact]
    public void Shut_before_the_day_and_long_after_it()
    {
        Assert.False(EventDayWindow.IsOpen(Start, Start.AddDays(-2)));
        Assert.False(EventDayWindow.IsOpen(Start, Start.AddDays(7)));
    }

    /// <summary>
    /// A date that cannot be shifted into Malé's offset must read as closed rather than throw. This
    /// is reachable with real data — a bucket row whose date was never set reads as year 1 — and a
    /// 500 from "is it the night" would take the whole page down instead of saying no.
    /// </summary>
    [Theory]
    [MemberData(nameof(Unreasonable))]
    public void An_unrepresentable_date_is_closed_rather_than_a_crash(DateTimeOffset eventStartAt) =>
        Assert.False(EventDayWindow.IsOpen(eventStartAt, DateTimeOffset.UtcNow));

    public static TheoryData<DateTimeOffset> Unreasonable() =>
        [default, DateTimeOffset.MinValue, DateTimeOffset.MaxValue];
}
