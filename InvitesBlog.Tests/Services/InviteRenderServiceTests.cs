using System.Text.Json.Nodes;
using InvitesBlog.Application.Rules;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using InvitesBlog.Infrastructure.Rendering;
using InvitesBlog.TemplateCompiler;
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
    public void The_invite_serves_the_package_the_campaign_pinned_not_the_templates_newest()
    {
        // An approved edit moves the live Template row to the new version's package. A campaign
        // pinned to the old version — and every invite already sent from it — must keep rendering
        // the markup it was built on, not silently pick up the new one.
        var template = TestData.Template();
        template.Version = "1.0.4";
        template.PackageUrl = "https://cdn.test/templates/golden-bloom@1.0.4/";

        var campaign = Campaign("{}");
        campaign.TemplateVersion = "1.0.0";
        campaign.TemplatePackageUrl = "https://cdn.test/templates/golden-bloom@1.0.0/";

        var invite = new Invite { Id = Guid.NewGuid(), RsvpStatus = RsvpStatus.NoResponse };
        var payload = Sut().Build(
            campaign, template, Guest("Family"), invite, "https://invites.blog/i/abc", "Aisha", null, null);

        Assert.Equal("https://cdn.test/templates/golden-bloom@1.0.0/", payload.PackageUrl);
    }

    [Fact]
    public void A_campaign_created_before_the_package_was_pinned_falls_back_to_the_live_template()
    {
        var template = TestData.Template();
        template.PackageUrl = "https://cdn.test/templates/golden-bloom@1.0.0/";
        var campaign = Campaign("{}");
        campaign.TemplatePackageUrl = string.Empty;

        var invite = new Invite { Id = Guid.NewGuid(), RsvpStatus = RsvpStatus.NoResponse };
        var payload = Sut().Build(
            campaign, template, Guest("Family"), invite, "https://invites.blog/i/abc", "Aisha", null, null);

        Assert.Equal("https://cdn.test/templates/golden-bloom@1.0.0/", payload.PackageUrl);
    }

    /// <summary>
    /// Every template's photo button binds camera.link. If the payload stops carrying it the binder
    /// leaves href="#", the button goes quiet, and nothing else in the build notices — which is the
    /// whole path a guest at the party uses.
    /// </summary>
    [Fact]
    public void The_payload_carries_the_camera_and_the_gallery()
    {
        var campaign = Campaign("{}");
        campaign.EventStartAt = DateTimeOffset.UtcNow;   // tonight
        var invite = new Invite { Id = Guid.NewGuid(), RsvpStatus = RsvpStatus.Going };

        var payload = Sut().Build(
            campaign, TestData.Template(), Guest("Family"), invite,
            "https://me.invites.blog/r/abc", "Aisha", null, null);

        Assert.Equal("https://me.invites.blog/r/abc/camera", payload.Data["camera"]?["link"]?.ToString());
        Assert.Equal("https://me.invites.blog/r/abc/photos", payload.Data["photos"]?["link"]?.ToString());
    }

    /// <summary>
    /// A closed camera keeps its object but loses its link. That distinction carries real weight
    /// downstream: a template's [data-optional] wrapper hides itself because the href never
    /// resolved, and the appended bar can tell "closed for this guest" apart from "rendered before
    /// there was a camera" — which still gets the gallery.
    /// </summary>
    [Fact]
    public void A_guest_who_is_not_coming_gets_the_camera_object_with_no_link()
    {
        var campaign = Campaign("{}");
        campaign.EventStartAt = DateTimeOffset.UtcNow;
        var invite = new Invite { Id = Guid.NewGuid(), RsvpStatus = RsvpStatus.NotGoing };

        var payload = Sut().Build(
            campaign, TestData.Template(), Guest("Family"), invite,
            "https://me.invites.blog/r/abc", "Aisha", null, null);

        Assert.NotNull(payload.Data["camera"]);
        Assert.Null(payload.Data["camera"]?["link"]);
        // The gallery is unaffected — not being at the party is not a reason to be shown nothing.
        Assert.Equal("https://me.invites.blog/r/abc/photos", payload.Data["photos"]?["link"]?.ToString());
    }

    /// <summary>Coming, whenever the event happens to be.</summary>
    [Fact]
    public void The_camera_is_offered_regardless_of_the_date()
    {
        var campaign = Campaign("{}");
        campaign.EventStartAt = DateTimeOffset.UtcNow.AddDays(9);
        var invite = new Invite { Id = Guid.NewGuid(), RsvpStatus = RsvpStatus.Going };

        var payload = Sut().Build(
            campaign, TestData.Template(), Guest("Family"), invite,
            "https://me.invites.blog/r/abc", "Aisha", null, null);

        // The date no longer gates it: coming is the whole condition.
        Assert.NotNull(payload.Data["camera"]?["link"]);
    }

    // --- Theme → CSS custom properties -----------------------------------------------------------

    [Fact]
    public void Theme_overrides_are_emitted_as_the_css_properties_the_template_declared()
    {
        // The manifest is what records which custom property each theme key drives.
        const string manifest = """
            {"theme":{"keys":[
                {"key":"accentColor","cssVar":"--ib-accent","type":"color","label":"Accent","default":"#000"},
                {"key":"headingFont","cssVar":"--ib-heading-font","type":"font","label":"Heading","default":"serif"}
            ]}}
            """;
        const string theme = """{"shared":{"accentColor":"#c9a227","headingFont":"Lora"},"roles":{}}""";

        var vars = (JsonObject)Render(Campaign("{}", theme, manifest), Guest("Family"))["themeVars"]!;

        Assert.Equal("#c9a227", vars["--ib-accent"]!.ToString());
        Assert.Equal("Lora", vars["--ib-heading-font"]!.ToString());
    }

    [Fact]
    public void A_roles_theme_override_reaches_that_role_as_a_css_property()
    {
        const string manifest = """
            {"theme":{"keys":[{"key":"accentColor","cssVar":"--ib-accent","type":"color","label":"A","default":"#000"}]}}
            """;
        const string theme = """{"shared":{"accentColor":"#c9a227"},"roles":{"Bride":{"accentColor":"#f2c9d4"}}}""";

        var bride = (JsonObject)Render(Campaign("{}", theme, manifest), Guest("Bride"))["themeVars"]!;
        var family = (JsonObject)Render(Campaign("{}", theme, manifest), Guest("Family"))["themeVars"]!;

        Assert.Equal("#f2c9d4", bride["--ib-accent"]!.ToString());
        Assert.Equal("#c9a227", family["--ib-accent"]!.ToString());
    }

    [Fact]
    public void An_override_whose_key_the_manifest_no_longer_declares_falls_back_to_the_naming_convention()
    {
        // The template dropped the key in a later version; a campaign's saved override shouldn't
        // silently vanish — the documented --ib-* naming still resolves it.
        const string theme =
            """{"shared":{"accentColor":"#c9a227","backgroundColor":"#111","headingFont":"Lora"},"roles":{}}""";

        var vars = (JsonObject)Render(Campaign("{}", theme, "{}"), Guest("Family"))["themeVars"]!;

        Assert.Equal("#c9a227", vars["--ib-accent"]!.ToString());
        Assert.Equal("#111", vars["--ib-bg"]!.ToString());
        Assert.Equal("Lora", vars["--ib-heading-font"]!.ToString());
    }

    [Fact]
    public void A_campaign_with_no_overrides_sends_no_css_properties_so_the_authors_defaults_stand()
    {
        var vars = (JsonObject)Render(Campaign("{}"), Guest("Family"))["themeVars"]!;

        Assert.Empty(vars);
    }

    [Fact]
    public void The_injector_applies_theme_vars_to_the_document_root()
    {
        // Without this the whole theming step is inert — the values ship and nothing reads them.
        Assert.Contains("applyTheme(data.themeVars)", TemplateInjector.Js);
        Assert.Contains("root.style.setProperty(name, value)", TemplateInjector.Js);
        // …and only ever as a custom property, never as arbitrary CSS.
        Assert.Contains("name.indexOf('--') !== 0", TemplateInjector.Js);
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

    // --- Date and time fields are stored in a machine format and must not reach a guest that way ---

    private const string DateTimeManifest = """
        {"fields":[
          {"key":"event.date","label":"Date","type":"date"},
          {"key":"event.time","label":"Time","type":"time"},
          {"key":"event.note","label":"Note","type":"text"}
        ]}
        """;

    [Fact]
    public void A_date_field_is_shown_the_way_the_fallback_would_have_shown_it()
    {
        // The builder's date picker stores 2026-08-28. Because a saved field OVERWRITES the formatted
        // default built from EventStartAt, that raw value used to be what the guest actually read.
        var campaign = Campaign(
            """{"fields":{"event.date":"2026-08-28","event.time":"22:00"}}""",
            manifest: DateTimeManifest);

        var data = Render(campaign, Guest("guest"));

        Assert.Equal("Friday, 28 August 2026", At(data, "event.date"));
        Assert.Equal("10:00 PM", At(data, "event.time"));
    }

    [Fact]
    public void A_field_that_is_not_a_date_is_left_exactly_as_entered()
    {
        var campaign = Campaign(
            """{"fields":{"event.note":"2026-08-28 was the day we met"}}""",
            manifest: DateTimeManifest);

        Assert.Equal("2026-08-28 was the day we met", At(Render(campaign, Guest("guest")), "event.note"));
    }

    [Fact]
    public void A_date_that_cannot_be_parsed_is_passed_through_rather_than_blanked()
    {
        // An unreadable date still beats an empty line where the date should be.
        var campaign = Campaign(
            """{"fields":{"event.date":"the last Friday in August"}}""",
            manifest: DateTimeManifest);

        Assert.Equal("the last Friday in August", At(Render(campaign, Guest("guest")), "event.date"));
    }

    [Fact]
    public void A_field_the_manifest_does_not_declare_is_left_alone()
    {
        // Fields outside the manifest still resolve — that is the point of the path map — they just
        // get no formatting, because nothing said what kind of value they hold.
        var campaign = Campaign(
            """{"fields":{"event.somethingNew":"2026-08-28"}}""",
            manifest: DateTimeManifest);

        Assert.Equal("2026-08-28", At(Render(campaign, Guest("guest")), "event.somethingNew"));
    }
}
