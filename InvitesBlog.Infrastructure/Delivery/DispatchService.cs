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
    [JsonPropertyName("channels")] public List<string> Channels { get; set; } = new() { "email" };
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

        int sent = 0, failed = 0;

        foreach (var guest in guests)
        {
            var invite = await db.Invites.FirstOrDefaultAsync(i => i.GuestId == guest.Id, ct);
            if (invite is { Status: InviteStatus.Sent or InviteStatus.Viewed })
            {
                sent++;
                continue; // already delivered — never double-send
            }

            var ok = await DeliverToGuestAsync(campaign, guest, settings, inviterName, inviter?.Email, ct);
            if (ok) sent++; else failed++;
        }

        campaign.Status = failed == 0
            ? CampaignStatus.Dispatched
            : (sent > 0 ? CampaignStatus.PartiallyDispatched : CampaignStatus.PaymentFailed);
        campaign.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        logger.LogInformation("Dispatch for {CampaignId}: {Sent} sent, {Failed} failed.", campaignId, sent, failed);
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
        var messageText = settings.MessageTemplate
            .Replace("{{inviter.name}}", inviterName)
            .Replace("{{invite.link}}", link);

        var ok = await TryDeliverAsync(invite, campaign.Id, guest, settings,
            inviterName, inviterEmail, link, removalLink, messageText, ct);
        invite.Status = ok ? InviteStatus.Sent : InviteStatus.Failed;
        await db.SaveChangesAsync(ct);
        return ok;
    }

    /// <summary>Try the configured channels in order, then the fallback, per §13.2.</summary>
    private async Task<bool> TryDeliverAsync(
        Invite invite, Guid campaignId, Guest guest, DeliverySettings settings,
        string inviterName, string? inviterEmail, string link, string removalLink, string messageText, CancellationToken ct)
    {
        var order = new List<string>(settings.Channels);
        if (settings.FallbackChannel is not null && !order.Contains(settings.FallbackChannel))
            order.Add(settings.FallbackChannel);

        foreach (var channel in order)
        {
            var address = AddressFor(channel, guest);
            if (address is null) continue; // channel needs contact info the guest lacks

            var provider = providers.FirstOrDefault(p =>
                p.Channel.Equals(channel, StringComparison.OrdinalIgnoreCase));
            if (provider is null) continue;

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
        return false;
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
