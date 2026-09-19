namespace InvitesBlog.Application.Events;

/// <summary>
/// When an event's night is open for shooting — the single definition of "it is the day".
///
/// <para><b>Why this is one place.</b> Three surfaces ask the same question: whether to offer a guest
/// the camera on their invitation, whether a media bucket will accept anything, and whether the
/// contributor page a QR code opens shows an upload button at all. Answered separately they drift,
/// and the way that shows up is a camera on an invitation that leads to a bucket refusing every
/// photo taken with it.</para>
/// </summary>
public static class EventDayWindow
{
    /// <summary>
    /// How long after the event begins the night stays open.
    ///
    /// <para>A full day, so that a party beginning in the evening is still open the following
    /// morning: the photographs somebody meant to add on the way home, or over breakfast, are part of
    /// the same night to everyone except a clock.</para>
    /// </summary>
    public static readonly TimeSpan ClosesAfter = TimeSpan.FromHours(24);

    /// <summary>Whole days before the event's own day that the window opens.</summary>
    public const int DaysBefore = 1;

    /// <summary>Whole days after the event's own day that the window stays open, to midnight.</summary>
    public const int DaysAfter = 1;

    /// <summary>
    /// The most days a bucket may stay open for, however generous the plan behind it gets.
    ///
    /// <para>A ceiling on this side rather than only in the pricing, because what this number really
    /// controls is how long a printed QR code on a table keeps working. A typo in configuration
    /// should not turn an evening's bucket into a standing invitation.</para>
    /// </summary>
    public const int MaxWindowDays = 5;

    /// <summary>
    /// Malé's offset, and the only local day this platform has. Hard-coded rather than looked up
    /// because the Maldives has never observed daylight saving — +05:00 holds every day of the year
    /// — and because a zone database id is spelled differently on Windows and Linux, which is a way
    /// for this to fail in production and not in a test.
    /// </summary>
    public static readonly TimeSpan Male = TimeSpan.FromHours(5);

    /// <summary>
    /// Whether the event's window is open, for an event starting at <paramref name="eventStartAt"/>.
    ///
    /// <para><b>Three days, not one evening.</b> It opens at midnight at the start of the day BEFORE
    /// the event and closes when the day AFTER it ends. Guests take photos at the mehendi the night
    /// before, at the event itself, and over breakfast the morning after, and to them that is all the
    /// same wedding. The camera on the invitation and the bucket it posts to share this answer, so a
    /// camera is never offered that leads to a bucket refusing the photo.</para>
    ///
    /// <para><b>Whose day.</b> Malé's, not UTC's. The column is normalised to UTC and the offset the
    /// inviter typed does not survive the round trip, so the day has to be reconstructed. Taking UTC's
    /// would move every boundary to 05:00 local. <see cref="Male"/> is what everyone here means by
    /// the date.</para>
    /// </summary>
    /// <param name="windowDays">
    /// For an album on a pass, how many days it stays open counted from the moment the event
    /// begins. It can only make the window longer: it closes at whichever is later, the end of the
    /// day after or the start plus this many days. Clamped to 1..<see cref="MaxWindowDays"/>.
    /// </param>
    public static bool IsOpen(DateTimeOffset eventStartAt, DateTimeOffset now, int windowDays = 1)
    {
        // A date near either end of the representable range cannot be shifted into Malé's offset:
        // `DateTimeOffset` throws rather than saturating. That is reachable with real data (a bucket
        // row whose date was never set reads as year 1), and "closed" is a far better answer than a 500.
        var guard = TimeSpan.FromDays(MaxWindowDays + DaysBefore + DaysAfter + 1);
        if (eventStartAt < DateTimeOffset.MinValue + guard || eventStartAt > DateTimeOffset.MaxValue - guard)
            return false;

        var days = Math.Clamp(windowDays, 1, MaxWindowDays);
        var eventDay = new DateTimeOffset(eventStartAt.ToOffset(Male).Date, Male);

        var opens = eventDay.AddDays(-DaysBefore);
        var dayAfterEnds = eventDay.AddDays(DaysAfter + 1);
        var longWindowEnds = eventStartAt + ClosesAfter * days;
        var closes = dayAfterEnds > longWindowEnds ? dayAfterEnds : longWindowEnds;

        return now >= opens && now < closes;
    }
}
