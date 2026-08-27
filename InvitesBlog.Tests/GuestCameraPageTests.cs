using System.Text.RegularExpressions;
using InvitesBlog.Api.Rendering;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>
/// The camera page a guest shoots from.
///
/// <para>Most of what this feature is cannot be tested here — a camera needs a camera. What CAN be
/// pinned is the part that would fail silently: the client is an embedded resource, so a rename or a
/// dropped csproj entry would ship a page that looks right, opens no viewfinder, and says nothing.</para>
/// </summary>
public class GuestCameraPageTests
{
    private static string Page(string title = "Raniya's birthday", string nonce = "n0nc3") =>
        GuestCameraPage.Render(
            "/r/abc/photos/capture", "/r/abc/photos", title,
            GuestPalette.From("#4d0000", "#1b1019", "#f7eee3"), nonce);

    /// <summary>
    /// The one that matters. An empty script is a page with a shutter that does nothing, and nothing
    /// else in the build would notice.
    /// </summary>
    [Fact]
    public void The_camera_client_is_actually_embedded()
    {
        var html = Page();

        Assert.Contains("__ibCamera", html);
        // A marker from inside camera.js itself, so this fails if the resource resolves to empty.
        Assert.Contains("getUserMedia", html);
        Assert.Contains("indexedDB", html);
    }

    /// <summary>The script runs only because the nonce matches the one in the response's CSP.</summary>
    [Fact]
    public void The_inline_script_carries_the_nonce()
    {
        Assert.Contains("<script nonce=\"n0nc3\">", Page(nonce: "n0nc3"));
    }

    [Fact]
    public void It_knows_where_to_upload_and_where_the_gallery_is()
    {
        var html = Page();

        Assert.Contains("upload: \"/r/abc/photos/capture\"", html);
        Assert.Contains("href=\"/r/abc/photos\"", html);
    }

    /// <summary>Same palette as the pages either side of it, so the camera is not a second product.</summary>
    [Fact]
    public void It_wears_the_campaign_colours()
    {
        var html = Page();

        Assert.Contains("--accent:#4d0000", html);
        Assert.Contains("--bg:#1b1019", html);
    }

    /// <summary>An event title is inviter-supplied text sitting in a document that runs a script.</summary>
    [Fact]
    public void The_event_title_is_escaped()
    {
        var html = Page(title: "<img src=x onerror=alert(1)>");

        Assert.DoesNotContain("<img src=x", html);
        Assert.Contains("&lt;img src=x", html);
    }

    /// <summary>
    /// The viewfinder must fill the phone without the URL bar being able to push the shutter off the
    /// bottom — svh is fixed per orientation where vh is not.
    /// </summary>
    [Fact]
    public void The_stage_is_sized_so_the_shutter_cannot_be_pushed_off_screen()
    {
        var html = Page();

        Assert.Contains("100svh", html);
        Assert.DoesNotContain("height:100vh", html);
    }

    /// <summary>
    /// A guest who cannot open the camera must not be left with nothing. The gallery takes library
    /// uploads, so the gate points there rather than at a sign-in they do not have.
    /// </summary>
    [Fact]
    public void The_denied_state_sends_them_somewhere_they_can_still_contribute()
    {
        var html = Page();

        Assert.Contains("id=\"why\"", html);
        Assert.Contains("library", html);
        Assert.Contains("See the photos", html);
    }

    /// <summary>
    /// The first moment on this page is the browser's permission prompt, during which there is no
    /// video to show. Without a state of its own that reads as a black rectangle under a shutter
    /// that does nothing.
    /// </summary>
    [Fact]
    public void It_says_something_while_the_camera_is_still_opening()
    {
        var html = Page();

        Assert.Contains("Starting the camera", html);
        // Keyed on the absence of a state, so it clears the moment the stream arrives or is refused.
        Assert.Contains("body:not([data-state]) .starting", html);
    }

    /// <summary>
    /// The script and the markup are written in two different files and shipped as one document, so
    /// nothing but this notices when they drift. A renamed id is not a build error — it is a null at
    /// a party, on the one control someone is reaching for.
    /// </summary>
    [Fact]
    public void Every_element_the_script_reaches_for_exists_in_the_page()
    {
        var html = Page();

        var wanted = Regex.Matches(html, @"\$\('([^']+)'\)")
            .Select(m => m.Groups[1].Value).Distinct().ToList();
        var present = Regex.Matches(html, @"id=""([^""]+)""")
            .Select(m => m.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

        Assert.NotEmpty(wanted);
        var missing = wanted.Where(w => !present.Contains(w)).ToList();
        Assert.True(missing.Count == 0, "Script asks for ids the page does not have: " + string.Join(", ", missing));
    }

    /// <summary>A selfie preview is mirrored; the rear camera is not.</summary>
    [Fact]
    public void The_selfie_preview_is_mirrored()
    {
        var html = Page();

        var rule = Regex.Match(html, @"video\.mirror \{[^}]*\}").Value;
        Assert.Contains("scaleX(-1)", rule);
        // Composed with the fallback zoom rather than replacing it, or a selfie would be mirrored
        // OR framed in, never both.
        Assert.Contains("--crop", rule);
    }

    /// <summary>
    /// Tap-to-focus draws its mark only where focus can actually be steered. On a camera doing its
    /// own continuous focus a reticle would be claiming credit for something the tap had no part in.
    /// </summary>
    [Fact]
    public void The_focus_mark_exists_and_starts_hidden()
    {
        var html = Page();

        Assert.Contains("id=\"reticle\"", html);
        Assert.Contains("pointsOfInterest", html);
        // The mirror inversion: a tap on the left of a selfie preview is the right of the sensor.
        Assert.Contains("x = 1 - x", html);
    }

    /// <summary>Front cameras are wide enough that a selfie at arm's length is mostly room.</summary>
    [Fact]
    public void The_front_camera_opens_a_little_way_in()
    {
        var html = Page();

        Assert.Contains("FRONT_ZOOM", html);
        // Sensor zoom where the track has it, a crop only where it does not — never both.
        Assert.Contains("caps.zoom", html);
        Assert.Contains("crop = FRONT_ZOOM", html);
    }
}
