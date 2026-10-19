using InvitesBlog.Application.Abstractions;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;

namespace InvitesBlog.Application.Delivery;

/// <summary>
/// Which way one guest's invitation goes, and the walk that sends it. The first send
/// (<c>CampaignService.FinalizeAsync</c>) and the per-guest sends (<c>DispatchService</c>) both come
/// through here, so a guest is reached the same way whichever path sends to them.
///
/// <para>The host's "send to each guest" choice is still stored as the <c>email</c> channel (every
/// existing event says so), but it now means: Viber when the guest has a phone number and Viber is
/// set up, email when they have no phone or Viber fails, otherwise not sent. Email is the last resort,
/// not the default, so OTP codes stay the main thing invites.blog emails.</para>
/// </summary>
public static class GuestRoute
{
    /// <summary>The channels to try for this guest, in order. Each needs the contact it is sent to.</summary>
    public static IReadOnlyList<(string Channel, string Address)> For(
        Guest guest, DeliverySettings settings, IEnumerable<IInviteDeliveryProvider> providers)
    {
        var wanted = new List<string>();
        foreach (var c in settings.Channels.Append(settings.FallbackChannel ?? ""))
        {
            if (string.Equals(c, "email", StringComparison.OrdinalIgnoreCase)) wanted.AddRange(["viber", "email"]);
            else if (!string.IsNullOrWhiteSpace(c)) wanted.Add(c.ToLowerInvariant());
        }

        var route = new List<(string, string)>();
        foreach (var channel in wanted.Distinct())
        {
            var provider = providers.FirstOrDefault(p => p.Channel.Equals(channel, StringComparison.OrdinalIgnoreCase));
            if (provider is null) continue;
            if (AddressFor(channel, guest) is { } address && (provider is not IAddressGatedProvider gated || gated.CanSendTo(address))) route.Add((channel, address));
        }
        return route;
    }

    /// <summary>
    /// Sends down the route until one channel takes it. Returns every attempt made (not yet saved),
    /// and whether one succeeded. A Viber "sent" is only accepted by Infobip: if Viber later reports the
    /// guest unreachable, the delivery-report webhook falls back to email then.
    /// </summary>
    public static async Task<(bool Sent, List<DeliveryAttempt> Attempts)> SendAsync(
        Guid inviteId, IReadOnlyList<(string Channel, string Address)> route, InviteLetter letter,
        IEnumerable<IInviteDeliveryProvider> providers, CancellationToken ct)
    {
        var attempts = new List<DeliveryAttempt>();
        foreach (var (channel, address) in route)
        {
            var provider = providers.First(p => p.Channel.Equals(channel, StringComparison.OrdinalIgnoreCase));
            var result = await provider.SendAsync(letter.To(channel, address), ct);
            attempts.Add(new DeliveryAttempt
            {
                Id = Guid.NewGuid(),
                InviteId = inviteId,
                Channel = channel,
                RecipientAddress = address,
                Status = result.Success ? DeliveryStatus.Sent : DeliveryStatus.Failed,
                ProviderMessageId = result.ProviderMessageId,
                ErrorMessage = result.Error,
                AttemptedAt = DateTimeOffset.UtcNow
            });
            if (result.Success) return (true, attempts);
        }
        return (false, attempts);
    }

    public static string? AddressFor(string channel, Guest g) => channel.ToLowerInvariant() switch
    {
        "email" => string.IsNullOrWhiteSpace(g.Email) ? null : g.Email.Trim(),
        "sms" or "whatsapp" or "viber" or "telegram" => string.IsNullOrWhiteSpace(g.PhoneE164) ? null : g.PhoneE164,
        "direct" => "direct-link",
        _ => null
    };
}
