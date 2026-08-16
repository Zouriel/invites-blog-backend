using System.Text.Json.Nodes;
using InvitesBlog.Application.Rules;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using InvitesBlog.Infrastructure.Rendering;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>
/// What each guest actually receives. Per-role scoping resolves here — server-side — so a guest can
/// never be sent content meant for another role, and the payload is built from the campaign's FROZEN
/// manifest rather than the live template's.
/// </summary>
public class InviteRenderServiceTests
{
    private static InviteRenderService Sut() => new(new RuleEngine());

    private static Campaign Campaign(string content, string theme = "{}", string? manifest = null) => new()
    {
        Id = Guid.NewGuid(),
        Title = "Aisha & Omar",
        TemplateVersion = "1.0.0",
        Status = CampaignStatus.Draft,
        EventStartAt = new DateTimeOffset(2026, 5, 1, 18, 0, 0, TimeSpan.Zero),
        CustomContentJson = content,
        ThemeOverridesJson = theme,
        RulesJson = "{\"rules\":[]}",
        TemplateManifestJson = manifest ?? "{}"
    };

    private static Guest Guest(string? role) => new()
    {
        Id = Guid.NewGuid(), Name = "Nadia", Role = role, Gender = "female"
    };

    private static JsonObject Render(Campaign campaign, Guest guest, Template? template = null)
    {
        var invite = new Invite { Id = Guid.NewGuid(), RsvpStatus = RsvpStatus.NoResponse };
        var payload = Sut().Build(
            campaign, template ?? TestData.Template(), guest, invite,
            "https://invites.blog/i/abc", "Aisha", null, null);
        return (JsonObject)payload.Data;
    }

    private static string? At(JsonObject data, string dotPath)
    {
        JsonNode? node = data;
        foreach (var part in dotPath.Split('.'))
            node = node is JsonObject o && o.TryGetPropertyValue(part, out var next) ? next : null;
        return node?.ToString();
    }

    [Fact]
    public void A_field_with_no_roles_reaches_every_guest()
    {
        var data = Render(
            Campaign("""{"fields":{"event.title":{"value":"Our Day","roles":[]}}}"""),
            Guest("Family"));

        Assert.Equal("Our Day", At(data, "event.title"));
    }

    [Fact]
    public void A_role_scoped_field_reaches_only_that_role()
    {
        var content = """{"fields":{"event.note":{"value":"Bring the rings","roles":["Groomsmen"]}}}""";

        Assert.Equal("Bring the rings", At(Render(Campaign(content), Guest("Groomsmen")), "event.note"));
        Assert.Null(At(Render(Campaign(content), Guest("Family")), "event.note"));
        Assert.Null(At(Render(Campaign(content), Guest(null)), "event.note"));
    }

    [Fact]
    public void Role_matching_ignores_case()
    {
        var data = Render(
            Campaign("""{"fields":{"event.note":{"value":"VIP only","roles":["vip"]}}}"""),
            Guest("VIP"));

        Assert.Equal("VIP only", At(data, "event.note"));
    }

    [Fact]
    public void The_pre_scoping_flat_shape_still_renders_for_everyone()
    {
        // Campaigns saved before scoping existed store a bare value — they must keep working.
        var data = Render(Campaign("""{"fields":{"event.title":"Legacy title"}}"""), Guest("Family"));

        Assert.Equal("Legacy title", At(data, "event.title"));
    }

    [Fact]
    public void A_gallery_slot_arrives_as_a_list_not_a_string()
    {
        var data = Render(
            Campaign("""{"imageSlots":{"event.gallery":{"value":["/a.png","/b.png"],"roles":[]}}}"""),
            Guest("Family"));

        var gallery = Assert.IsType<JsonArray>(((JsonObject)data["event"]!)["gallery"]);
        Assert.Equal(2, gallery.Count);
        Assert.Equal("/a.png", gallery[0]!.ToString());
    }

    [Fact]
    public void An_empty_gallery_is_omitted_so_the_templates_fallback_shows()
    {
        var data = Render(
            Campaign("""{"imageSlots":{"event.gallery":{"value":[],"roles":[]}}}"""),
            Guest("Family"));

        Assert.Null(((JsonObject)data["event"]!)["gallery"]);
    }

    [Fact]
    public void Theme_layers_the_guests_role_over_the_shared_defaults()
    {
        var theme = """
            {"shared":{"accentColor":"#c9a227","backgroundColor":"#000"},
             "roles":{"Bride":{"accentColor":"#f2c9d4"}}}
            """;

        var bride = (JsonObject)Render(Campaign("{}", theme), Guest("Bride"))["theme"]!;
        Assert.Equal("#f2c9d4", bride["accentColor"]!.ToString());   // role override wins
        Assert.Equal("#000", bride["backgroundColor"]!.ToString());  // shared falls through

        var family = (JsonObject)Render(Campaign("{}", theme), Guest("Family"))["theme"]!;
        Assert.Equal("#c9a227", family["accentColor"]!.ToString());  // untouched by another role
    }

    [Fact]
    public void A_flat_theme_saved_before_per_role_theming_applies_to_everyone()
    {
        var theme = (JsonObject)Render(Campaign("{}", """{"accentColor":"#abc"}"""), Guest("Family"))["theme"]!;

        Assert.Equal("#abc", theme["accentColor"]!.ToString());
    }

    [Fact]
    public void Content_blocks_come_from_the_campaigns_frozen_manifest_not_the_live_template()
    {
        var template = TestData.Template();
        // The live template has since dropped the block; the campaign's frozen manifest still has it.
        template.ManifestJson = """{"contentBlocks":[]}""";
        var campaign = Campaign("{}", "{}", """{"contentBlocks":["vipSchedule"]}""");
        campaign.RulesJson =
            """{"rules":[{"condition":{"field":"role","operator":"equals","value":"VIP"},"contentBlock":"vipSchedule"}]}""";

        var data = Render(campaign, Guest("VIP"), template);

        var blocks = Assert.IsType<JsonArray>(data["resolvedBlocks"]);
        Assert.Contains(blocks, b => b!.ToString() == "vipSchedule");
    }
}
