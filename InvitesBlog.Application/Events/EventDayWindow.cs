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
    /// Malé's offset: the clock of every event made before events had a time zone of their own
    /// (Indian/Maldives, which has never observed daylight saving). Only a legacy default now; the
    /// window is counted on the event's own zone (<c>Campaign.TimeZone</c>).
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
    /// <para><b>Whose day.</b> The event's, in its own time zone (<paramref name="timeZone"/>), not
    /// UTC's. The column is normalised to UTC and the offset the inviter typed does not survive the
    /// round trip, so the day has to be reconstructed on the event's clock; taking UTC's would move
    /// every boundary. Null is the legacy Maldives zone.</para>
    /// </summary>
    /// <param name="windowDays">
    /// For an album on a pass, how many days it stays open counted from the moment the event
    /// begins. It can only make the window longer: it closes at whichever is later, the end of the
    /// day after or the start plus this many days. Clamped to 1..<see cref="MaxWindowDays"/>.
    /// </param>
    /// <param name="timeZone">The event's IANA zone; null or unknown is Indian/Maldives.</param>
    public static bool IsOpen(DateTimeOffset eventStartAt, DateTimeOffset now, int windowDays = 1, string? timeZone = null)
    {
        // A date near either end of the representable range cannot be shifted into Malé's offset:
        // `DateTimeOffset` throws rather than saturating. That is reachable with real data (a bucket
        // row whose date was never set reads as year 1), and "closed" is a far better answer than a 500.
        var guard = TimeSpan.FromDays(MaxWindowDays + DaysBefore + DaysAfter + 1);
        if (eventStartAt < DateTimeOffset.MinValue + guard || eventStartAt > DateTimeOffset.MaxValue - guard)
            return false;

        var (opens, closes) = Bounds(eventStartAt, windowDays, timeZone);
        return now >= opens && now < closes;
    }

    /// <summary>
    /// When the window ENDS for good, or null for a date too near the ends of the calendar to have one
    /// (a row whose date was never set reads as year 1; see <see cref="IsOpen"/>).
    ///
    /// <para>Only the far side counts here. Before the window opens nothing is closed: hosts print the
    /// QR cards and send the links in the weeks before the event, and that is exactly when they need them.</para>
    /// </summary>
    public static DateTimeOffset? ClosesAt(DateTimeOffset eventStartAt, int windowDays = 1, string? timeZone = null)
    {
        var guard = TimeSpan.FromDays(MaxWindowDays + DaysBefore + DaysAfter + 1);
        if (eventStartAt < DateTimeOffset.MinValue + guard || eventStartAt > DateTimeOffset.MaxValue - guard)
            return null;
        return Bounds(eventStartAt, windowDays, timeZone).Closes;
    }

    /// <summary>
    /// Whether the window has ended: no new QR codes or links for adding photos (or for the invitation
    /// itself) are made after this. Printed codes and sent links keep opening their closed, view-only page.
    /// Lengthening the album's window or moving the date makes this false again.
    /// </summary>
    public static bool HasClosed(DateTimeOffset eventStartAt, DateTimeOffset now, int windowDays = 1, string? timeZone = null) =>
        ClosesAt(eventStartAt, windowDays, timeZone) is { } closes && now >= closes;

    /// <summary>The error code every refusal after the window carries (HTTP 409).</summary>
    public const string ClosedCode = "window_closed";

    /// <summary>
    /// The refusal for making a new code or link once the window has ended, naming the last day it was open
    /// on the event's own clock.
    /// </summary>
    public static InvitesBlog.Application.Exceptions.CodedRuleException Closed(DateTimeOffset closesAt, string? timeZone) =>
        new($"The photo window closed on {EventTime.DateText(closesAt.AddSeconds(-1), timeZone)}. "
            + "Guests can still view the album, but new QR codes and links can't be made.",
            ClosedCode, System.Net.HttpStatusCode.Conflict);

    /// <summary>
    /// When the window opens and closes, for pages that say so. The same arithmetic as
    /// <see cref="IsOpen"/>; callers pass a real event date (IsOpen guards the extremes).
    /// </summary>
    public static (DateTimeOffset Opens, DateTimeOffset Closes) Bounds(DateTimeOffset eventStartAt, int windowDays = 1, string? timeZone = null)
    {
        var days = Math.Clamp(windowDays, 1, MaxWindowDays);
        // Midnights on the event's own clock. Each boundary is found separately rather than by adding
        // 24 hours, so a daylight saving change inside the window moves it by the right amount.
        var eventDay = EventTime.Day(eventStartAt, timeZone);

        var opens = EventTime.StartOfDay(eventDay.AddDays(-DaysBefore), timeZone);
        var dayAfterEnds = EventTime.StartOfDay(eventDay.AddDays(DaysAfter + 1), timeZone);
        var longWindowEnds = eventStartAt + ClosesAfter * days;
        return (opens, dayAfterEnds > longWindowEnds ? dayAfterEnds : longWindowEnds);
    }
}
