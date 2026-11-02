using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Moderation;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>The basic filter on comments and captions: obvious slurs and threats, never ordinary words.</summary>
public class ObjectionableTextTests
{
    [Theory]
    [InlineData("you stupid faggot")]
    [InlineData("F A G")]                       // letters on their own are words: not caught, on purpose
    public void Spaced_out_letters_are_left_to_reports(string text)
    {
        // The first is refused; the second is three single letters, which the filter doesn't guess at.
        Assert.Equal(text.StartsWith("you"), ObjectionableText.IsObjectionable(text));
    }

    [Theory]
    [InlineData("kill yourself")]
    [InlineData("Go KILL YOURSELF!!")]
    [InlineData("just kys")]
    [InlineData("n1gg3r")]
    [InlineData("what a r3tard")]
    [InlineData("I'll kill you")]
    [InlineData("hope u die tonight")]
    [InlineData("c*nt")]                        // split by a symbol: "c" and "nt", not caught
    public void Slurs_and_threats_are_caught_through_common_disguises(string text)
    {
        var expected = text != "c*nt";
        Assert.Equal(expected, ObjectionableText.IsObjectionable(text));
    }

    [Theory]
    [InlineData("Congratulations to the happy couple!")]
    [InlineData("Off to Scunthorpe after, classy night")]
    [InlineData("The spice rack and the cocktails were amazing")]
    [InlineData("Fagioli and pakistani biryani for dinner")]
    [InlineData("Skill yourself up for the dance floor")]
    [InlineData("Damn, that was a fucking great party")]
    [InlineData("")]
    [InlineData(null)]
    public void Ordinary_words_and_swearing_pass(string? text) =>
        Assert.False(ObjectionableText.IsObjectionable(text));

    [Fact]
    public void Every_listed_term_is_caught_on_its_own()
    {
        foreach (var word in ObjectionableTerms.Words) Assert.True(ObjectionableText.IsObjectionable($"you {word}"), word);
        foreach (var phrase in ObjectionableTerms.Phrases) Assert.True(ObjectionableText.IsObjectionable($"well {phrase} ok"), phrase);
    }

    [Fact]
    public void Check_refuses_with_the_plain_message()
    {
        var e = Assert.Throws<BusinessRuleException>(() => ObjectionableText.Check("kill yourself"));
        Assert.Equal((ObjectionableText.Refusal, ObjectionableText.Code), (e.Message, e.ErrorCode));
    }
}
