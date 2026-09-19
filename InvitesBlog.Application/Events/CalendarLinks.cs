using System.Globalization;
using System.Text;

namespace InvitesBlog.Application.Events;

/// <summary>
/// One entry for a guest's calendar. <see cref="AllDay"/> when the host knows the day but not the
/// time — the usual case for a save the date — and then only the Malé date of <see cref="Start"/>
/// matters.
/// </summary>
/// <param name="Uid">Stable per event, so saving the same date twice updates rather than duplicates.</param>
public sealed record CalendarEntry(
    string Title,
    DateTimeOffset Start,
    DateTimeOffset? End,
    bool AllDay,
    string? Location,
    string? Details,
    string Uid,
    string? Url);

/// <summary>
/// "Add to calendar" for the calendars people here actually use: a pre-filled link for Google
/// Calendar, Outlook.com and Microsoft 365, and an .ics file for Apple Calendar and everything else.
/// Pure and deterministic; the tests pin every format.
/// </summary>
public static class CalendarLinks
{
    /// <summary>How long a timed entry lasts when the host gave no end.</summary>
    public static readonly TimeSpan DefaultLength = TimeSpan.FromHours(3);

    public static string Google(CalendarEntry e)
    {
        var dates = e.AllDay
            ? $"{Day(e.Start):yyyyMMdd}/{Day(e.Start).AddDays(1):yyyyMMdd}"
            : $"{Utc(e.Start)}/{Utc(EndOf(e))}";
        return "https://calendar.google.com/calendar/render?action=TEMPLATE"
               + Q("text", e.Title) + "&dates=" + dates
               + Q("details", Body(e)) + Q("location", e.Location);
    }

    /// <summary>Outlook.com, Hotmail and Live accounts.</summary>
    public static string Outlook(CalendarEntry e) => OutlookCompose("https://outlook.live.com", e);

    /// <summary>Work and school accounts on Microsoft 365.</summary>
    public static string Office365(CalendarEntry e) => OutlookCompose("https://outlook.office.com", e);

    private static string OutlookCompose(string host, CalendarEntry e)
    {
        var (start, end) = e.AllDay
            ? (Day(e.Start).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
               Day(e.Start).AddDays(1).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture))
            : (e.Start.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
               EndOf(e).ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture));
        return $"{host}/calendar/0/action/compose?path=%2Fcalendar%2Faction%2Fcompose&rru=addevent"
               + Q("subject", e.Title) + Q("startdt", start) + Q("enddt", end)
               + (e.AllDay ? "&allday=true" : "")
               + Q("body", Body(e)) + Q("location", e.Location);
    }

    /// <summary>
    /// An iCalendar file. METHOD:PUBLISH, so a mail app offers "Add to calendar" rather than
    /// treating it as a meeting request with Accept/Decline — a save the date isn't asking anything.
    /// </summary>
    public static string Ics(CalendarEntry e, DateTimeOffset? stamp = null)
    {
        var lines = new List<string>
        {
            "BEGIN:VCALENDAR",
            "VERSION:2.0",
            "PRODID:-//invites.blog//Save the date//EN",
            "CALSCALE:GREGORIAN",
            "METHOD:PUBLISH",
            "BEGIN:VEVENT",
            $"UID:{Escape(e.Uid)}",
            $"DTSTAMP:{Utc(stamp ?? DateTimeOffset.UtcNow)}",
        };
        if (e.AllDay)
        {
            lines.Add($"DTSTART;VALUE=DATE:{Day(e.Start):yyyyMMdd}");
            lines.Add($"DTEND;VALUE=DATE:{Day(e.Start).AddDays(1):yyyyMMdd}");
        }
        else
        {
            lines.Add($"DTSTART:{Utc(e.Start)}");
            lines.Add($"DTEND:{Utc(EndOf(e))}");
        }
        lines.Add($"SUMMARY:{Escape(e.Title)}");
        if (!string.IsNullOrWhiteSpace(e.Location)) lines.Add($"LOCATION:{Escape(e.Location!)}");
        if (!string.IsNullOrWhiteSpace(Body(e))) lines.Add($"DESCRIPTION:{Escape(Body(e)!)}");
        if (!string.IsNullOrWhiteSpace(e.Url)) lines.Add($"URL:{e.Url}");
        lines.Add("TRANSP:TRANSPARENT");
        lines.Add("END:VEVENT");
        lines.Add("END:VCALENDAR");

        var sb = new StringBuilder();
        foreach (var line in lines) sb.Append(Fold(line)).Append("\r\n");
        return sb.ToString();
    }

    /// <summary>The event's day as Malé counts it — the only local day this platform has.</summary>
    private static DateTime Day(DateTimeOffset at) => at.ToOffset(EventDayWindow.Male).Date;

    private static DateTimeOffset EndOf(CalendarEntry e) =>
        e.End is { } end && end > e.Start ? end : e.Start + DefaultLength;

    private static string Utc(DateTimeOffset at) =>
        at.ToUniversalTime().ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);

    /// <summary>The details plus the link back, so the calendar entry leads to the design.</summary>
    private static string? Body(CalendarEntry e)
    {
        var parts = new[] { e.Details?.Trim(), e.Url }.Where(p => !string.IsNullOrWhiteSpace(p));
        var body = string.Join("\n\n", parts);
        return body.Length == 0 ? null : body;
    }

    private static string Q(string name, string? value) =>
        string.IsNullOrWhiteSpace(value) ? "" : $"&{name}={Uri.EscapeDataString(value)}";

    /// <summary>RFC 5545 §3.3.11: backslash, semicolon, comma and newline are escaped in text.</summary>
    private static string Escape(string text) => text
        .Replace("\\", "\\\\").Replace(";", "\\;").Replace(",", "\\,")
        .Replace("\r\n", "\\n").Replace("\n", "\\n").Replace("\r", "\\n");

    /// <summary>RFC 5545 §3.1: lines longer than 75 octets continue on the next, indented by a space.</summary>
    private static string Fold(string line)
    {
        var bytes = Encoding.UTF8.GetBytes(line);
        if (bytes.Length <= 75) return line;
        var sb = new StringBuilder();
        var chunk = new StringBuilder();
        var size = 0;
        var limit = 75;
        foreach (var rune in line.EnumerateRunes())
        {
            var n = rune.Utf8SequenceLength;
            if (size + n > limit)
            {
                sb.Append(chunk).Append("\r\n ");
                chunk.Clear();
                size = 0;
                limit = 74; // the leading space counts
            }
            chunk.Append(rune.ToString());
            size += n;
        }
        return sb.Append(chunk).ToString();
    }
}
