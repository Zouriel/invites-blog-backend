using InvitesBlog.Domain.Enums;
using InvitesBlog.Infrastructure.Rendering;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>
/// When a guest is offered the camera, and what the invitation asks them.
///
/// <para>The camera once also required the day of the event. That is the more careful rule and it
/// is gone deliberately: it made the feature untestable and left almost every guest without it,
/// because most never answer an invitation at all. What remains is the single condition.</para>
/// </summary>
public class CameraAndRsvpPromptTests
{
    [Fact]
    public void Offered_to_a_guest_who_said_they_were_coming() =>
        Assert.True(InviteRenderService.CameraIsOpen(RsvpStatus.Going));

    /// <summary>
    /// Everyone else, including the large majority who never replied. That is the cost of this rule
    /// and it is deliberate — see the note on the method.
    /// </summary>
    [Theory]
    [InlineData(RsvpStatus.NoResponse)]
    [InlineData(RsvpStatus.Maybe)]
    [InlineData(RsvpStatus.NotGoing)]
    [InlineData(RsvpStatus.ViewedOnly)]
    public void Withheld_from_everyone_else(RsvpStatus rsvp) =>
        Assert.False(InviteRenderService.CameraIsOpen(rsvp));

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
