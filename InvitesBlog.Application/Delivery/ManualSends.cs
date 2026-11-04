using InvitesBlog.Application.Security;
using InvitesBlog.Domain.Entities;

namespace InvitesBlog.Application.Delivery;

/// <summary>
/// The invitations a host sends by hand from their own phone: WhatsApp, Viber or Telegram. The
/// server never sends these; it prepares the guest's personal link and the host's message, and
/// records that the host opened the app ("whatsapp-manual" and so on) so every device shows it.
///
/// <para>Automatic invitations go by email only. Historic rows with the channel "viber" are from
/// the automatic Viber sender that was removed; they stay in the database and read as "viber"
/// (clients label them "Viber (old)").</para>
/// </summary>
public static class ManualSends
{
    /// <summary>The apps a host can send from, as the API spells them.</summary>
    public static readonly IReadOnlyList<string> Apps = ["whatsapp", "viber", "telegram"];

    public const string Suffix = "-manual";

    /// <summary>The delivery channel recorded for a hand send from <paramref name="app"/>.</summary>
    public static string ChannelFor(string app) => app + Suffix;

    /// <summary>"whatsapp" for "whatsapp-manual"; null for any other channel.</summary>
    public static string? AppOf(string channel) =>
        channel.EndsWith(Suffix, StringComparison.Ordinal) ? channel[..^Suffix.Length] : null;

    public static bool IsManual(string channel) => channel.EndsWith(Suffix, StringComparison.Ordinal);

    /// <summary>The app named by a request, lowercased, or null when it isn't one of <see cref="Apps"/>.</summary>
    public static string? ParseApp(string? app)
    {
        var a = app?.Trim().ToLowerInvariant();
        return a is not null && Apps.Contains(a) ? a : null;
    }

    /// <summary>
    /// The guest's share link and the host's message with it. The message is the host's own wording
    /// (the campaign's delivery settings, the same placeholders the email uses); when it has no
    /// <c>{{invite.link}}</c> the link goes on its own line at the end. Raw text: the client encodes it
    /// for the app's URL (wa.me ?text=, t.me/share ?text=, viber://forward ?text=).
    /// </summary>
    public static (string Link, string Text) Compose(
        Guest guest, Guid inviteId, DeliverySettings settings, string inviteeBase, InviteShareLinks shareLinks, string? inviterName)
    {
        var link = $"{inviteeBase}/i/{shareLinks.TokenFor(inviteId)}";
        var host = string.IsNullOrWhiteSpace(inviterName) ? InviteLetter.FallbackInviterName : inviterName.Trim();
        var guestName = string.IsNullOrWhiteSpace(guest.Name) ? "there" : guest.Name.Trim();
        var text = settings.Personalize(guestName, host, link);
        if (!settings.MessageTemplate.Contains("{{invite.link}}", StringComparison.Ordinal))
            text = $"{text}\n\n{link}";
        return (link, text);
    }
}
