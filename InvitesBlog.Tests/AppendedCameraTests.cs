using System.Text;
using System.Text.Json.Nodes;
using InvitesBlog.Api.Rendering;
using InvitesBlog.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>
/// The camera a template gets when it declares none of its own.
///
/// <para>Run against a real uploaded template — red-curtain-2, captured from what is actually served
/// — because that is the case this floor exists for, and the one the templates in this repository
/// cannot stand in for. They all declare their own binding, so every test that uses them proves the
/// opposite branch.</para>
/// </summary>
public class AppendedCameraTests
{
    private static string Published()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "InvitesBlog.Tests", "red-curtain-2-published.html");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("red-curtain-2-published.html not found from " + AppContext.BaseDirectory);
    }

    private static async Task<string> RenderAsync(JsonObject data)
    {
        var storage = Substitute.For<IStorageService>();
        storage.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Encoding.UTF8.GetBytes(Published()));

        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Urls:AssetsBase"] = "/assets" })
            .Build();

        return await new RenderedInvitations(storage, config)
            .BuildAsync("/assets/templates/red-curtain-2@1.0.0/", data, default)
            ?? throw new InvalidOperationException("nothing rendered");
    }

    private static JsonObject Payload(JsonObject camera) => new()
    {
        ["event"] = new JsonObject { ["title"] = "Test invitation 104" },
        ["camera"] = camera,
        ["photos"] = new JsonObject { ["link"] = "/r/abc/photos" },
        ["rsvp"] = new JsonObject { ["link"] = null, ["label"] = "" },
    };

    /// <summary>
    /// The exemption, read from the file production actually ships, for the id it actually names.
    /// Everything else here injects the link; this is the only test that lets the RULE decide, which
    /// is where a mistyped key or a stray character would hide.
    /// </summary>
    [Fact]
    public void The_shipped_configuration_exempts_the_campaign_it_names()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        string? settings = null;
        while (dir is not null && settings is null)
        {
            var candidate = Path.Combine(dir.FullName, "InvitesBlog.Api", "appsettings.json");
            if (File.Exists(candidate)) settings = candidate;
            dir = dir.Parent;
        }
        Assert.NotNull(settings);

        var config = new ConfigurationBuilder().AddJsonFile(settings!).Build();
        var listed = config["Camera:IgnoreDateForCampaigns"];
        Assert.False(string.IsNullOrWhiteSpace(listed), "no campaign is exempt in the shipped settings");

        var ids = listed!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(e => Guid.TryParse(e, out var id) ? id : Guid.Empty)
            .ToList();

        Assert.DoesNotContain(Guid.Empty, ids);   // every entry parses
        Assert.Contains(Guid.Parse("cfb3617e-de5a-4f0c-802f-d7a0ffa5603c"), ids);
    }

    /// <summary>This template declares no camera of its own, so it must be given one.</summary>
    [Fact]
    public async Task An_uploaded_template_with_no_binding_is_given_a_camera()
    {
        var html = await RenderAsync(Payload(new JsonObject { ["link"] = "/r/abc/camera" }));

        Assert.Contains("Capture moments", html);
        Assert.Contains("href=\"/r/abc/camera\"", html);
        // Inside the document, not after it.
        Assert.True(
            html.LastIndexOf("Capture moments", StringComparison.Ordinal)
            < html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase),
            "the camera was appended outside <body>");
    }

    /// <summary>
    /// It has to paint above the template's scenery.
    ///
    /// <para>Templates build their backdrops from full-screen position:fixed layers — this one has
    /// four, topping out at a velvet curtain on z-index 9 — and an unpositioned block paints under
    /// every one of them. The section rendered correctly and was invisible for exactly that reason,
    /// which no assertion about the markup would have caught.</para>
    /// </summary>
    [Fact]
    public async Task The_appended_camera_paints_above_the_templates_fixed_layers()
    {
        var html = await RenderAsync(Payload(new JsonObject { ["link"] = "/r/abc/camera" }));

        var section = System.Text.RegularExpressions.Regex.Match(
            html, @"<section[^>]*Capture|<section[^>]*>(?=(?:(?!</section>).)*Capture moments)",
            System.Text.RegularExpressions.RegexOptions.Singleline).Value;
        Assert.NotEqual(string.Empty, section);
        Assert.Contains("position:relative", section);

        var z = System.Text.RegularExpressions.Regex.Match(section, @"z-index:(\d+)");
        Assert.True(z.Success, "the appended camera declares no stacking order");

        // Higher than anything this template — or any sane template — reaches for.
        var declared = System.Text.RegularExpressions.Regex.Matches(Published(), @"z-index:\s*(\d+)")
            .Select(m => int.Parse(m.Groups[1].Value))
            .DefaultIfEmpty(0)
            .Max();
        Assert.True(
            int.Parse(z.Groups[1].Value) > declared,
            $"camera z-index {z.Groups[1].Value} does not clear the template's {declared}");
    }

    /// <summary>A camera closed for this guest adds nothing at all.</summary>
    [Fact]
    public async Task A_closed_camera_appends_nothing()
    {
        var html = await RenderAsync(Payload(new JsonObject()));

        Assert.DoesNotContain("Capture moments", html);
    }

    /// <summary>
    /// The RSVP control this template ships has no [data-optional] wrapper. Withholding its link
    /// must still take it off the page rather than leave a button that scrolls to the top.
    /// </summary>
    [Fact]
    public async Task Its_wrapperless_rsvp_button_is_taken_away_when_withheld()
    {
        var html = await RenderAsync(Payload(new JsonObject { ["link"] = "/r/abc/camera" }));

        var anchor = System.Text.RegularExpressions.Regex.Match(
            html, @"<a[^>]*data-href=""rsvp\.link""[^>]*>").Value;

        Assert.NotEqual(string.Empty, anchor);
        Assert.Contains("display:none", anchor);
    }
}
