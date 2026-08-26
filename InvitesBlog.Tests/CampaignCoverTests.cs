using InvitesBlog.Application.Campaigns;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>
/// The picture a campaign shows when it is listed rather than opened. These exist because the first
/// version got it wrong in a way nobody would report as a bug: every Gilded Hour invitation in the
/// grid showed "AMELIA", the name baked into that template's marketing poster.
/// </summary>
public class CampaignCoverTests
{
    [Fact]
    public void A_campaign_with_no_cover_has_none_to_read() =>
        Assert.Null(CampaignCover.Read("""{"title":"Raniya's birthday"}"""));

    [Fact]
    public void The_cover_reads_back_out()
    {
        var json = CampaignCover.Write("""{"title":"Raniya's birthday"}""", "/assets/campaigns/a/b.jpg");

        Assert.Equal("/assets/campaigns/a/b.jpg", CampaignCover.Read(json));
    }

    /// <summary>
    /// The dashboard sets a cover without ever loading the rest of the content. If writing replaced
    /// the document instead of merging into it, changing the picture would silently wipe the title,
    /// the venue and every field the builder wrote.
    /// </summary>
    [Fact]
    public void Setting_a_cover_preserves_everything_else_in_the_content()
    {
        var before = """{"title":"Raniya's birthday","venueName":"Home","fields":{"event.dressCode":"Smart"}}""";

        var after = CampaignCover.Write(before, "/assets/c.jpg");

        // Compared through the parser, not as a substring: System.Text.Json escapes the apostrophe
        // in "Raniya's" to \u0027, so a raw substring check tests the encoder rather than the merge.
        var parsed = System.Text.Json.Nodes.JsonNode.Parse(after)!.AsObject();
        Assert.Equal("Raniya's birthday", parsed["title"]!.ToString());
        Assert.Contains("venueName", after, StringComparison.Ordinal);
        Assert.Contains("event.dressCode", after, StringComparison.Ordinal);
    }

    [Fact]
    public void Clearing_the_cover_removes_the_key_rather_than_blanking_it()
    {
        var json = CampaignCover.Write("""{"coverImageUrl":"/assets/c.jpg","title":"T"}""", null);

        Assert.Null(CampaignCover.Read(json));
        Assert.DoesNotContain("coverImageUrl", json, StringComparison.Ordinal);
        Assert.Contains("\"title\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void A_campaign_with_no_content_at_all_can_still_be_given_one()
    {
        foreach (var empty in new string?[] { null, "", "   ", "{}" })
            Assert.Equal("/assets/c.jpg", CampaignCover.Read(CampaignCover.Write(empty, "/assets/c.jpg")));
    }

    /// <summary>
    /// Content is written by the builder and has been through several shapes. A campaign whose
    /// content cannot be parsed still has to appear in a list — "no cover" is the answer, not a throw.
    /// </summary>
    [Fact]
    public void Unparseable_content_reads_as_no_cover_rather_than_throwing()
    {
        Assert.Null(CampaignCover.Read("not json at all"));
        Assert.Null(CampaignCover.Read("[1,2,3]"));
    }

    [Fact]
    public void Writing_over_unparseable_content_still_yields_a_usable_cover() =>
        Assert.Equal("/assets/c.jpg", CampaignCover.Read(CampaignCover.Write("not json", "/assets/c.jpg")));

    [Fact]
    public void An_empty_url_is_treated_as_absent_not_as_a_cover() =>
        Assert.Null(CampaignCover.Read("""{"coverImageUrl":"  "}"""));
}
