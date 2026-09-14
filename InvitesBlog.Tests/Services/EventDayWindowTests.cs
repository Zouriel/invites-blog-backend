using InvitesBlog.Application.Events;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>
/// The one definition of "the event is on". Three surfaces depend on it (whether a guest is offered
/// the camera, whether a bucket accepts anything, and whether a QR page shows an upload control), and
/// they only agree because they ask the same function.
/// </summary>
public class EventDayWindowTests
{
    /// <summary>A party at 20:00 Malé on 28 August 2026, which is 15:00 UTC.</summary>
    private static readonly DateTimeOffset Start = new(2026, 8, 28, 15, 0, 0, TimeSpan.Zero);

    /// <summary>Midnight in Malé at the start of 27 August, the day before: 19:00 UTC on the 26th.</summary>
    private static readonly DateTimeOffset DayBeforeBegins = new(2026, 8, 26, 19, 0, 0, TimeSpan.Zero);

    /// <summary>Midnight in Malé at the end of 29 August, the day after: 19:00 UTC on the 29th.</summary>
    private static readonly DateTimeOffset DayAfterEnds = new(2026, 8, 29, 19, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Opens_at_midnight_in_Male_on_the_day_before()
    {
        Assert.True(EventDayWindow.IsOpen(Start, DayBeforeBegins));
        Assert.False(EventDayWindow.IsOpen(Start, DayBeforeBegins.AddSeconds(-1)));
    }

    [Fact]
    public void Stays_open_through_the_event_day()
    {
        Assert.True(EventDayWindow.IsOpen(Start, Start.AddHours(-10)));
        Assert.True(EventDayWindow.IsOpen(Start, Start));
        Assert.True(EventDayWindow.IsOpen(Start, Start.AddHours(3)));
    }

    [Fact]
    public void Closes_when_the_day_after_ends_in_Male()
    {
        Assert.True(EventDayWindow.IsOpen(Start, DayAfterEnds.AddSeconds(-1)));
        Assert.False(EventDayWindow.IsOpen(Start, DayAfterEnds));
    }

    /// <summary>A party starting just before midnight still gets the whole of the following day.</summary>
    [Fact]
    public void A_late_party_is_still_open_all_the_next_day()
    {
        var lateStart = new DateTimeOffset(2026, 8, 28, 18, 30, 0, TimeSpan.Zero); // 23:30 Malé
        Assert.True(EventDayWindow.IsOpen(lateStart, DayAfterEnds.AddMinutes(-1)));
        Assert.False(EventDayWindow.IsOpen(lateStart, DayAfterEnds));
    }

    [Fact]
    public void Shut_long_before_and_long_after()
    {
        Assert.False(EventDayWindow.IsOpen(Start, Start.AddDays(-3)));
        Assert.False(EventDayWindow.IsOpen(Start, Start.AddDays(7)));
    }

    /// <summary>
    /// A date that cannot be shifted into Malé's offset must read as closed rather than throw. A bucket
    /// row whose date was never set reads as year 1.
    /// </summary>
    [Theory]
    [MemberData(nameof(Unreasonable))]
    public void An_unrepresentable_date_is_closed_rather_than_a_crash(DateTimeOffset eventStartAt) =>
        Assert.False(EventDayWindow.IsOpen(eventStartAt, DateTimeOffset.UtcNow));

    public static TheoryData<DateTimeOffset> Unreasonable() =>
        [default, DateTimeOffset.MinValue, DateTimeOffset.MaxValue];

    // ---------- the longer window a subscriber's bucket may carry ----------

    /// <summary>Counted from when the party begins, and only ever longer than the three days.</summary>
    [Fact]
    public void A_five_day_bucket_stays_open_five_days_past_the_start()
    {
        Assert.True(EventDayWindow.IsOpen(Start, Start.AddDays(5).AddSeconds(-1), 5));
        Assert.False(EventDayWindow.IsOpen(Start, Start.AddDays(5), 5));
    }

    [Fact]
    public void A_longer_window_does_not_open_it_any_earlier()
    {
        Assert.False(EventDayWindow.IsOpen(Start, DayBeforeBegins.AddSeconds(-1), 5));
        Assert.True(EventDayWindow.IsOpen(Start, DayBeforeBegins, 5));
    }

    /// <summary>One is the default, and a zero or negative stored by accident is clamped up to it.</summary>
    [Fact]
    public void One_day_is_the_default_and_the_floor()
    {
        Assert.True(EventDayWindow.IsOpen(Start, DayAfterEnds.AddSeconds(-1)));
        Assert.True(EventDayWindow.IsOpen(Start, DayAfterEnds.AddSeconds(-1), 0));
        Assert.True(EventDayWindow.IsOpen(Start, DayAfterEnds.AddSeconds(-1), -3));
        Assert.False(EventDayWindow.IsOpen(Start, DayAfterEnds, 0));
    }

    /// <summary>A printed QR code works exactly as long as this says, so configuration is capped.</summary>
    [Fact]
    public void However_large_the_number_it_stops_at_the_ceiling()
    {
        Assert.True(EventDayWindow.IsOpen(Start, Start.AddDays(EventDayWindow.MaxWindowDays).AddSeconds(-1), 9999));
        Assert.False(EventDayWindow.IsOpen(Start, Start.AddDays(EventDayWindow.MaxWindowDays), 9999));
    }

    [Fact]
    public void An_unrepresentable_date_is_closed_at_any_window()
    {
        Assert.False(EventDayWindow.IsOpen(DateTimeOffset.MinValue, DateTimeOffset.UtcNow, 5));
        Assert.False(EventDayWindow.IsOpen(DateTimeOffset.MaxValue, DateTimeOffset.UtcNow, 5));
    }
}
