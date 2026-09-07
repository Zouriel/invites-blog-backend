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

    // ---------- the longer window a subscriber's bucket may carry ----------

    /// <summary>
    /// The extra days are counted from when the party BEGINS, not from when the window opens. Adding
    /// them to the opening instead would quietly shorten every long bucket by the hours between
    /// midnight and the event.
    /// </summary>
    [Fact]
    public void A_five_day_bucket_stays_open_five_days_past_the_start()
    {
        Assert.True(EventDayWindow.IsOpen(Start, Start.AddDays(4), 5));
        Assert.True(EventDayWindow.IsOpen(Start, Start.AddDays(5), 5));
        Assert.False(EventDayWindow.IsOpen(Start, Start.AddDays(5).AddSeconds(1), 5));
    }

    /// <summary>A longer window still cannot open a bucket before the day it belongs to.</summary>
    [Fact]
    public void A_longer_window_does_not_open_it_any_earlier()
    {
        Assert.False(EventDayWindow.IsOpen(Start, new DateTimeOffset(2026, 8, 27, 18, 59, 59, TimeSpan.Zero), 5));
        Assert.True(EventDayWindow.IsOpen(Start, new DateTimeOffset(2026, 8, 27, 19, 0, 0, TimeSpan.Zero), 5));
    }

    /// <summary>
    /// One day is what everyone gets, and it has to be what an unspecified window means — every
    /// caller that existed before subscribers did passes nothing.
    /// </summary>
    [Fact]
    public void One_day_is_the_default_and_the_floor()
    {
        Assert.False(EventDayWindow.IsOpen(Start, Start.AddHours(25)));
        // A zero or a negative stored by accident closes a bucket that should be open. Clamped up.
        Assert.True(EventDayWindow.IsOpen(Start, Start.AddHours(13), 0));
        Assert.True(EventDayWindow.IsOpen(Start, Start.AddHours(13), -3));
        Assert.False(EventDayWindow.IsOpen(Start, Start.AddHours(25), 0));
    }

    /// <summary>
    /// A printed QR code keeps working for exactly as long as this says, so a number that arrives
    /// from configuration is capped rather than trusted.
    /// </summary>
    [Fact]
    public void However_large_the_number_it_stops_at_the_ceiling()
    {
        Assert.True(EventDayWindow.IsOpen(Start, Start.AddDays(EventDayWindow.MaxWindowDays), 9999));
        Assert.False(EventDayWindow.IsOpen(
            Start, Start.AddDays(EventDayWindow.MaxWindowDays).AddSeconds(1), 9999));
    }

    /// <summary>
    /// The unrepresentable-date guard has to survive the window being widened — the range it checks
    /// scales with it, and a bucket whose date was never set reads as year 1.
    /// </summary>
    [Fact]
    public void An_unrepresentable_date_is_closed_at_any_window()
    {
        Assert.False(EventDayWindow.IsOpen(DateTimeOffset.MinValue, DateTimeOffset.UtcNow, 5));
        Assert.False(EventDayWindow.IsOpen(DateTimeOffset.MaxValue, DateTimeOffset.UtcNow, 5));
    }
}
