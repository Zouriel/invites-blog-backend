using System.Text.Json;
using System.Text.Json.Serialization;

namespace InvitesBlog.Application.Rules;

public sealed class RuleCondition
{
    [JsonPropertyName("field")] public string Field { get; set; } = default!;
    [JsonPropertyName("operator")] public string Operator { get; set; } = "equals";
    [JsonPropertyName("value")] public JsonElement Value { get; set; }
}

public sealed class PersonalizationRule
{
    [JsonPropertyName("condition")] public RuleCondition Condition { get; set; } = default!;
    [JsonPropertyName("contentBlock")] public string ContentBlock { get; set; } = default!;
}

public sealed class RuleSet
{
    [JsonPropertyName("rules")] public List<PersonalizationRule> Rules { get; set; } = new();
}

/// <summary>
/// Evaluates the §12 JSON personalization rules server-side (never executable code, §5.3).
/// Given a guest's attributes it returns the set of content-block ids that should be rendered.
/// The resolved list — and only the resolved list — ships to the sandboxed template.
/// Neutral/default blocks (those no rule gates) are always included so <c>unspecified</c>
/// guests always see a complete invite (§12.4).
/// </summary>
public sealed class RuleEngine
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Resolve the visible content blocks for a guest.
    /// <paramref name="allBlocks"/> is the template manifest's full block list; blocks that no
    /// rule references are treated as neutral defaults and always shown.
    /// </summary>
    public IReadOnlyList<string> Resolve(
        string rulesJson,
        IReadOnlyDictionary<string, string?> guestAttributes,
        IReadOnlyCollection<string>? allBlocks = null)
    {
        var ruleSet = Parse(rulesJson);
        var gatedBlocks = ruleSet.Rules.Select(r => r.ContentBlock).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var resolved = new List<string>();

        // Neutral defaults: any manifest block not gated by a rule always renders.
        if (allBlocks is not null)
        {
            foreach (var block in allBlocks)
                if (!gatedBlocks.Contains(block))
                    resolved.Add(block);
        }

        foreach (var rule in ruleSet.Rules)
        {
            if (Matches(rule.Condition, guestAttributes) && !resolved.Contains(rule.ContentBlock))
                resolved.Add(rule.ContentBlock);
        }

        return resolved;
    }

    public RuleSet Parse(string rulesJson)
    {
        if (string.IsNullOrWhiteSpace(rulesJson)) return new RuleSet();
        try
        {
            return JsonSerializer.Deserialize<RuleSet>(rulesJson, JsonOptions) ?? new RuleSet();
        }
        catch (JsonException)
        {
            return new RuleSet();
        }
    }

    private static bool Matches(RuleCondition condition, IReadOnlyDictionary<string, string?> attrs)
    {
        var actual = attrs.TryGetValue(condition.Field, out var v) ? v : null;

        switch (condition.Operator.ToLowerInvariant())
        {
            case "exists":
                return !string.IsNullOrEmpty(actual);
            case "notexists":
                return string.IsNullOrEmpty(actual);
            case "equals":
                return StringEquals(actual, ValueAsString(condition.Value));
            case "notequals":
                return !StringEquals(actual, ValueAsString(condition.Value));
            case "in":
                return ValueAsList(condition.Value).Any(x => StringEquals(actual, x));
            case "notin":
                return !ValueAsList(condition.Value).Any(x => StringEquals(actual, x));
            default:
                return false;
        }
    }

    private static bool StringEquals(string? a, string? b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    private static string? ValueAsString(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Number => value.ToString(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => null
    };

    private static IEnumerable<string> ValueAsList(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                var s = ValueAsString(item);
                if (s is not null) yield return s;
            }
        }
        else
        {
            var s = ValueAsString(value);
            if (s is not null) yield return s;
        }
    }
}
