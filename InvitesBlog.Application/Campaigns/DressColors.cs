using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace InvitesBlog.Application.Campaigns;

/// <summary>
/// The dress colours each role is asked to wear, saved with the role on the Roles step.
///
/// <para>Kept inside <c>RolesJson</c> rather than in a table of its own: a palette means nothing
/// without its role, is always read and written with it, and this way needs no migration.</para>
/// </summary>
public static partial class DressColors
{
    /// <summary>More than this is a mistake, not a dress code, and would overflow the swatch row.</summary>
    public const int MaxPerRole = 6;

    [GeneratedRegex("^#[0-9a-fA-F]{6}$")]
    private static partial Regex Hex();

    /// <summary>Only well-formed <c>#rrggbb</c> values, lowercased, in order, without repeats.</summary>
    public static List<string> Clean(IEnumerable<string?>? palette) =>
        (palette ?? Array.Empty<string?>())
            .Select(c => c?.Trim().ToLowerInvariant())
            .Where(c => c is not null && Hex().IsMatch(c))
            .Select(c => c!)
            .Distinct()
            .Take(MaxPerRole)
            .ToList();

    /// <summary>
    /// The palettes for the roles a guest holds, in the guest's role order, skipping roles with none.
    /// Each entry is <c>{ role, colors: [...] }</c>, the shape the invitation payload carries.
    /// </summary>
    public static JsonArray ForGuest(string? rolesJson, IReadOnlyList<string> guestRoles)
    {
        var result = new JsonArray();
        if (guestRoles.Count == 0 || string.IsNullOrWhiteSpace(rolesJson)) return result;

        JsonArray? defined;
        try { defined = JsonNode.Parse(rolesJson)?["roles"] as JsonArray; }
        catch (JsonException) { return result; }
        if (defined is null) return result;

        foreach (var role in guestRoles)
        {
            var match = defined.FirstOrDefault(r =>
                string.Equals(r?["name"]?.ToString(), role, StringComparison.OrdinalIgnoreCase));
            if (match?["palette"] is not JsonArray palette) continue;

            var colors = Clean(palette.Select(c => c?.ToString()));
            if (colors.Count == 0) continue;

            result.Add(new JsonObject
            {
                ["role"] = role,
                ["colors"] = new JsonArray(colors.Select(c => (JsonNode?)JsonValue.Create(c)).ToArray())
            });
        }
        return result;
    }
}
