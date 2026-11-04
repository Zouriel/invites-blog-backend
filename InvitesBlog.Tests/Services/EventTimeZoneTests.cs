using System.Text.Json.Nodes;
using InvitesBlog.Application.Events;
using InvitesBlog.Application.Rules;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using InvitesBlog.Infrastructure.Rendering;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>
/// Event times are instants shown on the event's own clock. A London host's 7 pm reads "7:00 pm GMT"
/// to a guest in New York; a Maldives event reads "+05" to someone in Sydney; and the days an event's
/// windows count are the event's days, across a daylight saving change.
/// </summary>
public class EventTimeZoneTests
{
    private const string London = "Europe/London";
    private const string NewYork = "America/New_York";
    private const string Male = "Indian/Maldives";
    private const string Sydney = "Australia/Sydney";

    // A London wedding at 7 pm on a winter Saturday: 19:00 GMT.
    private static readonly DateTimeOffset LondonWinter = new(2027, 1, 16, 19, 0, 0, TimeSpan.Zero);
    // The same clock time in summer is 18:00 UTC (BST, +01).
    private static readonly DateTimeOffset LondonSummer = new(2027, 7, 17, 18, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_london_hosts_time_reads_the_same_to_a_guest_in_new_york()
    {
        // The server shows the event's clock whoever is looking; there is no viewer's zone in it.
        Assert.Equal("7:00 pm GMT", EventTime.TimeText(LondonWinter, London));
        Assert.Equal("Saturday, 16 January 2027", EventTime.DateText(LondonWinter, London));
        Assert.Equal("7:00 pm +01", EventTime.TimeText(LondonSummer, London));
        // What a client in New York adds as "(2:00 pm your time)": the same instant on its own clock.
        Assert.Equal(new DateTimeOffset(2027, 1, 16, 14, 0, 0, TimeSpan.FromHours(-5)), EventTime.Local(LondonWinter, NewYork));
    }

    [Fact]
    public void A_maldives_event_viewed_from_sydney_keeps_its_own_day_and_time()
    {
        // 23:30 in Malé on 14 March is already 15 March in Sydney.
        var start = new DateTimeOffset(2027, 3, 14, 23, 30, 0, TimeSpan.FromHours(5));
        Assert.Equal("11:30 pm +05", EventTime.TimeText(start, Male));
        Assert.Equal("Sunday, 14 March 2027", EventTime.DateText(start, Male));
        Assert.Equal(new DateTime(2027, 3, 15), EventTime.Day(start, Sydney));
        Assert.Equal("+11", EventTime.Label(start, Sydney));
        Assert.Equal("5:30 am +11", EventTime.TimeText(start, Sydney));
    }

    [Fact]
    public void Half_hour_and_negative_offsets_are_labelled_plainly()
    {
        Assert.Equal("+05:30", EventTime.OffsetLabel(TimeSpan.FromMinutes(330)));
        Assert.Equal("-04", EventTime.OffsetLabel(TimeSpan.FromHours(-4)));
        Assert.Equal("GMT", EventTime.OffsetLabel(TimeSpan.Zero));
    }

    [Theory]
    [InlineData(London, true)]
    [InlineData("UTC", true)]
    [InlineData("America/Argentina/Buenos_Aires", true)]
    [InlineData("GMT Standard Time", false)]   // a Windows id: refused even though .NET could map it
    [InlineData("Mars/Olympus_Mons", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Only_iana_zones_are_accepted(string? id, bool valid) => Assert.Equal(valid, EventTime.IsValidZone(id));

    [Fact]
    public void An_unknown_or_missing_zone_is_the_legacy_maldives_clock()
    {
        var at = new DateTimeOffset(2027, 1, 1, 14, 0, 0, TimeSpan.Zero);
        Assert.Equal("7:00 pm +05", EventTime.TimeText(at, null));
        Assert.Equal("7:00 pm +05", EventTime.TimeText(at, "Nowhere/Land"));
        Assert.Equal(Male, EventTime.ZoneOrDefault(null, "also not a zone"));
        Assert.Equal(NewYork, EventTime.ZoneOrDefault(null, NewYork));
        Assert.Equal(London, EventTime.ZoneOrDefault(London, NewYork));
    }

    // ----- Daylight saving: Europe/London ends BST at 02:00 on Sunday 25 October 2026 ----------------

    [Fact]
    public void The_day_the_clocks_go_back_is_twenty_five_hours_long()
    {
        var day = new DateTime(2026, 10, 25);
        var starts = EventTime.StartOfDay(day, London);
        var ends = EventTime.StartOfDay(day.AddDays(1), London);
        Assert.Equal(new DateTimeOffset(2026, 10, 24, 23, 0, 0, TimeSpan.Zero), starts.ToUniversalTime());
        Assert.Equal(new DateTimeOffset(2026, 10, 26, 0, 0, 0, TimeSpan.Zero), ends.ToUniversalTime());
        Assert.Equal(TimeSpan.FromHours(25), ends - starts);
        // 01:30 happens twice that night: the first is BST, the second GMT.
        Assert.Equal("1:30 am +01", EventTime.TimeText(new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero), London));
        Assert.Equal("1:30 am GMT", EventTime.TimeText(new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.Zero), London));
    }

    [Fact]
    public void A_london_events_photo_window_runs_from_its_midnights_across_the_clock_change()
    {
        // 6 pm on Sunday 25 October 2026, London (GMT by then).
        var start = new DateTimeOffset(2026, 10, 25, 18, 0, 0, TimeSpan.Zero);
        var (opens, closes) = EventDayWindow.Bounds(start, 1, London);
        // Opens at midnight starting Saturday 24 October (still BST), closes at midnight ending Monday 26th (GMT).
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 23, 0, 0, TimeSpan.Zero), opens.ToUniversalTime());
        Assert.Equal(new DateTimeOffset(2026, 10, 27, 0, 0, 0, TimeSpan.Zero), closes.ToUniversalTime());

