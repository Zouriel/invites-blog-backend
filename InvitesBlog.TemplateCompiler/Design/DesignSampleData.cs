using System.Text;
using System.Text.Json.Nodes;

namespace InvitesBlog.TemplateCompiler.Design;

/// <summary>
/// The invitation the editor previews with. Shaped exactly like the render payload
/// (<c>InviteRenderService</c>) so the preview goes through the real <see cref="ServerBinder"/> — the
/// designer sees what a guest would, including elements that disappear when a value is missing.
/// </summary>
public static class DesignSampleData
{
    public const string Filled = "filled";
    public const string Empty = "empty";
    public const string TwoRoles = "roles";

    /// <param name="mode">filled | empty (only what's always present) | roles (a guest holding two roles).</param>
    /// <param name="blocks">Blocks to show; null shows every block the scene uses.</param>
    public static JsonObject Build(DesignScene scene, string mode, IReadOnlyCollection<string>? blocks = null)
    {
        var empty = mode == Empty;
        var roles = scene.Roles.Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
        var guestRoles = mode == TwoRoles && roles.Count >= 2 ? roles.Take(2).ToList() : roles.Take(1).ToList();

        string? Sample(string path) => empty ? null : DesignCatalog.FindVariable(path)?.Sample;

        var eventObj = new JsonObject
        {
            ["title"] = DesignCatalog.FindVariable("event.title")!.Sample, // a title always exists
            ["subtitle"] = Sample("event.subtitle"),
            ["description"] = Sample("event.description"),
            ["date"] = Sample("event.date"),
            ["time"] = Sample("event.time"),
            ["schedule"] = Sample("event.schedule"),
            ["dressCode"] = Sample("event.dressCode"),
            ["hashtag"] = Sample("event.hashtag"),
            ["venue"] = new JsonObject
            {
                ["name"] = Sample("event.venue.name"),
                ["address"] = Sample("event.venue.address"),
                ["mapLink"] = empty ? null : "https://maps.example.com",
            },
        };

        var photos = new JsonArray();
        if (!empty)
            for (var i = 0; i < 6; i++) photos.Add(Placeholder(i, i + 1));

        // Every image slot the scene declares gets a placeholder, so a layout is judged with pictures in it.
        foreach (var (el, _, _) in scene.Walk())
        {
            if (el.Type != "slot" || el.Slot is null || empty) continue;
            var key = el.Slot.Path.StartsWith("event.", StringComparison.Ordinal) ? el.Slot.Path[6..] : null;
            if (key is null || key.Contains('.')) continue;
            // A gallery, or one photo out of it: either way the preview gets the whole numbered set.
            eventObj[key] = el.Slot.Multiple || el.Slot.Index is > 0 ? photos.DeepClone() : Placeholder(key.Length);
        }

        foreach (var field in scene.Fields)
        {
            if (!field.Path.StartsWith("event.", StringComparison.Ordinal)) continue;
            var key = field.Path[6..];
            if (key.Contains('.') || empty) continue;
            eventObj[key] = field.Type switch
            {
                "url" => "https://example.com",
                "select" => field.Options?.FirstOrDefault() ?? field.Label,
                _ => string.IsNullOrWhiteSpace(field.Sample) ? field.Label : field.Sample,
            };
        }

        var usedBlocks = scene.Walk()
            .Select(w => w.Element.Block)
            .Where(b => !string.IsNullOrWhiteSpace(b))
            .Select(b => DesignCompiler.Slug(b!))
            .Distinct()
            .ToList();
        var shown = blocks is null ? usedBlocks : usedBlocks.Where(blocks.Contains).ToList();

        var dress = new JsonArray();
        if (!empty)
        {
            var palettes = guestRoles.Count == 0 ? ["Guests"] : guestRoles;
            string[][] swatches = [["#1b3d59", "#d4eef8", "#f3eed8"], ["#6a97c0", "#152026", "#b3d5f1"]];
            for (var i = 0; i < palettes.Count; i++)
                dress.Add(new JsonObject
                {
                    ["role"] = palettes[i],
                    ["colors"] = new JsonArray(swatches[i % 2].Select(c => (JsonNode?)JsonValue.Create(c)).ToArray()),
                });
        }

        return new JsonObject
        {
            ["event"] = eventObj,
            ["guest"] = new JsonObject
            {
                ["name"] = DesignCatalog.FindVariable("guest.name")!.Sample,
                ["role"] = guestRoles.FirstOrDefault(),
                ["roles"] = new JsonArray(guestRoles.Select(r => (JsonNode?)JsonValue.Create(r)).ToArray()),
                ["gender"] = null,
            },
            ["venue"] = eventObj["venue"]!.DeepClone(),
            ["inviter"] = new JsonObject
            {
                ["name"] = Sample("inviter.name"),
                ["phone"] = Sample("inviter.phone"),
                ["email"] = Sample("inviter.email"),
            },
            ["rsvp"] = new JsonObject { ["link"] = "#rsvp", ["label"] = "Reply now", ["status"] = "Pending" },
            ["invite"] = new JsonObject { ["link"] = "#invite" },
            ["invitation"] = new JsonObject { ["kind"] = "dynamic" },
            ["photos"] = new JsonObject { ["link"] = "#photos" },
            ["camera"] = empty ? new JsonObject() : new JsonObject { ["link"] = "#camera" },
            ["theme"] = new JsonObject(),
            ["themeVars"] = new JsonObject(),
            ["resolvedBlocks"] = new JsonArray(shown.Select(b => (JsonNode?)JsonValue.Create(b)).ToArray()),
            ["dressColors"] = dress,
        };
    }

    /// <summary>A soft gradient card that reads as "a photo goes here" — inline, so the preview needs no network.</summary>
    public static string Placeholder(int seed, int? number = null)
    {
        string[][] pairs = [["#b3d5f1", "#1b3d59"], ["#f3eed8", "#6a97c0"], ["#d4eef8", "#152026"], ["#6a97c0", "#f3eed8"]];
        var p = pairs[Math.Abs(seed) % pairs.Length];
        var svg = $"""<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 400 400"><defs><linearGradient id="g" x1="0" y1="0" x2="1" y2="1"><stop offset="0" stop-color="{p[0]}"/><stop offset="1" stop-color="{p[1]}"/></linearGradient></defs><rect width="400" height="400" fill="url(#g)"/><circle cx="140" cy="150" r="42" fill="#ffffff" fill-opacity=".45"/><path d="M0 330 L120 220 L220 300 L300 240 L400 320 L400 400 L0 400Z" fill="#ffffff" fill-opacity=".35"/>{(number is { } k ? $"<text x=\"200\" y=\"250\" font-family=\"Georgia,serif\" font-size=\"150\" text-anchor=\"middle\" fill=\"#ffffff\" fill-opacity=\".85\">{k}</text>" : "")}</svg>""";
        return "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg));
    }
}
