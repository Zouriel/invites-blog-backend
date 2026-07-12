using FluentValidation;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Invites;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Exceptions.Invites;
using InvitesBlog.Application.Security;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace InvitesBlog.Application.Services.Invites;

/// <summary>
/// Invitee-facing invite logic (§10.8): view by token (no login), zero-login RSVP, the OTP-verified
/// inbox, and claiming an invite to the verified identity.
/// </summary>
public sealed class InviteService(
    IInviteRepository invites,
    IGuestRepository guests,
    ICampaignRepository campaigns,
    ITemplateRepository templates,
    IInviterRepository inviters,
    IRepository<RsvpResponse> rsvpResponses,
    IUnitOfWork uow,
    ICurrentUser currentUser,
    IConfiguration config,
    IValidator<RsvpRequest> rsvpValidator) : IInviteService
{
    public async Task<object> GetByTokenAsync(string token, InviteRenderer render, CancellationToken ct = default)
    {
        var hash = TokenService.Hash(token);
        var invite = await invites.GetByTokenHashAsync(hash, ct)
            ?? throw new InviteNotFoundException();

        var campaign = await campaigns.GetByIdAsync(invite.CampaignId, ct)
            ?? throw new InviteNotFoundException();

        if (campaign.Status == CampaignStatus.Cancelled)
            return new InviteCancelledResponse(true, "This event has been cancelled.");

        // Sensitive invites require OTP before viewing (§4.9.1 / §4.9.3).
        if (invite.RequiresOtp)
            return new InviteRequiresOtpResponse(true);

        var guest = await guests.GetByIdAsync(invite.GuestId, ct)
            ?? throw new InviteNotFoundException();
        var template = await templates.GetByIdAsync(campaign.TemplateId, ct)
            ?? throw new InviteNotFoundException();
        var inviter = campaign.InviterId is null
            ? null : await inviters.GetByIdAsync(campaign.InviterId.Value, ct);

        // Mark viewed (first view).
        if (invite.ViewedAt is null)
        {
            invite.ViewedAt = DateTimeOffset.UtcNow;
            if (invite.Status != InviteStatus.Viewed) invite.Status = InviteStatus.Viewed;
            if (invite.RsvpStatus == RsvpStatus.NoResponse) invite.RsvpStatus = RsvpStatus.ViewedOnly;
            await uow.SaveChangesAsync(ct);
        }

        var inviteeBase = (config["Urls:InviteeBase"] ?? "http://localhost:4201").TrimEnd('/');
        var link = $"{inviteeBase}/i/{token}";
        var payload = render(campaign, template, guest, invite, link,
            inviter?.Name, inviter?.PhoneE164, inviter?.Email);

        return new InviteViewResponse(payload.PackageUrl, payload.Data, false, payload.CampaignStatus);
    }

    public async Task<RsvpResultResponse> RsvpAsync(string token, RsvpRequest req, CancellationToken ct = default)
    {
        await rsvpValidator.ValidateAndThrowAsync(req, ct);

        var hash = TokenService.Hash(token);
        var invite = await invites.GetByTokenHashAsync(hash, ct)
            ?? throw new InviteNotFoundException();

        // Sensitive invites gate viewing behind OTP (§4.9.1); gate RSVP too. An OTP-verified session
        // carries the contact on the (optional) JWT, so an authenticated holder can still RSVP.
        if (invite.RequiresOtp && string.IsNullOrEmpty(currentUser.Contact))
            throw new UnauthorizedException();

        return await RecordRsvpAsync(invite, req, ct);
    }

    /// <summary>
    /// Authenticated RSVP from the inbox (§10.8). The caller is OTP-verified; the invite is addressed
    /// by id, so verify the caller owns it (the guest's contact matches the verified identity) before
    /// recording — otherwise any authenticated user who learned an invite id could RSVP for it.
    /// </summary>
    public async Task<RsvpResultResponse> RsvpByInviteIdAsync(Guid inviteId, RsvpRequest req, CancellationToken ct = default)
    {
        await rsvpValidator.ValidateAndThrowAsync(req, ct);

        var contact = currentUser.Contact;
        if (string.IsNullOrEmpty(contact)) throw new UnauthorizedException();

        var invite = await invites.GetByIdAsync(inviteId, ct)
            ?? throw new InviteNotFoundException();
        var guest = await guests.GetByIdAsync(invite.GuestId, ct)
            ?? throw new InviteNotFoundException();

        var owns = currentUser.ContactType == "phone"
            ? string.Equals(guest.PhoneE164, contact, StringComparison.OrdinalIgnoreCase)
            : string.Equals(guest.Email, contact, StringComparison.OrdinalIgnoreCase);
        if (!owns) throw new InviteNotFoundException(); // don't reveal existence to non-owners

        return await RecordRsvpAsync(invite, req, ct);
    }

    /// <summary>Shared RSVP write path for the token and authenticated flows.</summary>
    private async Task<RsvpResultResponse> RecordRsvpAsync(Invite invite, RsvpRequest req, CancellationToken ct)
    {
        if (!Enum.TryParse<RsvpStatus>(req.Status, true, out var status))
            throw new InvalidRsvpStatusException(req.Status);

        invite.RsvpStatus = status;
        invite.RespondedAt = DateTimeOffset.UtcNow;
        await rsvpResponses.AddAsync(new RsvpResponse
        {
            Id = Guid.NewGuid(),
            InviteId = invite.Id,
            Status = status,
            GuestCount = req.GuestCount,
            MealPreference = req.MealPreference,
            Comment = req.Comment,
            ArrivalTime = req.ArrivalTime,
            ContactNote = req.ContactNote,
            CreatedAt = DateTimeOffset.UtcNow
        }, ct);
        await uow.SaveChangesAsync(ct);

        return new RsvpResultResponse(status.ToString());
    }

    public async Task<IReadOnlyList<InboxCardResponse>> GetInboxAsync(CancellationToken ct = default)
    {
        var contact = currentUser.Contact;
        var type = currentUser.ContactType;
        if (string.IsNullOrEmpty(contact)) throw new UnauthorizedException();

        var guestIds = await (type == "phone"
                ? guests.Query().Where(g => g.PhoneE164 == contact)
                : guests.Query().Where(g => g.Email == contact))
            .Select(g => g.Id).ToListAsync(ct);

        var inviteList = await invites.Query()
            .Where(i => guestIds.Contains(i.GuestId)).ToListAsync(ct);

        var campaignIds = inviteList.Select(i => i.CampaignId).Distinct().ToList();
        var campaignList = await campaigns.Query()
            .Where(c => campaignIds.Contains(c.Id)).ToListAsync(ct);

        var now = DateTimeOffset.UtcNow;
        return inviteList.Select(i =>
        {
            var c = campaignList.First(x => x.Id == i.CampaignId);
            return new InboxCardResponse(
                i.Id, c.Title, c.EventStartAt, c.EventType,
                i.RsvpStatus.ToString(), i.ViewedAt is null,
                c.EventStartAt < now, c.Status == CampaignStatus.Cancelled);
        }).ToList();
    }

    public async Task<ClaimResponse> ClaimAsync(string token, CancellationToken ct = default)
    {
        var contact = currentUser.Contact;
        var type = currentUser.ContactType;
        if (string.IsNullOrEmpty(contact)) throw new UnauthorizedException();

        // Possession of the raw invite token is the authorization to claim it — otherwise any
        // authenticated user who guessed/learned an invite id could hijack it onto their inbox.
        var invite = await invites.GetByTokenHashAsync(TokenService.Hash(token), ct)
            ?? throw new InviteNotFoundException();
        var guest = await guests.GetByIdAsync(invite.GuestId, ct)
            ?? throw new InviteNotFoundException();

        // Link the invite to the verified identity so it appears in the inbox permanently (§4.9.2).
        if (type == "phone") guest.PhoneE164 = contact;
        else guest.Email = contact;
        await uow.SaveChangesAsync(ct);

        return new ClaimResponse(true);
    }
}
