using System.Text.Json;
using System.Text.Json.Nodes;

namespace InvitesBlog.Application.Campaigns;

/// <summary>
/// The picture that stands for a campaign anywhere it is listed rather than opened — the tile in
/// someone's invitations grid, today.
///
/// <para><b>Why it is not the template's preview.</b> That was the first attempt and it was actively
/// wrong: a template's preview is a marketing poster rendered from the template's own demo content,
/// so every Gilded Hour invitation in the grid showed a stranger's name — "AMELIA" — over somebody
/// else's birthday. A poster identifies the TEMPLATE. A tile has to identify the EVENT.</para>
///
/// <para>So the cover is the host's own choice, kept on the campaign rather than in the manifest's
/// image slots. It is deliberately NOT a template slot: templates neither declare it nor render it,
/// and putting it among the slots would feed it to the binder and hand the manifest a key it has
/// never heard of.</para>
/// </summary>
public static class CampaignCover
{
    /// <summary>Where the URL lives inside a campaign's <c>CustomContentJson</c>.</summary>
    public const string Key = "coverImageUrl";

    /// <summary>
    /// The campaign's own cover, or null when the host hasn't chosen one. Callers fall back to the
    /// template preview — which is honest as a "what does this design look like" placeholder, and
    /// only misleading when it is allowed to masquerade as the event itself.
    /// </summary>
    public static string? Read(string? customContentJson)
    {
        if (string.IsNullOrWhiteSpace(customContentJson)) return null;

        try
        {
            // Indexing a JsonNode that is not an object THROWS rather than returning null, so the
            // shape is checked instead of assumed: content is written by the builder, has been
            // through several shapes, and a campaign whose content is an array would otherwise take
            // down the whole listing it appears in.
            if (JsonNode.Parse(customContentJson) is not JsonObject content) return null;

            var url = content[Key]?.ToString();
            return string.IsNullOrWhiteSpace(url) ? null : url;
        }
        catch (JsonException)
        {
            // Not parseable at all. Same answer: a campaign still has to list, so this is "no cover",
            // never an exception.
            return null;
        }
    }

    /// <summary>
    /// Returns <paramref name="customContentJson"/> with the cover set, or removed when
    /// <paramref name="url"/> is null. Everything else in the document is preserved untouched — this
    /// is called from a screen that has not loaded the rest of the content and must not overwrite it.
    /// </summary>
    public static string Write(string? customContentJson, string? url)
    {
        JsonObject content;
        try
        {
            content = string.IsNullOrWhiteSpace(customContentJson)
                ? new JsonObject()
                : JsonNode.Parse(customContentJson) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            content = new JsonObject();
        }

        if (string.IsNullOrWhiteSpace(url)) content.Remove(Key);
        else content[Key] = url;

        return content.ToJsonString();
    }
}
