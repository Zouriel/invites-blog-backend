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

    /// <summary>
    /// The remove control overlaps the corner of the photograph it deletes, which is exactly where a
    /// thumb lands to open one. It must lead to a question, not straight to a POST.
    /// </summary>
    [Fact]
    public void Removing_a_photo_asks_first()
    {
        var photos = new List<(Guid, string, string, string, string?, bool)>
        {
            (Guid.Parse("11111111-1111-1111-1111-111111111111"), "t", "u", "o", "Ali", true),
        };
        var html = GuestPages.Photos(
            "/r/abc/photos", "/r/abc", "Raniya's birthday", photos, true, null, "/r/abc/camera",
            GuestPalette.Fallback);

        Assert.Contains("/r/abc/photos/11111111-1111-1111-1111-111111111111/remove", html);
        // The one-tap POST is gone from the grid; it now lives behind the confirmation.
        Assert.DoesNotContain("/delete\"", html);

        var confirm = GuestPages.ConfirmRemove("/r/abc/photos/1/delete", "/r/abc/photos");
        Assert.Contains("This cannot be undone", confirm);
        Assert.Contains("action=\"/r/abc/photos/1/delete\"", confirm);
        Assert.Contains("Keep it", confirm);
    }

    /// <summary>
    /// Confirming used to cost a page load before the question appeared, and the POST behind it
    /// re-rendered every tile — about a second per photo on venue wifi, with the wait landing before
    /// any sign the tap had registered. The question is asked in the page now.
    /// </summary>
    [Fact]
    public void The_delete_confirmation_is_asked_in_the_page_when_script_can_run()
    {
        var html = Box(nonce: "n0nc3");

        Assert.Contains("id=\"confirm\"", html);
        Assert.Contains("<script nonce=\"n0nc3\">", html);
        Assert.Contains("This cannot be undone", html);
        // A marker from gallery.js itself, so an unembedded resource fails here rather than silently.
        Assert.Contains("__ibGallery", html);
        Assert.Contains("application/json", html);
    }

    /// <summary>
    /// The enhancement is exactly that. Without a nonce the page ships no script, and Remove is
    /// still a link to a confirmation page that works on its own.
    /// </summary>
    [Fact]
    public void Without_script_the_remove_link_still_leads_to_a_confirmation()
    {
        var photos = new List<(Guid, string, string, string, string?, bool)>
        {
            (Guid.Parse("11111111-1111-1111-1111-111111111111"), "t", "u", "o", "Ali", true),
        };
        var html = GuestPages.Photos(
            "/r/abc/photos", "/r/abc", "Raniya's birthday", photos, true, null, "/r/abc/camera",
            GuestPalette.Fallback);

        Assert.DoesNotContain("<script", html);
        Assert.Contains("/r/abc/photos/11111111-1111-1111-1111-111111111111/remove", html);
    }

    /// <summary>A cancelled event's box is a read-only archive — neither door opens.</summary>
    [Fact]
    public void A_closed_box_offers_neither()
    {
        var html = Box(canUpload: false);

        Assert.DoesNotContain("/r/abc/camera", html);
        Assert.DoesNotContain("type=\"file\"", html);
    }
}
