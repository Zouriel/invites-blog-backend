using System.Text.Json.Nodes;
using InvitesBlog.Application.Campaigns;
using InvitesBlog.TemplateCompiler;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>What a guest is asked to wear: saved per role, chosen per guest, drawn into the invitation.</summary>
public class DressColorsTests
{
    private const string RolesJson = """
        {"roles":[
          {"name":"Groom family male","contentBlocks":[],"palette":["#1F3A5F","#2e5283"]},
          {"name":"Groom family female","contentBlocks":[],"palette":["#c9a227","not-a-colour","#e8d48b"]},
          {"name":"Friends","contentBlocks":[]}
        ]}
        """;

    [Fact]
    public void Clean_keeps_only_hex_colours_lowercased_without_repeats_and_caps_the_count()
    {
        var cleaned = DressColors.Clean(new[]
        {
            "#AABBCC", "#aabbcc", "red", "#123", null, " #112233 ", "#010101", "#020202", "#030303", "#040404", "#050505"
        });

        Assert.Equal(new[] { "#aabbcc", "#112233", "#010101", "#020202", "#030303", "#040404" }, cleaned);
    }

    [Fact]
    public void A_household_invited_as_two_roles_gets_both_palettes_in_its_role_order()
    {
        var palettes = DressColors.ForGuest(RolesJson, new[] { "groom family female", "Groom family male" });

        Assert.Equal(2, palettes.Count);
        Assert.Equal("groom family female", palettes[0]!["role"]!.ToString());
        Assert.Equal(new[] { "#c9a227", "#e8d48b" }, ((JsonArray)palettes[0]!["colors"]!).Select(c => c!.ToString()));
        Assert.Equal(new[] { "#1f3a5f", "#2e5283" }, ((JsonArray)palettes[1]!["colors"]!).Select(c => c!.ToString()));
    }

    [Fact]
    public void A_role_with_no_palette_and_a_guest_with_no_roles_get_nothing()
    {
        Assert.Empty(DressColors.ForGuest(RolesJson, new[] { "Friends" }));
        Assert.Empty(DressColors.ForGuest(RolesJson, Array.Empty<string>()));
        Assert.Empty(DressColors.ForGuest("not json", new[] { "Friends" }));
    }

    [Fact]
    public void The_binder_fills_a_designers_spot_and_hides_it_when_there_is_nothing_to_wear()
    {
        const string html = "<html><body><div id=\"wear\" data-dress-colors></div></body></html>";

        var withColours = new JsonObject
        {
            ["dressColors"] = DressColors.ForGuest(RolesJson, new[] { "Groom family male" })
        };
        var filled = ServerBinder.Bind(html, withColours);
        Assert.Contains("class=\"ib-dress__swatch\"", filled);
        Assert.Contains("background:#1f3a5f", filled);
        // One role: no role heading, the swatches speak for themselves.
        Assert.DoesNotContain("ib-dress__role", filled);

        var empty = ServerBinder.Bind(html, new JsonObject { ["dressColors"] = new JsonArray() });
        Assert.Contains("display:none", empty);
    }

    [Fact]
    public void Role_names_are_encoded_when_several_roles_are_shown()
    {
        var palettes = new JsonArray(
            new JsonObject { ["role"] = "<b>A</b>", ["colors"] = new JsonArray("#111111") },
            new JsonObject { ["role"] = "B", ["colors"] = new JsonArray("#222222") });

        var html = ServerBinder.DressColorsHtml(palettes, withInlineStyle: false);

        Assert.Contains("&lt;b&gt;A&lt;/b&gt;", html);
        Assert.DoesNotContain("<b>A</b>", html);
    }
}
