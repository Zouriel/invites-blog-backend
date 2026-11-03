using System.Text.RegularExpressions;
using InvitesBlog.Api.Rendering;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>
/// The two ways a guest adds to an event's photo box from the server-rendered path.
///
/// <para>The camera leads — a guest at the party has not "captured" anything yet. But the shot they
/// actually want is often already in the camera roll, and a page offering only a viewfinder makes
/// that photograph unreachable without an account.</para>
/// </summary>
public class GuestPagesPhotoBarTests
{
    private static string Box(bool canUpload = true, string? nonce = null) =>
        GuestPages.Photos(
            "/r/abc/photos", "/r/abc", "Raniya's birthday",
            [], canUpload, null, "/r/abc/camera",
            GuestPalette.From("#4d0000", "#1b1019", "#f7eee3"), nonce);

    [Fact]
    public void Offers_both_the_camera_and_the_library()
    {
        var html = Box();

        Assert.Contains("href=\"/r/abc/camera\"", html);
        Assert.Contains("type=\"file\"", html);
        Assert.Contains("enctype=\"multipart/form-data\"", html);
        // The picker posts to the box's own path, which is what authorizes it.
        Assert.Contains("action=\"/r/abc/photos\"", html);
    }

    /// <summary>A phone should offer the camera roll, and more than one photo at a time.</summary>
    [Fact]
    public void The_picker_takes_several_photos_at_once()
    {
        var html = Box();

        Assert.Contains("accept=\"image/*\"", html);
        Assert.Contains("multiple", html);
    }

    private static readonly Guid Mine = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid Theirs = Guid.Parse("22222222-2222-2222-2222-222222222222");

    private static string Album(string? nonce = null) => GuestPages.Photos(
        "/r/abc/photos", "/r/abc", "Raniya's birthday",
        new List<(Guid, string, string, string, string?, bool, bool)>
        {
            (Mine, "t", "u", "/assets/buckets/o.jpg", "Ali", true, false),
            (Theirs, "t2", "u2", "/assets/buckets/o2.mp4", "Hawwa", false, true),
        },
        true, null, "/r/abc/camera", GuestPalette.Fallback, nonce);

    /// <summary>
    /// The owner's rule: nothing on a tile deletes. A tap opens the photo's own page; removing is in
    /// the selection bar and in that viewer.
    /// </summary>
    [Fact]
    public void No_tile_has_a_remove_control_and_a_tap_opens_the_photo()
    {
        foreach (var html in new[] { Album(), Album("n0nc3") })
        {
            var grid = html[html.IndexOf("<div class=\"grid\">", StringComparison.Ordinal)..html.IndexOf("class=\"back\"", StringComparison.Ordinal)];
            Assert.DoesNotContain("/remove", grid);
            Assert.DoesNotContain("/delete", grid);
            Assert.DoesNotContain("Remove", grid);
            Assert.Contains($"href=\"/r/abc/photos/{Mine}\"", grid);
            Assert.Contains($"href=\"/r/abc/photos/{Theirs}\"", grid);
        }
    }

    /// <summary>What the script needs to choose several: ids, originals and which are the guest's own.</summary>
    [Fact]
    public void Tiles_carry_what_choosing_needs_and_the_bar_is_in_the_page()
    {
        var html = Album("n0nc3");
        Assert.Contains($"data-id=\"{Mine}\" data-original=\"/assets/buckets/o.jpg\" data-mine=\"1\"", html);
        Assert.Contains($"data-id=\"{Theirs}\" data-original=\"/assets/buckets/o2.mp4\">", html);
        Assert.Contains("id=\"pick\"", html);
        Assert.Contains("id=\"sel-remove\"", html);
        Assert.Contains("id=\"sel-share\"", html);
        Assert.Contains("id=\"ready-yes\"", html);
        // Markers from gallery.js itself, so an unembedded resource fails here rather than silently.
        Assert.Contains("__ibGallery", html);
        Assert.Contains("application/json", html);
        Assert.Contains("navigator.share", html);
        Assert.Contains("userActivation", html);
    }

