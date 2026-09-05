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

    /// <summary>
    /// Everything the recording gesture reaches for by id. Each of these is looked up in camera.js
    /// with no guard around it, so a rename here is a TypeError the moment somebody holds the
    /// shutter — on the one night of the year the page exists for, and only for the guests who tried
    /// to film something.
    /// </summary>
    [Theory]
    [InlineData("id=\"lock\"")]
    [InlineData("id=\"rectime\"")]
    [InlineData("id=\"shoot\"")]
    [InlineData("id=\"reticle\"")]
    [InlineData("id=\"queue\"")]
    public void Every_control_the_client_reaches_for_is_on_the_page(string element)
    {
        Assert.Contains(element, Page());
    }

    /// <summary>
    /// The lock and the clock are chrome for a recording, so they stay off the page until there is
    /// one. Driven by data-rec on the body, which is the only thing camera.js sets.
    /// </summary>
    [Fact]
    public void The_recording_chrome_is_hidden_until_something_is_recording()
    {
        var html = Page();

        Assert.Contains(".lock { position:absolute", html);
        Assert.Contains("display:none", html);
        Assert.Contains("body[data-rec=\"1\"] .lock { display:grid; }", html);
        Assert.Contains("body[data-rec] .rec { display:flex; }", html);
        // Locked, the shutter stops being a shutter and becomes a stop button.
        Assert.Contains("body[data-rec=\"locked\"] .shoot", html);
    }

    /// <summary>
    /// A hold is a recording and a tap is a photograph, and the button has to say so — it is the
    /// only control on the page, and the second gesture is invisible without a label.
    /// </summary>
    [Fact]
    public void The_shutter_says_it_does_both()
    {
        Assert.Contains("hold to record a video", Page());
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

    /// <summary>
    /// Zoom has to be asked for in the getUserMedia call itself. A camera permission granted
    /// without pan-tilt-zoom never gains it afterwards, so requesting it at applyConstraints time
    /// is too late — which is exactly how the zoom control could quietly never appear.
    /// </summary>
    [Fact]
    public void Zoom_permission_is_requested_when_the_camera_is_opened()
    {
        var html = Page();

        var calls = Regex.Matches(html, @"getUserMedia\(").Count;

        Assert.Contains("zoom: true", html);
        // Two asks, not one: a device that refuses the whole request over pan-tilt-zoom still gets
        // a camera from the plainer second attempt. Counted rather than matched on the exact text,
        // which pinned an earlier version of this test to a variable name and broke on a rename.
        Assert.True(calls >= 2, $"expected a fallback getUserMedia call, found {calls}");
    }

    /// <summary>
    /// A camera in a dark room lengthens its exposure and drops frames to do it. Demanding a steady
    /// rate fights the one adaptation that makes an evening usable.
    /// </summary>
    [Fact]
    public void The_frame_rate_floor_leaves_room_for_a_long_exposure()
    {
        Assert.Contains("frameRate: { ideal: 30, min: 5 }", Page());
    }

    /// <summary>
    /// Metering stays automatic. A fixed exposure is the wrong instrument for a party — the light
    /// changes as people move — and manual settings persist onto whatever the browser points at
    /// that camera next, which is not ours to leave behind.
    /// </summary>
    [Fact]
    public void The_camera_keeps_metering_for_itself()
    {
        var html = Page();

        Assert.Contains("exposureMode: 'continuous'", html);
        Assert.Contains("whiteBalanceMode: 'continuous'", html);
        Assert.Contains("focusMode: 'continuous'", html);
        // Night mode biases that adaptation rather than replacing it.
        Assert.Contains("exposureCompensation", html);
        Assert.DoesNotContain("exposureMode: 'manual'", html);
    }

    /// <summary>
    /// Without imageWidth/imageHeight a still comes back at whatever the device defaults to, which
    /// is often the preview size — so the full-resolution capture quietly was not one.
    /// </summary>
    [Fact]
    public void A_still_is_asked_for_at_the_sensors_own_size()
    {
        var html = Page();

        Assert.Contains("getPhotoCapabilities", html);
        Assert.Contains("settings.imageWidth = caps.imageWidth.max", html);
        Assert.Contains("fillLightMode", html);
    }

    /// <summary>
    /// With nothing to change, the encoder's own file is the better one. Drawing it to a canvas to
    /// read it straight back costs a generation of quality and the sensor's resolution for nothing.
    /// </summary>
    [Fact]
    public void An_unaltered_still_is_not_re_encoded()
    {
        var html = Page();

        Assert.Contains("if (!needsRedraw()) return still;", html);
        Assert.Contains("crop !== 1 || facing === 'user'", html);
    }

    /// <summary>
    /// The names the script relies on at the top of its scope must not be redeclared inside a
    /// function.
    ///
    /// <para>This is not style. `video` is the &lt;video&gt; element; a constraints object of the
    /// same name inside start() shadowed it, so `video.srcObject = stream` quietly set a property on
    /// a plain object and `video.classList` threw on undefined. The throw landed between the preview
    /// starting and the state being set, and the page sat on its loading spinner forever — with the
    /// markup, the ids, the config and every other test still passing.</para>
    /// </summary>
    [Theory]
    [InlineData("video")]
    [InlineData("track")]
    [InlineData("stream")]
    [InlineData("shutter")]
    [InlineData("strip")]
    public void The_scripts_top_level_names_are_never_shadowed(string name)
    {
        var html = Page();

        // A declaration of that name indented past the top level of the IIFE is a redeclaration.
        var shadowed = Regex.Matches(html, @"(?m)^\s{4,}(?:const|let|var)\s+" + name + @"\s*=")
            .Select(m => m.Value.Trim())
            .ToList();

        Assert.True(
            shadowed.Count == 0,
            $"'{name}' is redeclared inside a function: {string.Join(" | ", shadowed)}");
    }

    /// <summary>
    /// A fault while opening the camera must end somewhere. Between the stream arriving and the
    /// state becoming live there is nothing on screen but a spinner, and no path out of it.
    /// </summary>
    [Fact]
    public void A_failure_while_starting_cannot_leave_the_page_spinning()
    {
        var html = Page();

        Assert.Contains("document.body.dataset.state = 'live';", html);
        // The whole run-up to it is guarded, and the guard reports rather than swallowing.
        Assert.Contains("} catch (err) {\n      stop();\n      fail(err);", html.Replace("\r\n", "\n"));
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
    /// The focus mark is drawn on EVERY tap. It was once gated on getCapabilities() reporting a
    /// focusMode, which is not a signal worth trusting — devices that focus perfectly well do not
    /// always advertise it, and the result was a camera that refocused with no sign it had heard
    /// you. The mark says where you tapped, which is true whatever the camera does next.
    /// </summary>
    [Fact]
    public void The_focus_mark_is_drawn_for_every_tap()
    {
        var html = Page();

        Assert.Contains("id=\"reticle\"", html);
        Assert.Contains("pointsOfInterest", html);
        // The mirror inversion: a tap on the left of a selfie preview is the right of the sensor.
        Assert.Contains("x = 1 - x", html);
        // Nothing may stand between the tap and the mark.
        Assert.DoesNotContain("canFocus", html);
        // Four corner brackets, not a plain box.
        Assert.Contains("<i></i><i></i><i></i><i></i>", html);
    }

    /// <summary>The mark must never be able to cover the shutter.</summary>
    [Fact]
    public void The_focus_mark_sits_below_the_controls()
    {
        var html = Page();

        var reticle = Regex.Match(html, @"\.reticle \{[^}]*\}").Value;
        Assert.Contains("z-index:2", reticle);
        Assert.Contains("pointer-events:none", reticle);

        // The claim is about ORDER, so it is the controls' own stacking that has to be asserted —
        // z-index:2 against their default would have painted the mark over the shutter.
        foreach (var control in new[] { ".top", ".bottom" })
        {
            // Anchored to the start of a line, or a descendant rule like "body.night .bottom" is
            // matched instead — which says nothing about how the control itself stacks.
            var rule = Regex.Match(html, @"(?m)^\s*" + Regex.Escape(control) + @" \{[^}]*\}").Value;
            Assert.NotEqual(string.Empty, rule);
            Assert.True(rule.Contains("z-index:3"), $"{control} must stack above the focus mark");
        }
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

    /// <summary>
    /// The same page serves two paths now, and they authorize differently: an invited guest by a
    /// cookie, somebody who scanned a printed code by a ticket. The ticket rides in the page's own
    /// config so every upload carries it — a URL would put it in browser history, and web storage
    /// would hand it to any script on the origin.
    /// </summary>
    [Fact]
    public void Extra_upload_fields_reach_the_page()
    {
        var html = GuestCameraPage.Render(
            "/api/q/tok/media", "/q/tok", "Hana and Ibrahim", GuestPalette.Fallback, "n0nc3",
            fields: new Dictionary<string, string> { ["ticket"] = "abc.123" });

        Assert.Contains("""fields: {"ticket":"abc.123"}""", html);
    }

    /// <summary>
    /// The config is written into a script element, where HTML escaping means nothing and JSON
    /// encoding is everything: a value containing a closing script tag would otherwise end the
    /// element and turn the rest of the page into markup.
    /// </summary>
    [Fact]
    public void A_field_cannot_close_the_script_it_sits_in()
    {
        var html = GuestCameraPage.Render(
            "/api/q/tok/media", "/q/tok", "x", GuestPalette.Fallback, "n0nc3",
            fields: new Dictionary<string, string> { ["ticket"] = "</script><script>alert(1)</script>" });

        Assert.DoesNotContain("</script><script>alert(1)", html);
    }

    /// <summary>
    /// A contributor cannot read the bucket, so the guest's consolations — "see the photos", a
    /// "Gallery" button — describe a door that does not exist for them.
    /// </summary>
    [Fact]
    public void The_way_back_is_whatever_the_caller_says_it_is()
    {
        var html = GuestCameraPage.Render(
            "/api/q/tok/media", "/q/tok", "x", GuestPalette.Fallback, "n0nc3",
            backLabel: "Back", gateNote: "You can still add photos straight from this phone's library.",
            gateAction: "Add from your library");

        Assert.Contains(">Back<", html);
        Assert.Contains("Add from your library", html);
        Assert.DoesNotContain("See the photos", html);
        Assert.DoesNotContain(">Gallery<", html);
    }

    /// <summary>The guest wording is still the default, so nothing on that path moved.</summary>
    [Fact]
    public void A_guest_still_gets_the_gallery()
    {
        var html = Page();

        Assert.Contains(">Gallery<", html);
        Assert.Contains("See the photos", html);
        Assert.Contains("fields: {}", html);
    }
}
