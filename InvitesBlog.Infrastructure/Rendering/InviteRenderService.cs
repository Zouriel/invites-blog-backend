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

        // The campaign's FROZEN manifest, not the live template's — a template edited since this
        // campaign was created must not change what its guests receive.
        var manifestJson = string.IsNullOrWhiteSpace(campaign.TemplateManifestJson)
                           || campaign.TemplateManifestJson.Trim() is "{}"
            ? template.ManifestJson
            : campaign.TemplateManifestJson;
        var manifest = JsonSerializer.Deserialize<TemplateManifest>(manifestJson, JsonOpts);
        var attrs = new Dictionary<string, string?>
        {
            ["role"] = guest.Role,
            ["gender"] = guest.Gender,
            ["name"] = guest.Name,
            ["email"] = guest.Email,
            ["phone"] = guest.PhoneE164
        };
        var resolved = ruleEngine.Resolve(campaign.RulesJson, attrs, manifest?.ContentBlocks);
        var theme = ResolveTheme(campaign.ThemeOverridesJson, guest.Role);

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
            ["theme"] = theme,
            // The same overrides keyed by the CSS custom property each one drives, which is what the
            // injector actually sets on the document. Resolved here because the manifest — the only
            // place a theme key's cssVar is recorded — lives server-side.
            ["themeVars"] = ThemeVars(theme, manifest),
            ["resolvedBlocks"] = new JsonArray(resolved.Select(b => (JsonNode)b!).ToArray())
        };

        // Inviter-filled dynamic fields + images, keyed by their data-var/href/src path. Each value is
        // placed at its path, so any field an author adds to a template just resolves — no server-side
        // whitelist. The whitelisted event object above stays as the default.
        ApplyPathMap(data, content["fields"] as JsonObject, guest.Role);
        ApplyPathMap(data, content["imageSlots"] as JsonObject, guest.Role);

        return new InviteRenderPayload(template.PackageUrl, data, invite.RequiresOtp, campaign.Status.ToString());
    }

    /// <summary>
    /// Overlays a { path: value } map onto <paramref name="data"/>, each at its dot-path. An entry is
    /// either a bare value (applies to everyone — the shape saved before per-role scoping existed) or
    /// <c>{ "value": …, "roles": [...] }</c>, in which case an empty/absent role list still means
    /// everyone and a populated one means only those roles. A value may be an array (a gallery slot).
    /// </summary>
    private static void ApplyPathMap(JsonObject data, JsonObject? map, string? guestRole)
    {
        if (map is null) return;
        foreach (var (path, node) in map)
        {
            if (string.IsNullOrWhiteSpace(path) || node is null) continue;

            var (value, roles) = node is JsonObject scoped && scoped.ContainsKey("value")
                ? (scoped["value"], scoped["roles"] as JsonArray)
                : (node, null);

            if (value is null || !AppliesTo(roles, guestRole)) continue;

            // A gallery slot keeps its array so the template can repeat over it; anything else is text.
            if (value is JsonArray gallery)
            {
                if (gallery.Count > 0) SetPath(data, path, gallery.DeepClone());
                continue;
            }

            var text = value.ToString();
            if (!string.IsNullOrWhiteSpace(text)) SetPath(data, path, text);
        }
    }

    /// <summary>An empty or absent role list means "everyone"; otherwise the guest's role must be listed.</summary>
    private static bool AppliesTo(JsonArray? roles, string? guestRole)
    {
        if (roles is null || roles.Count == 0) return true;
        return roles.Any(r => string.Equals(r?.ToString(), guestRole, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The theme this guest sees: the shared overrides, with their role's overrides layered on top.
    /// A flat object (the shape saved before per-role theming) is treated entirely as shared.
    /// </summary>
    private static JsonObject ResolveTheme(string themeJson, string? guestRole)
    {
        var theme = ParseObject(themeJson);
        if (theme["shared"] is not JsonObject && theme["roles"] is not JsonObject) return theme;

        var resolved = theme["shared"] is JsonObject shared
            ? shared.DeepClone().AsObject()
            : new JsonObject();

        if (!string.IsNullOrWhiteSpace(guestRole) && theme["roles"] is JsonObject byRole)
        {
            var match = byRole.FirstOrDefault(kv =>
                string.Equals(kv.Key, guestRole, StringComparison.OrdinalIgnoreCase)).Value;
            if (match is JsonObject overrides)
                foreach (var (key, value) in overrides)
                    if (value is not null) resolved[key] = value.DeepClone();
        }

        return resolved;
    }

    /// <summary>
    /// Maps the resolved theme onto the CSS custom properties it drives, e.g.
    /// <c>accentColor</c> → <c>--ib-accent</c>. The manifest is authoritative (it records each key's
    /// <c>cssVar</c> as the template declared it); a key the manifest doesn't know — an override left
    /// over from an earlier version of the template — falls back to the documented naming convention
    /// rather than being silently dropped.
    /// </summary>
    private static JsonObject ThemeVars(JsonObject theme, TemplateManifest? manifest)
    {
        var vars = new JsonObject();
        var declared = manifest?.Theme?.Keys?
            .Where(k => !string.IsNullOrWhiteSpace(k.Key) && !string.IsNullOrWhiteSpace(k.CssVar))
            .ToDictionary(k => k.Key, k => k.CssVar, StringComparer.OrdinalIgnoreCase);

        foreach (var (key, node) in theme)
        {
            var value = node?.ToString();
            if (string.IsNullOrWhiteSpace(value)) continue;

            var cssVar = declared is not null && declared.TryGetValue(key, out var declaredVar)
                ? declaredVar
                : ConventionalCssVar(key);
            if (cssVar is not null) vars[cssVar] = value;
        }

        return vars;
    }

    /// <summary>The documented fallback naming: <c>accentColor</c>/<c>backgroundColor</c>/<c>textColor</c>
    /// map to the three required properties, anything else camelCase → <c>--ib-kebab-case</c>.</summary>
    private static string? ConventionalCssVar(string key) => key switch
    {
        "accentColor" => "--ib-accent",
        "backgroundColor" => "--ib-bg",
        "textColor" => "--ib-text",
        _ => string.IsNullOrWhiteSpace(key)
            ? null
            : "--ib-" + string.Concat(key.Select(c => char.IsUpper(c) ? "-" + char.ToLowerInvariant(c) : c.ToString()))
    };

    /// <summary>Assigns <paramref name="value"/> into <paramref name="root"/> at a dot-path, creating objects as needed.</summary>
    private static void SetPath(JsonObject root, string dotPath, JsonNode value)
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