        Assert.True(EventDayWindow.IsOpen(start, opens, 1, London));
        Assert.False(EventDayWindow.IsOpen(start, opens.AddTicks(-1), 1, London));
        Assert.True(EventDayWindow.IsOpen(start, closes.AddTicks(-1), 1, London));
        Assert.False(EventDayWindow.IsOpen(start, closes, 1, London));
    }

    [Fact]
    public void A_maldives_events_window_is_what_it_always_was()
    {
        var start = new DateTimeOffset(2027, 3, 14, 19, 30, 0, TimeSpan.FromHours(5));
        var (opens, closes) = EventDayWindow.Bounds(start, 1, Male);
        Assert.Equal(new DateTimeOffset(2027, 3, 13, 0, 0, 0, TimeSpan.FromHours(5)), opens);
        Assert.Equal(new DateTimeOffset(2027, 3, 16, 0, 0, 0, TimeSpan.FromHours(5)), closes);
        Assert.Equal(EventDayWindow.Bounds(start, 1, null), (opens, closes));
    }

    [Fact]
    public void A_new_york_event_late_at_night_counts_its_own_day_not_utcs()
    {
        // 9 pm on Friday 15 January in New York is already Saturday in UTC and in Malé.
        var start = new DateTimeOffset(2027, 1, 15, 21, 0, 0, TimeSpan.FromHours(-5));
        var (opens, _) = EventDayWindow.Bounds(start, 1, NewYork);
        Assert.Equal(new DateTimeOffset(2027, 1, 14, 0, 0, 0, TimeSpan.FromHours(-5)), opens);
        // On the old Malé-only rule it opened a day later, missing the day before entirely.
        Assert.NotEqual(opens, EventDayWindow.Bounds(start, 1, Male).Opens);
    }

    // ----- Calendars ---------------------------------------------------------------------------------

    // 11:30 pm on Saturday 24 October in London (BST) is Sunday 25th in Malé.
    private static readonly DateTimeOffset LateInLondon = new(2026, 10, 24, 22, 30, 0, TimeSpan.Zero);

    private static CalendarEntry Entry(bool allDay, string zone) =>
        new("Party", LateInLondon, null, allDay, "Camden", null, "std@invites.blog", null, zone);

    [Fact]
    public void An_all_day_entry_is_on_the_events_own_date()
    {
        var ics = CalendarLinks.Ics(Entry(true, London), new DateTimeOffset(2026, 10, 1, 0, 0, 0, TimeSpan.Zero));
        Assert.Contains("DTSTART;VALUE=DATE:20261024\r\n", ics);
        Assert.Contains("DTEND;VALUE=DATE:20261025\r\n", ics);
        Assert.Contains("&dates=20261024/20261025", CalendarLinks.Google(Entry(true, London)));
        // The same instant as a Maldives event is the 25th there.
        Assert.Contains("DTSTART;VALUE=DATE:20261025\r\n", CalendarLinks.Ics(Entry(true, Male)));
    }

    [Fact]
    public void A_timed_entry_is_written_in_utc_whatever_the_zone()
    {
        foreach (var zone in new[] { London, Male, NewYork })
        {
            var ics = CalendarLinks.Ics(Entry(false, zone));
            Assert.Contains("DTSTART:20261024T223000Z\r\n", ics);
            Assert.Contains("DTEND:20261025T013000Z\r\n", ics);
            Assert.Contains("&dates=20261024T223000Z/20261025T013000Z", CalendarLinks.Google(Entry(false, zone)));
            Assert.Contains("&startdt=2026-10-24T22%3A30%3A00Z", CalendarLinks.Outlook(Entry(false, zone)));
        }
    }

    [Fact]
    public void A_save_the_dates_calendar_entry_carries_the_events_zone()
    {
        var c = new Campaign { Id = Guid.NewGuid(), Title = "Party", EventStartAt = LateInLondon, AllDay = true, TimeZone = London, CustomContentJson = "{}" };
        var entry = Application.Campaigns.EventCalendar.For(c, null);
        Assert.Equal(London, entry.TimeZone);
        Assert.Contains("DTSTART;VALUE=DATE:20261024\r\n", CalendarLinks.Ics(entry));
    }

    // ----- The invitation itself ---------------------------------------------------------------------

    [Fact]
    public void The_invitation_shows_the_events_date_and_time_with_its_zone()
    {
        var render = new InviteRenderService(new RuleEngine(), new ConfigurationBuilder().Build());
        var campaign = new Campaign
        {
            Id = Guid.NewGuid(), Title = "Wedding", TemplateVersion = "1.0.0", Status = CampaignStatus.Dispatched,
            EventStartAt = LondonWinter, TimeZone = London,
            CustomContentJson = "{}", ThemeOverridesJson = "{}", RulesJson = "{\"rules\":[]}", TemplateManifestJson = "{}",
        };
        var data = (JsonObject)render.Build(campaign, TestData.Template(), new Guest { Id = Guid.NewGuid(), Name = "N", Gender = "female" },
            new Invite { Id = Guid.NewGuid() }, "https://x/i/t", "Host", null, null).Data;
        var ev = (JsonObject)data["event"]!;
        Assert.Equal("Saturday, 16 January 2027", ev["date"]!.ToString());
        Assert.Equal("7:00 pm GMT", ev["time"]!.ToString());
        Assert.Equal("2027-01-16T19:00:00Z", ev["startsAt"]!.ToString());
        Assert.Equal(London, ev["timeZone"]!.ToString());

        // The same instant on a Maldives event (every event made before zones existed).
        campaign.TimeZone = Male;
        var male = (JsonObject)((JsonObject)render.Build(campaign, TestData.Template(), new Guest { Id = Guid.NewGuid(), Name = "N", Gender = "female" },
            new Invite { Id = Guid.NewGuid() }, "https://x/i/t", "Host", null, null).Data)["event"]!;
        Assert.Equal(("Sunday, 17 January 2027", "12:00 am +05"), (male["date"]!.ToString(), male["time"]!.ToString()));
    }
}
