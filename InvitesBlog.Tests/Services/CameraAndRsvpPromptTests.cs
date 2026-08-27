using InvitesBlog.Domain.Enums;
using InvitesBlog.Infrastructure.Rendering;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>
/// When a guest is offered the camera, and what the invitation asks them.
///
/// <para>Two conditions: the answer, and the day. The edges are what matter — an edge that is wrong
/// is wrong on the one evening the feature exists for. Times are Raniya's: 22:00 in Malé, stored as
/// 17:00 UTC because the column is normalised and the offset the inviter typed does not survive the
/// round trip.</para>
/// </summary>
public class CameraAndRsvpPromptTests
{
    /// <summary>2026-08-28 22:00 Malé.</summary>
    private static readonly DateTimeOffset Start = new(2026, 8, 28, 17, 0, 0, TimeSpan.Zero);

    private static bool Open(DateTimeOffset now, RsvpStatus rsvp = RsvpStatus.Going, bool ignoreDate = false) =>
        InviteRenderService.CameraIsOpen(Start, rsvp, now, ignoreDate);

    [Fact]
    public void Open_while_the_party_is_happening() =>
        Assert.True(Open(Start.AddHours(1)));

    /// <summary>
    /// The case a calendar-date comparison gets wrong. Two hours in it is already tomorrow in Malé,
    /// and the camera would have gone dark at exactly the point people are using it.
    /// </summary>
    [Fact]
    public void Still_open_after_local_midnight()
    {
        var twoHoursIn = Start.AddHours(2);
        var male = TimeSpan.FromHours(5);

        Assert.NotEqual(Start.ToOffset(male).Date, twoHoursIn.ToOffset(male).Date);
        Assert.True(Open(twoHoursIn));
    }

    [Fact]
    public void Shut_the_day_before() =>
        Assert.False(Open(new DateTimeOffset(2026, 8, 27, 23, 59, 0, TimeSpan.Zero)));

    [Fact]
    public void Shut_once_the_night_is_over() =>
        Assert.False(Open(Start.AddHours(13)));

    [Theory]
    [InlineData(RsvpStatus.NoResponse)]
    [InlineData(RsvpStatus.Maybe)]
    [InlineData(RsvpStatus.NotGoing)]
    [InlineData(RsvpStatus.ViewedOnly)]
    public void Shut_for_anyone_who_did_not_say_they_were_coming(RsvpStatus rsvp) =>
        Assert.False(Open(Start.AddHours(1), rsvp));

    [Fact]
    public void The_edges_are_inclusive()
    {
        Assert.True(Open(new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero)));
        Assert.False(Open(new DateTimeOffset(2026, 8, 27, 23, 59, 59, TimeSpan.Zero)));
        Assert.True(Open(Start.AddHours(12)));
        Assert.False(Open(Start.AddHours(12).AddSeconds(1)));
    }

    // ----- the exemption -------------------------------------------------------------------------

    /// <summary>An exempt campaign is free of the day, months either side of it.</summary>
    [Theory]
    [InlineData(-90)]
    [InlineData(90)]
    public void An_exempt_campaign_ignores_the_day(int daysAway) =>
        Assert.True(Open(Start.AddDays(daysAway), ignoreDate: true));

    /// <summary>
    /// It exempts the DATE and nothing else. A test invitation that let anyone shoot would be a
    /// hole rather than an affordance.
    /// </summary>
    [Theory]
    [InlineData(RsvpStatus.NoResponse)]
    [InlineData(RsvpStatus.Maybe)]
    [InlineData(RsvpStatus.NotGoing)]
    [InlineData(RsvpStatus.ViewedOnly)]
    public void An_exempt_campaign_still_asks_whether_they_are_coming(RsvpStatus rsvp) =>
        Assert.False(Open(Start.AddDays(90), rsvp, ignoreDate: true));

    // ----- what the RSVP control says -------------------------------------------------------------

    /// <summary>Already coming: nothing left to ask, so the control goes.</summary>
    [Fact]
    public void A_guest_who_is_coming_is_asked_nothing() =>
        Assert.Equal(string.Empty, InviteRenderService.RsvpPrompt(RsvpStatus.Going));

    /// <summary>A maybe is asked to settle it; a no is offered the chance to change their mind.</summary>
    [Theory]
    [InlineData(RsvpStatus.Maybe, "Confirm your reply")]
    [InlineData(RsvpStatus.NotGoing, "Change your reply")]
    public void An_unsettled_answer_is_offered_a_second_go(RsvpStatus status, string expected) =>
        Assert.Equal(expected, InviteRenderService.RsvpPrompt(status));

    [Theory]
    [InlineData(RsvpStatus.NoResponse)]
    [InlineData(RsvpStatus.ViewedOnly)]
    public void Someone_who_has_not_answered_is_simply_asked(RsvpStatus status) =>
        Assert.Equal("Reply now", InviteRenderService.RsvpPrompt(status));
}
