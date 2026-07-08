using System.Text.Json;
using System.Text.Json.Nodes;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Rules;
using InvitesBlog.Domain.Entities;
using InvitesBlog.TemplateCompiler;

namespace InvitesBlog.Infrastructure.Rendering;

/// <summary>
/// Builds the single JSON payload injected into the sandboxed template (§5.3). Personalization
/// rules are resolved here — server-side — and only the resolved content-block list ships, so the
/// template never evaluates rules. Guest content is data, never markup.
/// </summary>
public sealed class InviteRenderService(RuleEngine ruleEngine) : IInviteRenderer
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public InviteRenderPayload Build(Campaign campaign, Template template, Guest guest, Invite invite, string inviteLink, string? inviterName, string? inviterPhone, string? inviterEmail)
    {
        var content = ParseObject(campaign.CustomContentJson);
        var venue = content["venue"] as JsonObject ?? new JsonObject();

        var eventObj = new JsonObject
        {
            ["title"] = Str(content, "title") ?? campaign.Title,
            ["subtitle"] = Str(content, "subtitle"),
            ["description"] = Str(content, "description"),
            ["date"] = Str(content, "date") ?? campaign.EventStartAt.ToString("dddd, d MMMM yyyy"),
            ["time"] = Str(content, "time") ?? campaign.EventStartAt.ToString("h:mm tt"),
            ["schedule"] = Str(content, "schedule"),
            ["dressCode"] = Str(content, "dressCode"),
            ["venue"] = new JsonObject
            {
                ["name"] = Str(venue, "name"),
                ["address"] = Str(venue, "address"),
                ["mapLink"] = Str(venue, "mapLink")
            }
        };

        var guestObj = new JsonObject
        {
            ["name"] = guest.Name,
            ["role"] = guest.Role,
            ["gender"] = guest.Gender
        };

        var manifest = JsonSerializer.Deserialize<TemplateManifest>(template.ManifestJson, JsonOpts);
        var attrs = new Dictionary<string, string?>
        {
            ["role"] = guest.Role,
            ["gender"] = guest.Gender,
            ["name"] = guest.Name,
            ["email"] = guest.Email,
            ["phone"] = guest.PhoneE164
        };
        var resolved = ruleEngine.Resolve(campaign.RulesJson, attrs, manifest?.ContentBlocks);

        var data = new JsonObject
        {
            ["event"] = eventObj,
            ["guest"] = guestObj,
            ["venue"] = (JsonNode?)eventObj["venue"]!.DeepClone(),
            ["inviter"] = new JsonObject
            {
                ["name"] = inviterName,
                ["phone"] = inviterPhone,
                ["email"] = inviterEmail
            },
            ["rsvp"] = new JsonObject
            {
                ["link"] = $"{inviteLink}/rsvp",
                ["status"] = invite.RsvpStatus.ToString()
            },
            ["invite"] = new JsonObject { ["link"] = inviteLink },
            ["theme"] = ParseObject(campaign.ThemeOverridesJson),
            ["resolvedBlocks"] = new JsonArray(resolved.Select(b => (JsonNode)b!).ToArray())
        };

        // Inviter-filled dynamic fields + images: flat { "data-var/href/src path": value } maps saved by
        // the builder. Each value is placed at its path, so any field an author adds to a template just
        // resolves — no server-side whitelist. The whitelisted event object above stays as the default.
        ApplyPathMap(data, content["fields"] as JsonObject);
        ApplyPathMap(data, content["imageSlots"] as JsonObject);

        return new InviteRenderPayload(template.PackageUrl, data, invite.RequiresOtp, campaign.Status.ToString());
    }

    /// <summary>Overlays a flat { path: value } map onto <paramref name="data"/>, each at its dot-path.</summary>
    private static void ApplyPathMap(JsonObject data, JsonObject? map)
    {
        if (map is null) return;
        foreach (var (path, node) in map)
        {
            var value = node?.ToString();
            if (!string.IsNullOrWhiteSpace(path) && !string.IsNullOrWhiteSpace(value))
                SetPath(data, path, value!);
        }
    }

    /// <summary>Assigns <paramref name="value"/> into <paramref name="root"/> at a dot-path, creating objects as needed.</summary>
    private static void SetPath(JsonObject root, string dotPath, string value)
    {
        var parts = dotPath.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return;
        var node = root;
        for (var i = 0; i < parts.Length - 1; i++)
        {
            if (node[parts[i]] is JsonObject child) { node = child; }
            else { var created = new JsonObject(); node[parts[i]] = created; node = created; }
        }
        node[parts[^1]] = value;
    }

    private static JsonObject ParseObject(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}") return new JsonObject();
        try { return JsonNode.Parse(json) as JsonObject ?? new JsonObject(); }
        catch (JsonException) { return new JsonObject(); }
    }

    private static string? Str(JsonObject o, string key)
    {
        if (!o.TryGetPropertyValue(key, out var v) || v is null) return null;
        try { return v.GetValue<string>(); }
        catch { return v.ToString(); }
    }
}