    /// <summary>The viewer has Download (a saved file, not a new tab) and, for the guest's own, Remove.</summary>
    [Fact]
    public void The_viewer_offers_download_and_removes_only_the_guests_own_through_the_confirmation()
    {
        var own = GuestPages.PhotoView("/r/abc/photos", Mine, "u", "/assets/buckets/o.jpg", false, "Ali", true,
            null, "/r/abc/photos/next", "invitesblog-11111111.jpg", GuestPalette.Fallback);
        Assert.Contains("href=\"/assets/buckets/o.jpg\" download=\"invitesblog-11111111.jpg\"", own);
        Assert.DoesNotContain("target=", own);
        Assert.Contains($"href=\"/r/abc/photos/{Mine}/remove\"", own);
        Assert.Contains("href=\"/r/abc/photos/next\"", own);
        // No nonce, no script: Download and Remove still work as links.
        Assert.DoesNotContain("<script", own);

        var theirs = GuestPages.PhotoView("/r/abc/photos", Theirs, "u2", "/assets/buckets/o2.mp4", true, "Hawwa", false,
            "/r/abc/photos/prev", null, "invitesblog-22222222.mp4", GuestPalette.Fallback, "n0nc3");
        Assert.DoesNotContain("/remove", theirs);
        Assert.Contains("<video", theirs);
        Assert.Contains("id=\"view-share\"", theirs);
        Assert.Contains("<script nonce=\"n0nc3\">", theirs);
    }

    [Fact]
    public void Removing_from_the_viewer_still_asks_first()
    {
        var confirm = GuestPages.ConfirmRemove("/r/abc/photos/1/delete", "/r/abc/photos");
        Assert.Contains("This cannot be undone", confirm);
        Assert.Contains("action=\"/r/abc/photos/1/delete\"", confirm);
        Assert.Contains("Keep it", confirm);
    }

    [Theory]
    [InlineData("/assets/buckets/x/abc.PNG", false, "invitesblog-11111111.png")]
    [InlineData("/assets/buckets/x/abc", true, "invitesblog-11111111.mp4")]
    [InlineData("https://invites.blog/assets/b/c.jpeg?v=2", false, "invitesblog-11111111.jpeg")]
    public void Download_names_match_the_app_and_website(string url, bool video, string expected) =>
        Assert.Equal(expected, GuestPages.DownloadName(Mine, url, video));

    /// <summary>A cancelled event's box is a read-only archive — neither door opens.</summary>
    [Fact]
    public void A_closed_box_offers_neither()
    {
        var html = Box(canUpload: false);

        Assert.DoesNotContain("/r/abc/camera", html);
        Assert.DoesNotContain("type=\"file\"", html);
    }

    /// <summary>
    /// A clip's tile is a still, so without a marker it is indistinguishable from a photograph until
    /// someone opens it and gets a video.
    /// </summary>
    [Fact]
    public void A_clip_is_marked_in_the_grid_and_a_photograph_is_not()
    {
        var photos = new List<(Guid, string, string, string, string?, bool, bool)>
        {
            (Guid.Parse("11111111-1111-1111-1111-111111111111"), "t", "u", "o", "Ali", true, true),
            (Guid.Parse("22222222-2222-2222-2222-222222222222"), "t2", "u2", "o2", "Ali", true, false),
        };

        var html = GuestPages.Photos(
            "/r/abc/photos", "/r/abc", "Raniya's birthday", photos, true, null, "/r/abc/camera",
            GuestPalette.Fallback);

        // The tile itself, not the CSS rule that styles it — both carry the same attribute text.
        Assert.Equal(1, Regex.Matches(html, "<div class=\"tile\" data-kind=\"video\" data-id=").Count);
        Assert.Equal(1, Regex.Matches(html, "<div class=\"tile\" data-id=").Count);
        Assert.Contains(".tile[data-kind=\"video\"]::after", html);
    }
}
