using InvitesBlog.Api.Rendering;
using InvitesBlog.Application.Dtos.Invites;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>The small credit a guest sees on an invitation and an album.</summary>
public class GuestCreditHtmlTests
{
    private const string Page = "<html><body><main>Invitation</main></body></html>";

    [Fact]
    public void A_free_event_says_made_with_invites_blog_just_before_the_end_of_the_body()
    {
        var html = GuestCreditHtml.InjectIntoInvitation(Page, new GuestCredit(true, null, null, null));

        Assert.Contains("Made with invites.blog", html);
        Assert.True(html.IndexOf("data-ib-credit", StringComparison.Ordinal) < html.IndexOf("</body>", StringComparison.Ordinal));
    }

    [Fact]
    public void A_pass_takes_the_mark_off()
    {
        Assert.Equal(Page, GuestCreditHtml.InjectIntoInvitation(Page, new GuestCredit(false, null, null, null)));
    }

    [Fact]
    public void A_studio_designer_is_credited_and_their_name_is_encoded()
    {
        var html = GuestCreditHtml.InjectIntoInvitation(Page, new GuestCredit(false, "Aisha <b>Studio</b>", null, null));

        Assert.Contains("Designed by Aisha &lt;b&gt;Studio&lt;/b&gt;", html);
        Assert.DoesNotContain("Made with", html);
    }

    [Fact]
    public void An_album_at_a_venue_carries_its_name()
    {
        Assert.Contains("Photos at Sun Siyam", GuestCreditHtml.AlbumFooter(new GuestCredit(false, null, "Sun Siyam", null)));
        Assert.Contains("Sun Siyam", GuestCreditHtml.AlbumHeader(new GuestCredit(false, null, "Sun Siyam", "/assets/logo.png")));
        Assert.Equal("", GuestCreditHtml.AlbumHeader(new GuestCredit(true, null, null, null)));
    }
}
