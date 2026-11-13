using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Common;
using InvitesBlog.Application.Delivery;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Exceptions.Campaigns;
using InvitesBlog.Application.Exceptions.Guests;
using InvitesBlog.Application.Security;
using InvitesBlog.Application.Services.Campaigns;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace InvitesBlog.Application.Services.Guests;

/// <param name="Link">The guest's personal link (a share link: the same every time, the emailed one keeps working).</param>
/// <param name="Text">The message to put in the app, link included. Raw text; the client URL-encodes it.</param>
/// <param name="SentAt">When the send was recorded (UTC).</param>
public sealed record ManualSendResultDto(string Link, string Text, DateTimeOffset SentAt);

/// <summary>The app the host is sending from: "whatsapp", "viber" or "telegram".</summary>
public sealed record ManualSendRequest(string? Channel);

public interface IManualSendService
{
    /// <summary>
    /// Records that the host is sending this guest their invitation by hand from <c>req.Channel</c>,
    /// and returns the link and message to send. Never counts against the emailed-invitation allowance.
    /// </summary>
    Task<ManualSendResultDto> RecordAsync(Guid campaignId, Guid guestId, ManualSendRequest req, CancellationToken ct = default);
}

public sealed class ManualSendService(
    ICampaignOwnershipService ownership,
    ICampaignRepository campaigns,
    IGuestRepository guests,
    IInviteRepository invites,
    IInviterRepository inviters,
    IRepository<DeliveryAttempt> deliveryAttempts,
    IUnitOfWork uow,
    InviteShareLinks shareLinks,
    IConfiguration config,
    InvitesBlog.Application.Services.MediaBuckets.IPhotoWindowService? windows = null) : IManualSendService
{
    public async Task<ManualSendResultDto> RecordAsync(Guid campaignId, Guid guestId, ManualSendRequest req, CancellationToken ct = default)
    {
        // The same keyholders as a resend: the organiser (account or link token) or a manager.
        if (!await ownership.OwnsAsync(campaignId, ct)) throw new CampaignAccessDeniedException();
        var campaign = await campaigns.GetByIdAsync(campaignId, ct) ?? throw new CampaignNotFoundException(campaignId);
        // No invitation links handed out once the event's photo window has ended (the links already sent still work).
        if (windows is not null) await windows.EnsureNotClosedAsync(campaignId, ct);

        var app = ManualSends.ParseApp(req.Channel)
                  ?? throw new BusinessRuleException("Choose WhatsApp, Viber or Telegram.", "manual_send_channel");

        var guest = await guests.Query(tracking: true)
                        .FirstOrDefaultAsync(g => g.Id == guestId && g.CampaignId == campaignId, ct)
                    ?? throw new GuestNotFoundException(guestId);
        if (guest.OptedOut)
            throw new BusinessRuleException("This guest asked not to be contacted.", "guest_opted_out");
        if (string.IsNullOrWhiteSpace(guest.PhoneE164))
            throw new BusinessRuleException(
                "This guest has no phone number. Add one to send on WhatsApp, Viber or Telegram.", "guest_phone_required");

        var now = DateTimeOffset.UtcNow;
        var invite = await invites.Query(tracking: true).FirstOrDefaultAsync(i => i.GuestId == guest.Id, ct);
        if (invite is null)
        {
            // Never sent anything: an invite of their own, as a dispatch would make. Its emailed token
            // is random and never shown; the share link is what this guest gets.
            invite = new Invite
            {
                Id = Guid.NewGuid(),
                CampaignId = campaign.Id,
                GuestId = guest.Id,
                TokenHash = TokenService.Hash(TokenService.GenerateToken()),
                RequiresOtp = campaign.IsSensitive,
                Status = InviteStatus.Sent,
                RsvpStatus = RsvpStatus.NoResponse,
                CreatedAt = now,
            };
            await invites.AddAsync(invite, ct);
        }
        else if (invite.Status is InviteStatus.Created or InviteStatus.Queued or InviteStatus.NotSent)
        {
            // Failed stays Failed (the email problem is still there to fix); Viewed stays Viewed.
            invite.Status = InviteStatus.Sent;
        }

        await deliveryAttempts.AddAsync(new DeliveryAttempt
        {
            Id = Guid.NewGuid(),
            InviteId = invite.Id,
            Channel = ManualSends.ChannelFor(app),
            RecipientAddress = guest.PhoneE164!,
            Status = DeliveryStatus.Sent,
            AttemptedAt = now,
        }, ct);
        await uow.SaveChangesAsync(ct);

        var inviter = campaign.InviterId is { } inviterId ? await inviters.GetByIdAsync(inviterId, ct) : null;
        var (link, text) = ManualSends.Compose(
            guest, invite.Id, DeliverySettings.Parse(campaign.DeliverySettingsJson), config.InviteeBase(), shareLinks, inviter?.Name);
        return new ManualSendResultDto(link, text, now);
    }
}
