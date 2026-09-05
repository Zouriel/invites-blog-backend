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

    /// <summary>
    /// Malé's offset, and the only local day this platform has. Hard-coded rather than looked up
    /// because the Maldives has never observed daylight saving — +05:00 holds every day of the year
    /// — and because a zone database id is spelled differently on Windows and Linux, which is a way
    /// for this to fail in production and not in a test.
    /// </summary>
    public static readonly TimeSpan Male = TimeSpan.FromHours(5);

    /// <summary>
    /// Whether it is the night, for an event starting at <paramref name="eventStartAt"/>.
    ///
    /// <para><b>Why a window and not a date.</b> A calendar-date comparison expires at midnight, and a
    /// party that begins at 22:00 is two hours old by then — everything would go dark at exactly the
    /// point people are using it. So it opens at the start of the event's day and closes
    /// <see cref="ClosesAfter"/> after it begins, which covers the evening it was meant for including
    /// the part after midnight.</para>
    ///
    /// <para><b>Whose day.</b> Malé's, not UTC's. The column is normalised to UTC and the offset the
    /// inviter typed does not survive the round trip, so the day has to be reconstructed — and taking
    /// UTC's opens the window at 05:00 local, five hours into a day the guest has been living in
    /// since midnight. Someone checking their invitation the night before the party is told it is not
    /// the day yet when their own calendar says it is. <see cref="Male"/> is what everyone here means
    /// by the date.</para>
    /// </summary>
    public static bool IsOpen(DateTimeOffset eventStartAt, DateTimeOffset now)
    {
        // A date near either end of the representable range cannot be shifted into Malé's offset —
        // `DateTimeOffset`'s constructor throws rather than saturating. That is reachable with real
        // data: a bucket row whose date was never set reads as year 1, and a 500 from "is it the
        // night" is a far worse answer than "no". Anything we cannot reason about is closed.
        var limit = ClosesAfter + Male;
        if (eventStartAt < DateTimeOffset.MinValue + limit || eventStartAt > DateTimeOffset.MaxValue - limit)
            return false;

        var opens = new DateTimeOffset(eventStartAt.ToOffset(Male).Date, Male);
        var closes = eventStartAt + ClosesAfter;
        return now >= opens && now <= closes;
    }
}
