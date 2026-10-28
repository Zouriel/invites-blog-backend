using System.Text;
using System.Text.Json.Nodes;
using InvitesBlog.Api.Rendering;
using InvitesBlog.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>
/// The floors the renderer puts under a document that declares nothing of its own.
///
/// <para>These matter most for a design a customer brought themselves: it is a picture, it has no
/// contract elements and never will, so without them an imported invitation would silently cost its
/// guests the ability to reply or to reach the media bucket — most of what the platform is for.</para>
/// </summary>
public class RenderedInvitationFloorsTests
{
    private const string BareDesign = """<html><body><img src="design.png"></body></html>""";

    private readonly IStorageService _storage = Substitute.For<IStorageService>();

    private RenderedInvitations Sut(string html)
    {
        _storage.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Encoding.UTF8.GetBytes(html));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Urls:AssetsBase"] = "/assets" })
            .Build();

        return new RenderedInvitations(_storage, config);
    }

    private static JsonObject Data(string? rsvpLink = "/r/abc/rsvp", string? cameraLink = "/r/abc/camera") =>
        new()
        {
            ["rsvp"] = new JsonObject { ["link"] = rsvpLink, ["label"] = "Reply now" },
            ["camera"] = cameraLink is null ? null : new JsonObject { ["link"] = cameraLink },
        };

    private Task<string?> RenderAsync(string html, JsonObject data) =>
        Sut(html).BuildAsync("/assets/imported/abc/", data, CancellationToken.None);

    /// <summary>
    /// Whether a FLOOR was appended, as opposed to the link merely appearing somewhere.
    ///
    /// <para>Counting occurrences of the link does not answer this and quietly gives the wrong one:
    /// the binder inlines the whole payload as <c>&lt;script id="invite-data"&gt;</c>, so every link
    /// appears in the document whether a bar was added or not — a test counting them passes for a
    /// design that got no bar at all. The appended bars are the only things claiming this z-index, so
    /// their presence is the actual question.</para>
    /// </summary>
    private static int AppendedBars(string html) => html.Split("z-index:2147483000").Length - 1;

    // ---------- the RSVP floor ----------

    /// <summary>
    /// The case the floor exists for: an uploaded picture, with no way for a guest to answer unless
    /// one is put there.
    /// </summary>
    [Fact]
    public async Task A_design_with_no_binding_gets_a_way_to_reply()
    {
        var html = await RenderAsync(BareDesign, Data());

        Assert.NotNull(html);
        // Two floors: the reply bar and the media-bucket bar, neither of which this design declared.
        Assert.Equal(2, AppendedBars(html!));
    }

    /// <summary>
    /// A guest who has already said they are coming has no rsvp.link — and must not be asked twice,
    /// the same rule a template's own button follows through [data-optional].
    /// </summary>
    [Fact]
    public async Task Somebody_who_already_replied_is_not_asked_again()
    {
        var html = await RenderAsync(BareDesign, Data(rsvpLink: null));

        Assert.NotNull(html);
        // The bucket bar still belongs; the reply bar does not. Asserted on the bar count and not on
        // the absence of the label, which is in the inlined payload either way — see AppendedBars.
        Assert.Equal(1, AppendedBars(html!));
    }

    /// <summary>
    /// A template that placed the binding itself keeps its own styling, and never gets an appended
    /// duplicate underneath it.
    /// </summary>
    [Fact]
    public async Task A_template_that_places_its_own_button_gets_no_second_one()
    {
        const string withOwn = """<html><body><a data-href="rsvp.link" href="#">Reply</a></body></html>""";

        var html = await RenderAsync(withOwn, Data());

        Assert.NotNull(html);
        Assert.NotNull(html);
        // Only the media-bucket floor fires — the template placed the reply link itself, so it keeps
        // its own styling and gets no appended duplicate beneath it.
        Assert.Equal(1, AppendedBars(html!));
    }

    // ---------- the media bucket floor ----------

    [Fact]
    public async Task A_design_with_no_binding_gets_a_way_into_the_bucket()
    {
        var html = await RenderAsync(BareDesign, Data());

        Assert.NotNull(html);
        Assert.Contains("Capture moments", html);
    }

    /// <summary>
    /// Both bars land INSIDE the document rather than after it, so a template's own custom
    /// properties resolve against them and an appended bar inherits the design's colours.
    /// </summary>
    [Fact]
    public async Task The_bars_go_inside_the_body()
    {
        var html = await RenderAsync(BareDesign, Data());

        Assert.NotNull(html);
        Assert.EndsWith("</body></html>", html!.TrimEnd());
    }

    /// <summary>
    /// The bars claim a z-index no template would reach for. Templates build scenery out of
    /// full-screen fixed layers — one first-party template stacks four, the topmost a curtain at
    /// z-index 9 — and an unpositioned block paints underneath every one of them, rendering
    /// perfectly and invisibly. This is the regression that behaviour already had once.
    /// </summary>
    [Fact]
    public async Task The_bars_outrank_a_templates_own_scenery()
    {
        var html = await RenderAsync(BareDesign, Data());

        Assert.NotNull(html);
        Assert.Contains("z-index:2147483000", html);
    }

    // ---------- uploaded designs stack top to bottom ----------

    /// <summary>
    /// An uploaded design centres its art with a flex row, so the reply bar used to sit beside the
    /// picture and widen the page past the phone. Pinned packages keep that CSS, so the render fixes it.
    /// </summary>
    [Fact]
    public async Task An_uploaded_design_is_stacked_so_its_bars_go_under_the_art()
    {
        var design = """<html><head><style>body{display:flex}</style></head><body><img class="art" src="design.png"></body></html>""";
        var data = Data();
        data["invitation"] = new JsonObject { ["kind"] = "static" };

        var html = (await RenderAsync(design, data))!;

        var column = html.IndexOf("body{flex-direction:column}", StringComparison.Ordinal);
        Assert.True(column > html.IndexOf("body{display:flex}", StringComparison.Ordinal), "the column rule must come after the design's own");
        Assert.True(column < html.IndexOf("</head>", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_template_invitation_is_left_as_it_was_laid_out()
    {
        var html = (await RenderAsync(BareDesign, Data()))!;
        Assert.DoesNotContain("flex-direction:column", html);
    }

    // ---------- the bar button's label ----------

    /// <summary>
    /// The label used to be the background colour, which is unreadable once a host picks their own
    /// accent (pale pink on a pale ground). With an accent chosen it is worked out from the accent.
    /// </summary>
    [Fact]
    public async Task A_chosen_accent_gets_a_readable_label()
    {
        var dark = Data();
        dark["theme"] = new JsonObject { ["accentColor"] = "#7a1f3d" };
        var light = Data();
        light["theme"] = new JsonObject { ["accentColor"] = "#f6c6d4" };

        Assert.Contains("color:#ffffff", (await RenderAsync(BareDesign, dark))!);
        Assert.Contains("color:#1a1408", (await RenderAsync(BareDesign, light))!);
    }

    [Fact]
    public async Task Without_a_chosen_accent_the_label_is_as_it_always_was()
    {
        var html = (await RenderAsync(BareDesign, Data()))!;
        Assert.Contains("color:var(--ib-bg,#17131a)", html);
    }

    /// <summary>A value that isn't a colour never reaches the style attribute.</summary>
    [Fact]
    public async Task A_theme_value_that_is_not_a_colour_is_not_used_for_the_label()
    {
        var data = Data();
        data["theme"] = new JsonObject { ["accentColor"] = "red;background:url(x)" };
        var html = (await RenderAsync(BareDesign, data))!;
        Assert.DoesNotContain("url(x)", html.Split("z-index:2147483000")[1]);
    }
}
