using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Common;
using InvitesBlog.Application.Delivery;
using InvitesBlog.Application.Plans;
using InvitesBlog.Application.Security;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using InvitesBlog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Delivery;

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
    ILogger<DispatchService> logger,
    ISendingAllowanceService allowances)
{
    /// <summary>What the dashboard shows beside a guest held back by the event's emailed-invitation limit.</summary>
    public const string OverLimitMessage =
        "Not sent: this event's emailed invitations are used up. Share their link, or ask us to add more.";


    public async Task DispatchCampaignAsync(Guid campaignId, CancellationToken ct = default)
    {
        var campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Id == campaignId, ct);
        if (campaign is null) return;
        var inviter = campaign.InviterId is null
            ? null : await db.Inviters.FirstOrDefaultAsync(i => i.Id == campaign.InviterId, ct);
        var settings = DeliverySettings.Parse(campaign.DeliverySettingsJson);
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

            var ok = await DeliverToGuestAsync(campaign, guest, settings, inviter?.Name, inviter?.Email, ct);
            if (ok) sent++;
            else if (HasAnyContact(guest, settings)) failed++;
            else notSent++;   // §product rule: no phone (Viber) and no email — recorded, not a failure
        }

        // A "not sent — no contact" guest is not a delivery failure: a campaign where every
        // reachable guest got their invite still rolls up to Dispatched (with N not-sent).
        campaign.Status = failed == 0
            ? CampaignStatus.Dispatched
            : (sent > 0 ? CampaignStatus.PartiallyDispatched : CampaignStatus.DispatchFailed);
        campaign.UpdatedAt = DateTimeOffset.UtcNow;

        // A dedicated template is spent once its invitations actually reach guests — not when someone
        // merely starts a campaign with it. Marking it at creation meant one abandoned draft burned a
        // single-use template for good, with nothing sent and no way for its owner to start again.
        if (sent > 0)
        {
            var template = await db.Templates.FirstOrDefaultAsync(t => t.Id == campaign.TemplateId, ct);
            if (template is { Visibility: TemplateVisibility.Dedicated, IsUsed: false })
            {
                template.IsUsed = true;
                logger.LogInformation(
                    "Dedicated template {TemplateId} marked used — campaign {CampaignId} reached {Sent} guest(s).",
                    template.Id, campaignId, sent);
            }
        }

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
        var settings = DeliverySettings.Parse(campaign.DeliverySettingsJson);
        return await DeliverToGuestAsync(campaign, guest, settings, inviter?.Name, inviter?.Email, ct);
    }

    /// <summary>
    /// Mint token, compose the <see cref="InviteLetter"/>, deliver with fallback, update the invite.
    /// Shared by dispatch + resend — and the letter is the same one the first send
    /// (<c>CampaignService.FinalizeAsync</c>) composes, so every path mails the same email.
    /// </summary>
    private async Task<bool> DeliverToGuestAsync(
        Campaign campaign, Guest guest, DeliverySettings settings, string? inviterName, string? inviterEmail, CancellationToken ct)
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

        // Emailing is counted per guest, once (SendingAllowanceService): a guest already emailed is
        // re-sent for free; a new one past what the event includes is held back, and says so.
        var emailsThem = settings.Uses("email") && !string.IsNullOrWhiteSpace(guest.Email);
        var firstTime = emailsThem && invite.FirstEmailedAt is null;
        if (firstTime && (await allowances.ForCampaignAsync(campaign.Id, ct)).Left <= 0)
        {
            db.DeliveryAttempts.Add(new DeliveryAttempt
            {
                Id = Guid.NewGuid(),
                InviteId = invite.Id,
                Channel = "email",
                RecipientAddress = guest.Email!,
                Status = DeliveryStatus.Skipped,
                ErrorMessage = OverLimitMessage,
                AttemptedAt = DateTimeOffset.UtcNow
            });
            if (invite.Status is InviteStatus.Created or InviteStatus.Queued) invite.Status = InviteStatus.NotSent;
            await db.SaveChangesAsync(ct);
            return false;
        }

        var letter = InviteLetter.For(
            campaign, guest, invite.Id, settings, config.InviteeBase(), rawToken, inviterName, inviterEmail);

        var ok = await TryDeliverAsync(invite, guest, settings, letter, ct);
        if (ok && firstTime) invite.FirstEmailedAt = DateTimeOffset.UtcNow;
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
        Invite invite, Guest guest, DeliverySettings settings, InviteLetter letter, CancellationToken ct)
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
            var result = await provider.SendAsync(letter.To(channel, address), ct);

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
            // Nothing configured can reach this guest — record it plainly so the dashboard shows who
            // was missed and what to do, rather than leaving a silent gap in the delivery report.
            // A guest with a phone but no email lands here (email is the only sending channel), and
            // they are NOT stuck: the shared campaign link accepts their number at the invite gate.
            db.DeliveryAttempts.Add(new DeliveryAttempt
            {
                Id = Guid.NewGuid(),
                InviteId = invite.Id,
                Channel = "none",
                RecipientAddress = "-",
                Status = DeliveryStatus.Skipped,
                ErrorMessage = string.IsNullOrWhiteSpace(guest.Email)
                    ? "Not sent: no email address on file. Share the invitation link with them — they can open it and verify with their phone number."
                    : "Not sent: no delivery channel could reach this guest.",
                AttemptedAt = DateTimeOffset.UtcNow
            });
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
}
