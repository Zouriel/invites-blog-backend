using InvitesBlog.Application.Dtos.Campaigns;
using InvitesBlog.Application.Rsvp;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>
/// The question set is what already-collected answers are filed against, so the rules that keep keys
/// stable matter more than the editing itself.
/// </summary>
public class RsvpQuestionsTests
{
    // ----- Reading what a campaign asks ----------------------------------------------------------

    [Fact]
    public void A_campaign_that_never_configured_questions_still_asks_the_original_four()
    {
        // Not "ask nothing": these campaigns were asking these questions before the step existed.
        var questions = RsvpQuestions.Parse(null);

        Assert.Equal(4, questions.Count);
        Assert.Contains(questions, q => q.Key == RsvpQuestions.GuestCount);
        Assert.Contains(questions, q => q.Key == RsvpQuestions.Comment);
    }

    [Fact]
    public void An_empty_set_reads_as_unconfigured_rather_than_as_no_questions()
    {
        Assert.Equal(4, RsvpQuestions.Parse("{\"questions\":[]}").Count);
    }

    [Fact]
    public void Unreadable_json_falls_back_instead_of_throwing()
    {
        // A malformed column must not make every invitation for that campaign unopenable.
        Assert.Equal(4, RsvpQuestions.Parse("not json at all").Count);
    }

    [Fact]
    public void A_configured_set_round_trips()
    {
        var set = new List<RsvpQuestionDto>
        {
            new("guestCount", "Heads", "number", Required: true),
            new("song", "Song request", "text"),
        };

        var back = RsvpQuestions.Parse(RsvpQuestions.Serialize(set));

        Assert.Equal(2, back.Count);
        Assert.Equal("Heads", back[0].Label);
        Assert.True(back[0].Required);
        Assert.Equal("song", back[1].Key);
    }
}
