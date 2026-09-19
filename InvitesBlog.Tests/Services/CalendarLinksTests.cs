using System.Text;
using InvitesBlog.Application.Events;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>
/// "Add to calendar": the day a save the date names must land on the same day in every calendar,
/// read by Malé's clock, and the .ics must be something Apple Calendar and Outlook will open.
/// </summary>
public class CalendarLinksTests
{
    private static readonly TimeSpan Male = TimeSpan.FromHours(5);

    // 14 March 2027 in Malé; 01:00 local is still 13 March in UTC, the case a UTC date gets wrong.
    private static CalendarEntry AllDay(string? location = "Maafushi") => new(
        "Aisha & Omar, save the date", new DateTimeOffset(2027, 3, 14, 1, 0, 0, Male), null, true,
        location, "Invitation to follow.", "std-1@invites.blog", "https://me.invites.blog/r/abc");

    private static CalendarEntry Timed() => new(
        "Aisha & Omar", new DateTimeOffset(2027, 3, 14, 19, 30, 0, Male), null, false,
        null, null, "std-2@invites.blog", null);

    [Fact]
    public void Google_all_day_uses_the_Male_date_with_an_exclusive_end()
    {
        var url = CalendarLinks.Google(AllDay());
        Assert.StartsWith("https://calendar.google.com/calendar/render?action=TEMPLATE", url);
        Assert.Contains("&dates=20270314/20270315", url);
        Assert.Contains("&text=Aisha%20%26%20Omar%2C%20save%20the%20date", url);
        Assert.Contains("&location=Maafushi", url);
    }

    [Fact]
    public void Google_timed_is_in_UTC_and_lasts_three_hours_without_an_end()
    {
        Assert.Contains("&dates=20270314T143000Z/20270314T173000Z", CalendarLinks.Google(Timed()));
    }

    [Fact]
    public void Outlook_and_Microsoft_365_prefill_the_compose_form()
    {
        var live = CalendarLinks.Outlook(AllDay());
        var work = CalendarLinks.Office365(AllDay());
        Assert.StartsWith("https://outlook.live.com/calendar/0/action/compose?", live);
        Assert.StartsWith("https://outlook.office.com/calendar/0/action/compose?", work);
        Assert.Contains("&startdt=2027-03-14&enddt=2027-03-15&allday=true", live);
        Assert.Contains("&startdt=2027-03-14T14%3A30%3A00Z", CalendarLinks.Outlook(Timed()));
    }

    [Fact]
    public void Ics_is_a_published_all_day_event_with_escaped_text()
    {
        var ics = CalendarLinks.Ics(AllDay("Sun Island, Ari Atoll"), new DateTimeOffset(2026, 9, 19, 0, 0, 0, TimeSpan.Zero));
        Assert.Contains("METHOD:PUBLISH\r\n", ics);
        Assert.Contains("DTSTART;VALUE=DATE:20270314\r\n", ics);
        Assert.Contains("DTEND;VALUE=DATE:20270315\r\n", ics);
        Assert.Contains("LOCATION:Sun Island\\, Ari Atoll\r\n", ics);
        Assert.Contains("DESCRIPTION:Invitation to follow.\\n\\nhttps://me.invites.blog/r/abc\r\n", ics);
        Assert.Contains("UID:std-1@invites.blog\r\n", ics);
        Assert.EndsWith("END:VCALENDAR\r\n", ics);
    }

    [Fact]
    public void Long_ics_lines_are_folded_at_75_octets()
    {
        var entry = AllDay() with { Details = new string('é', 120) };
        var ics = CalendarLinks.Ics(entry);
        foreach (var line in ics.Split("\r\n"))
            Assert.True(Encoding.UTF8.GetByteCount(line) <= 75, line);
        // Unfolding gives back the text.
        Assert.Contains(new string('é', 120), ics.Replace("\r\n ", ""));
    }
}
