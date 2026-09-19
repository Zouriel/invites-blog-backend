using InvitesBlog.Application.Abstractions;
using InvitesBlog.Domain.Entities;

namespace InvitesBlog.Application.Delivery;

/// <summary>
/// Everything one guest's invitation says, whichever path sends it: the personal link, the
/// data-removal link, and the host's message with its placeholders filled in.
///
/// <para>The first send and a resend used to build this separately — the first send with its own
/// HTML that had no "Remove my data" link at all, which §15.2 requires on every invitation email.
/// Both now compose the letter here and hand it to the same <see cref="IInviteDeliveryProvider"/>,
/// so the removal link cannot go missing from one path again.</para>
/// </summary>
public sealed record InviteLetter(
    Guid CampaignId,
    Guid InviteId,
    string InviterName,
    string? InviterEmail,
    string InviteLink,
    string RemovalLink,
    string MessageText,
    Events.CalendarEntry? SaveTheDate = null)
{
    /// <summary>What the host is called when the campaign has no inviter name yet.</summary>
    public const string FallbackInviterName = "your host";

    /// <param name="inviteeBase">The guest app origin (<see cref="Common.AppUrls.InviteeBase"/>).</param>
    /// <param name="rawToken">
    /// The guest's freshly minted invite token. Only its hash is stored, so both links are built
    /// from it here, at send time, and never again.
    /// </param>
    public static InviteLetter For(
        Campaign campaign, Guest guest, Guid inviteId, DeliverySettings settings,
        string inviteeBase, string rawToken, string? inviterName, string? inviterEmail)
    {
        var host = string.IsNullOrWhiteSpace(inviterName) ? FallbackInviterName : inviterName.Trim();
        var guestName = string.IsNullOrWhiteSpace(guest.Name) ? "there" : guest.Name.Trim();
        var link = $"{inviteeBase}/i/{rawToken}";
        return new InviteLetter(
            campaign.Id, inviteId, host, inviterEmail, link,
            RemovalLink: $"{inviteeBase}/privacy/remove/{rawToken}",
            MessageText: settings.Personalize(guestName, host, link),
            SaveTheDate: Campaigns.SaveTheDates.Is(campaign) ? Campaigns.EventCalendar.For(campaign, link) : null);
    }

    /// <summary>The letter addressed for one channel.</summary>
    public InviteDeliveryMessage To(string channel, string address) =>
        new(channel, address, InviterName, InviteLink, MessageText,
            CampaignId: CampaignId, InviteId: InviteId, InviterEmail: InviterEmail, RemovalLink: RemovalLink,
            SaveTheDate: SaveTheDate);
}
