using System.Text.Json;
using System.Text.Json.Serialization;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Security;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using InvitesBlog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Delivery;

public sealed class DeliverySettings
{
    // Product rule: nothing selected → Viber first, email fallback (guests without a phone get email).
    [JsonPropertyName("channels")] public List<string> Channels { get; set; } = new() { "viber" };
    [JsonPropertyName("fallbackChannel")] public string? FallbackChannel { get; set; } = "email";
    [JsonPropertyName("messageTemplate")] public string MessageTemplate { get; set; } =
        "You have a new invite from {{inviter.name}}. Open it here: {{invite.link}}";
}

/// <summary>
/// Turns a paid campaign into sent invites (§13.1). For each guest: mint a secure token, render the
/// delivery message, try channels in order with fallback (§13.2), record every attempt, and roll the
/// campaign up to Dispatched / PartiallyDispatched. Idempotent per guest: a guest already Sent is
/// skipped so a re-run (or duplicate webhook) never double-sends (§22.3 #9).
/// </summary>
public sealed class DispatchService(
    AppDbContext db,
    IEnumerable<IInviteDeliveryProvider> providers,
    IConfiguration config,
    ILogger<DispatchService> logger) : IInviteDispatcher
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    private string InviteeBase => (config["Urls:InviteeBase"] ?? "http://localhost:4201").TrimEnd('/');

    public async Task DispatchCampaignAsync(Guid campaignId, CancellationToken ct = default)
    {
        var campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Id == campaignId, ct);
        if (campaign is null) return;
        var inviter = campaign.InviterId is null
            ? null : await db.Inviters.FirstOrDefaultAsync(i => i.Id == campaign.InviterId, ct);
        var inviterName = inviter?.Name ?? "your host";

        var settings = Deserialize(campaign.DeliverySettingsJson);
        var guests = await db.Guests
            .Where(g => g.CampaignId == campaignId && !g.OptedOut)
            .ToListAsync(ct);

        campaign.Status = CampaignStatus.Dispatching;
        await db.SaveChangesAsync(ct);

        int sent = 0, failed = 0, notSent = 0;

        foreach (var guest in guests)
        {
            var invite = await db.Invites.FirstOrDefaultAsync(i => i.GuestId == guest.Id, ct);
            if (invite is { Status: InviteStatus.Sent or InviteStatus.Viewed })
            {
                sent++;
                continue; // already delivered — never double-send
            }

            var ok = await DeliverToGuestAsync(campaign, guest, settings, inviterName, inviter?.Email, ct);
            if (ok) sent++;
            else if (HasAnyContact(guest, settings)) failed++;
            else notSent++;   // §product rule: no phone (Viber) and no email — recorded, not a failure
        }

        // A "not sent — no contact" guest is not a delivery failure: a campaign where every
        // reachable guest got their invite still rolls up to Dispatched (with N not-sent).
        campaign.Status = failed == 0
            ? CampaignStatus.Dispatched
            : (sent > 0 ? CampaignStatus.PartiallyDispatched : CampaignStatus.PaymentFailed);
        campaign.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Dispatch for {CampaignId}: {Sent} sent, {Failed} failed, {NotSent} not sent (no contact).",
            campaignId, sent, failed, notSent);
    }

    /// <summary>
    /// Free per-guest resend (§4.7.4). Reuses the same invite row; mints a fresh token since only the
    /// hash is stored (§9.3). Caller enforces the "max 3 per 24h" limit.
    /// </summary>
    public async Task<bool> ResendAsync(Guid guestId, CancellationToken ct = default)
    {
        var guest = await db.Guests.FirstOrDefaultAsync(g => g.Id == guestId, ct);
        if (guest is null || guest.OptedOut) return false;
        var campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Id == guest.CampaignId, ct);
        if (campaign is null) return false;
        var inviter = campaign.InviterId is null ? null
            : await db.Inviters.FirstOrDefaultAsync(i => i.Id == campaign.InviterId, ct);
        var settings = Deserialize(campaign.DeliverySettingsJson);
        return await DeliverToGuestAsync(campaign, guest, settings, inviter?.Name ?? "your host", inviter?.Email, ct);
    }

    /// <summary>Mint token, build message, deliver with fallback, update the invite. Shared by dispatch + resend.</summary>
    private async Task<bool> DeliverToGuestAsync(
        Campaign campaign, Guest guest, DeliverySettings settings, string inviterName, string? inviterEmail, CancellationToken ct)
    {
        var invite = await db.Invites.FirstOrDefaultAsync(i => i.GuestId == guest.Id, ct);
        var rawToken = TokenService.GenerateToken();
        if (invite is null)
        {
            invite = new Invite
            {
                Id = Guid.NewGuid(),
                CampaignId = campaign.Id,
                GuestId = guest.Id,
                RequiresOtp = campaign.IsSensitive,
                Status = InviteStatus.Queued,
                RsvpStatus = RsvpStatus.NoResponse,
                CreatedAt = DateTimeOffset.UtcNow
            };
            db.Invites.Add(invite);
        }
        invite.TokenHash = TokenService.Hash(rawToken);

        var link = $"{InviteeBase}/i/{rawToken}";
        var removalLink = $"{InviteeBase}/privacy/remove/{rawToken}";
        // Personalize the delivery message. {{name}} / {{guest.name}} → this guest; {{inviter.name}} → host.
        var guestName = string.IsNullOrWhiteSpace(guest.Name) ? "there" : guest.Name.Trim();
        var messageText = settings.MessageTemplate
            .Replace("{{name}}", guestName)
            .Replace("{{guest.name}}", guestName)
            .Replace("{{inviter.name}}", inviterName)
            .Replace("{{invite.link}}", link);

        var ok = await TryDeliverAsync(invite, campaign.Id, guest, settings,
            inviterName, inviterEmail, link, removalLink, messageText, ct);
        // Distinguish "not sent — no deliverable contact" from a provider failure (§product rule).
        invite.Status = ok
            ? InviteStatus.Sent
            : (HasAnyContact(guest, settings) ? InviteStatus.Failed : InviteStatus.NotSent);
        await db.SaveChangesAsync(ct);
        return ok;
    }

    /// <summary>True if any configured/fallback channel could reach this guest (has the contact it needs).</summary>
    private static bool HasAnyContact(Guest g, DeliverySettings s) =>
        !string.IsNullOrWhiteSpace(g.Email) || !string.IsNullOrWhiteSpace(g.PhoneE164);

    /// <summary>Try the configured channels in order, then the fallback, per §13.2.</summary>
    private async Task<bool> TryDeliverAsync(
        Invite invite, Guid campaignId, Guest guest, DeliverySettings settings,
        string inviterName, string? inviterEmail, string link, string removalLink, string messageText, CancellationToken ct)
    {
        var order = new List<string>(settings.Channels);
        if (settings.FallbackChannel is not null && !order.Contains(settings.FallbackChannel))
            order.Add(settings.FallbackChannel);

        var anyAddressable = false;

        foreach (var channel in order)
        {
            var address = AddressFor(channel, guest);
            if (address is null) continue; // channel needs contact info the guest lacks

            var provider = providers.FirstOrDefault(p =>
                p.Channel.Equals(channel, StringComparison.OrdinalIgnoreCase));
            if (provider is null) continue;

            anyAddressable = true;
            var result = await provider.SendAsync(
                new InviteDeliveryMessage(channel, address, inviterName, link, messageText,
                    CampaignId: campaignId, InviteId: invite.Id, InviterEmail: inviterEmail, RemovalLink: removalLink), ct);

            db.DeliveryAttempts.Add(new DeliveryAttempt
            {
                Id = Guid.NewGuid(),
                InviteId = invite.Id,
                Channel = channel,
                RecipientAddress = address,
                Status = result.Success ? DeliveryStatus.Sent : DeliveryStatus.Failed,
                ProviderMessageId = result.ProviderMessageId,
                ErrorMessage = result.Error,
                AttemptedAt = DateTimeOffset.UtcNow
            });

            if (result.Success) return true;
        }

        if (!anyAddressable)
        {
            // §product rule: no phone for Viber AND no email → do nothing, but say so on the dashboard.
            db.DeliveryAttempts.Add(new DeliveryAttempt
            {
                Id = Guid.NewGuid(),
                InviteId = invite.Id,
                Channel = "none",
                RecipientAddress = "-",
                Status = DeliveryStatus.Skipped,
                ErrorMessage = "Not sent: guest has no phone number (Viber) and no email address.",
                AttemptedAt = DateTimeOffset.UtcNow
            });
        }
        return false;
    }

    /// <summary>
    /// Async fallback (§13.2): a channel's delivery report came back undeliverable. If the invite has
    /// no successful attempt yet and the guest has an email, deliver via email and record the attempt.
    /// Idempotent — a second call after email already went out is a no-op (an email attempt exists).
    /// </summary>
    public async Task<bool> FallbackToEmailAsync(Guid inviteId, CancellationToken ct = default)
    {
        var invite = await db.Invites.FirstOrDefaultAsync(i => i.Id == inviteId, ct);
        if (invite is null) return false;

        var alreadyDelivered = await db.DeliveryAttempts.AnyAsync(
            a => a.InviteId == inviteId &&
                 (a.Status == DeliveryStatus.Sent || a.Status == DeliveryStatus.Delivered), ct);
        if (alreadyDelivered) return false;

        var guest = await db.Guests.FirstOrDefaultAsync(g => g.Id == invite.GuestId, ct);
        if (guest is null || string.IsNullOrWhiteSpace(guest.Email)) return false;

        var campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Id == invite.CampaignId, ct);
        if (campaign is null) return false;
        var inviter = campaign.InviterId is null ? null
            : await db.Inviters.FirstOrDefaultAsync(i => i.Id == campaign.InviterId, ct);

        var baseSettings = Deserialize(campaign.DeliverySettingsJson);
        // Force email-only so we don't re-attempt the channel that just failed (avoids report loops).
        var emailOnly = new DeliverySettings
        {
            Channels = new() { "email" },
            FallbackChannel = null,
            MessageTemplate = baseSettings.MessageTemplate
        };
        return await DeliverToGuestAsync(campaign, guest, emailOnly, inviter?.Name ?? "your host", inviter?.Email, ct);
    }

    private static string? AddressFor(string channel, Guest g) => channel.ToLowerInvariant() switch
    {
        "email" => string.IsNullOrWhiteSpace(g.Email) ? null : g.Email,
        "sms" or "whatsapp" or "viber" or "telegram" => string.IsNullOrWhiteSpace(g.PhoneE164) ? null : g.PhoneE164,
        "direct" => "direct-link",
        _ => null
    };

    private static DeliverySettings Deserialize(string json)
    {
        if (string.IsNullOrWhiteSpace(json) || json == "{}") return new DeliverySettings();
        try { return JsonSerializer.Deserialize<DeliverySettings>(json, JsonOpts) ?? new DeliverySettings(); }
        catch (JsonException) { return new DeliverySettings(); }
    }
}
