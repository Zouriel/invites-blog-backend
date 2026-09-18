using System.Text.Json;
using System.Text.Json.Serialization;

namespace InvitesBlog.Application.Delivery;

/// <summary>
/// A campaign's <c>DeliverySettingsJson</c>, read one way for every send. The first send
/// (<c>CampaignService.FinalizeAsync</c>) and the per-guest sends (<c>DispatchService</c>: resends and
/// "add and send now") used to parse it separately, with different defaults and different
/// placeholders — so the same host message came out differently depending on which path mailed it.
/// </summary>
public sealed class DeliverySettings
{
    /// <summary>
    /// The wording used when the host never wrote their own. It sits above a button and the link
    /// itself, so it does not repeat the link.
    /// </summary>
    public const string DefaultMessageTemplate = "You're warmly invited! Tap below to open your invitation.";

    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    // "share" (the default) hands the host a link to pass on; "email" also mails each guest their own
    // /i/{token} link. See UpdateDeliverySettingsRequestValidator for what may be stored.
    [JsonPropertyName("channels")] public List<string> Channels { get; set; } = new() { "share" };
    [JsonPropertyName("fallbackChannel")] public string? FallbackChannel { get; set; }
    [JsonPropertyName("messageTemplate")] public string MessageTemplate { get; set; } = DefaultMessageTemplate;

    /// <summary>True when the host picked <paramref name="channel"/> (case-insensitive).</summary>
    public bool Uses(string channel) =>
        Channels.Any(c => string.Equals(c, channel, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The host's message for one guest. <c>{{name}}</c> / <c>{{guest.name}}</c> → the guest,
    /// <c>{{inviter.name}}</c> → the host, <c>{{invite.link}}</c> → this guest's personal link.
    /// </summary>
    public string Personalize(string guestName, string inviterName, string inviteLink) =>
        MessageTemplate
            .Replace("{{name}}", guestName)
            .Replace("{{guest.name}}", guestName)
            .Replace("{{inviter.name}}", inviterName)
            .Replace("{{invite.link}}", inviteLink);

    /// <summary>
    /// Reads the stored JSON. Anything unreadable, and any field left out or nulled, falls back to
    /// the defaults rather than failing a send.
    /// </summary>
    public static DeliverySettings Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new DeliverySettings();
        DeliverySettings? parsed;
        try { parsed = JsonSerializer.Deserialize<DeliverySettings>(json, JsonOpts); }
        catch (JsonException) { return new DeliverySettings(); }
        if (parsed is null) return new DeliverySettings();

        // An explicit null deserializes over the initializer; put the defaults back.
        parsed.Channels = (parsed.Channels ?? new List<string> { "share" })
            .Where(c => !string.IsNullOrWhiteSpace(c)).ToList();
        if (string.IsNullOrWhiteSpace(parsed.MessageTemplate)) parsed.MessageTemplate = DefaultMessageTemplate;
        return parsed;
    }
}
