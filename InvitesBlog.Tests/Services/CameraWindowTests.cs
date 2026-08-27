using InvitesBlog.Domain.Enums;
using InvitesBlog.Infrastructure.Rendering;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>
/// When a guest is offered the camera: they said they were coming, and it is the night.
///
/// <para>Pinned here because the interesting cases are edges, and an edge that is wrong is wrong on
/// the one evening the feature exists for. Times are Raniya's: 22:00 in Malé, stored as 17:00 UTC
/// because the column is normalised and the offset the inviter typed does not survive the round
/// trip.</para>
/// </summary>
public class CameraWindowTests
{
    /// <summary>2026-08-28 22:00 Malé.</summary>
    private static readonly DateTimeOffset Start = new(2026, 8, 28, 17, 0, 0, TimeSpan.Zero);

    private static bool Open(DateTimeOffset now, RsvpStatus rsvp = RsvpStatus.Going) =>
        InviteRenderService.CameraIsOpen(Start, rsvp, now);

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

        Assert.NotEqual(
            Start.ToOffset(male).Date,
            twoHoursIn.ToOffset(male).Date);   // the date HAS turned over

        Assert.True(Open(twoHoursIn));          // and the camera is still open
    }

    [Fact]
    public void Open_earlier_on_the_day_itself() =>
        Assert.True(Open(new DateTimeOffset(2026, 8, 28, 6, 0, 0, TimeSpan.Zero)));

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

    /// <summary>Both conditions, not either: the right night is not enough on its own.</summary>
    [Fact]
    public void Going_alone_is_not_enough_either() =>
        Assert.False(Open(Start.AddDays(3), RsvpStatus.Going));

    /// <summary>The boundaries themselves, since off-by-one here costs an evening.</summary>
    [Fact]
    public void The_edges_are_inclusive()
    {
        Assert.True(Open(new DateTimeOffset(2026, 8, 28, 0, 0, 0, TimeSpan.Zero)));  // opens
        Assert.False(Open(new DateTimeOffset(2026, 8, 27, 23, 59, 59, TimeSpan.Zero)));
        Assert.True(Open(Start.AddHours(12)));                                        // closes
        Assert.False(Open(Start.AddHours(12).AddSeconds(1)));
    }
}
