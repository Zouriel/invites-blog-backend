using System.Text.Json;
using FluentValidation;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Invites;
using InvitesBlog.Application.Rsvp;
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
    IRepository<AppUser> users,
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

        return new InviteViewResponse(payload.PackageUrl, payload.Data, false, payload.CampaignStatus,
            RsvpQuestions.Parse(campaign.RsvpQuestionsJson));
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

        var (email, phone) = await IdentifiersAsync(ct);
        if (email is null && phone is null) throw new UnauthorizedException();

        var invite = await invites.GetByIdAsync(inviteId, ct)
            ?? throw new InviteNotFoundException();
        var guest = await guests.GetByIdAsync(invite.GuestId, ct)
            ?? throw new InviteNotFoundException();

        // Every identifier the caller holds, matching the inbox that listed this invitation: an
        // account merged from a phone and an email must be able to answer either invitation.
        if (!Owns(guest, email, phone))
            throw new InviteNotFoundException(); // don't reveal existence to non-owners

        return await RecordRsvpAsync(invite, req, ct);
    }

    /// <summary>
    /// Resolve the OTP-authenticated caller's invite for a shared campaign link (<c>/e/{campaignId}</c>).
    /// Guest-list-only: the verified email must match a guest on the campaign, otherwise access is refused.
    /// The invite row is created on first view (no upfront dispatch needed). Returns the rendered invite.
    /// </summary>
    public async Task<object> GetMyInviteAsync(Guid campaignId, InviteRenderer render, CancellationToken ct = default)
    {
        var (email, phone) = await IdentifiersAsync(ct);
        if (email is null && phone is null) throw new UnauthorizedException();

        var campaign = await campaigns.GetByIdAsync(campaignId, ct)
            ?? throw new InviteNotFoundException();
        if (campaign.Status == CampaignStatus.Cancelled)
            return new InviteCancelledResponse(true, "This event has been cancelled.");

        // Match the VERIFIED identifier to a guest on this campaign (guest-list-only access). Phone
        // counts as well as email — the sign-in code can be sent to either, and a guest list of phone
        // numbers would otherwise lock everyone out of the shared link.
        var guestList = await guests.ListByCampaignAsync(campaignId, includeOptedOut: false, ct);
        var guest = guestList.FirstOrDefault(g => Owns(g, email, phone))
            ?? throw new InviteNotFoundException(); // they aren't on the guest list

        // Get-or-create this guest's invite (lazy — created on first authenticated view).
        var invite = await invites.GetByGuestIdAsync(guest.Id, ct);
        if (invite is null)
        {
            invite = new Invite
            {
                Id = Guid.NewGuid(),
                CampaignId = campaignId,
                GuestId = guest.Id,
                // token_hash is NOT NULL; viewing no longer uses it (access is by OTP match), but keep it random.
                TokenHash = TokenService.Hash(TokenService.GenerateToken()),
                Status = InviteStatus.Sent,
                RsvpStatus = RsvpStatus.NoResponse,
                CreatedAt = DateTimeOffset.UtcNow
            };
            await invites.AddAsync(invite, ct);
        }

        var template = await templates.GetByIdAsync(campaign.TemplateId, ct)
            ?? throw new InviteNotFoundException();
        var inviter = campaign.InviterId is null
            ? null : await inviters.GetByIdAsync(campaign.InviterId.Value, ct);

        if (invite.ViewedAt is null)
        {
            invite.ViewedAt = DateTimeOffset.UtcNow;
            if (invite.Status != InviteStatus.Viewed) invite.Status = InviteStatus.Viewed;
        }

        // The template's own "Respond now" button gets `{link}/rsvp`, so the base has to be a place
        // where BOTH the invitation and its RSVP actually resolve. /e/{campaignId} had no /rsvp
        // sibling, so that button landed on the invitee site's home page — the "empty page" it
        // appeared to open. Addressing the invite by ID gives it a route that exists.
        var inviteeBase = (config["Urls:InviteeBase"] ?? "http://localhost:4201").TrimEnd('/');
        var link = $"{inviteeBase}/invites/{invite.Id}";
        var payload = render(campaign, template, guest, invite, link, inviter?.Name, inviter?.PhoneE164, inviter?.Email);
        await uow.SaveChangesAsync(ct);

        return new MyInviteResponse(payload.PackageUrl, payload.Data, payload.CampaignStatus,
            invite.Id, invite.RsvpStatus.ToString(), RsvpQuestions.Parse(campaign.RsvpQuestionsJson));
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
            // Whatever the host asked beyond those four. Blank answers are dropped rather than
            // stored as empty strings, so "didn't answer" and "answered with nothing" stay the same.
            AnswersJson = JsonSerializer.Serialize(
                (req.Answers ?? new Dictionary<string, string>())
                    .Where(a => !string.IsNullOrWhiteSpace(a.Value))
                    .ToDictionary(a => a.Key, a => a.Value.Trim())),
            CreatedAt = DateTimeOffset.UtcNow
        }, ct);
        await uow.SaveChangesAsync(ct);

        return new RsvpResultResponse(status.ToString());
    }

    public async Task<IReadOnlyList<InboxCardResponse>> GetInboxAsync(CancellationToken ct = default)
    {
        var (email, phone) = await IdentifiersAsync(ct);
        if (email is null && phone is null) throw new UnauthorizedException();

        // Every identifier, not just the one on the token: someone who signed in with their phone
        // and later linked their email should find BOTH sets of invitations in one inbox.
        var guestIds = await guests.Query()
            .Where(g => (email != null && g.Email == email) || (phone != null && g.PhoneE164 == phone))
            .Select(g => g.Id).ToListAsync(ct);
        if (guestIds.Count == 0) return [];

        var inviteList = await invites.Query()
            .Where(i => guestIds.Contains(i.GuestId)).ToListAsync(ct);
        if (inviteList.Count == 0) return [];

        var campaignIds = inviteList.Select(i => i.CampaignId).Distinct().ToList();
        var campaignList = await campaigns.Query()
            .Where(c => campaignIds.Contains(c.Id)).ToListAsync(ct);

        var inviterIds = campaignList.Select(c => c.InviterId).OfType<Guid>().Distinct().ToList();
        var inviterNames = await inviters.Query()
            .Where(i => inviterIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, i => i.Name, ct);

        var now = DateTimeOffset.UtcNow;
        return inviteList
            .Select(i =>
            {
                var c = campaignList.FirstOrDefault(x => x.Id == i.CampaignId);
                if (c is null) return null;
                return new InboxCardResponse(
                    i.Id, c.Id, c.Title, c.EventStartAt, c.EventType,
                    i.RsvpStatus.ToString(), i.ViewedAt is null,
                    c.EventStartAt < now, c.Status == CampaignStatus.Cancelled,
                    c.InviterId is { } iid ? inviterNames.GetValueOrDefault(iid) : null);
            })
            .OfType<InboxCardResponse>()
            .OrderByDescending(i => i.EventDate)
            .ToList();
    }

    /// <summary>Does this guest row belong to the caller, by either identifier?</summary>
    private static bool Owns(Guest guest, string? email, string? phone) =>
        (email is not null && !string.IsNullOrWhiteSpace(guest.Email)
            && string.Equals(guest.Email, email, StringComparison.OrdinalIgnoreCase)) ||
        (phone is not null && !string.IsNullOrWhiteSpace(guest.PhoneE164)
            && string.Equals(guest.PhoneE164, phone, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Who the caller is reachable as. A signed-in account answers to both its email and its phone;
    /// an invitee who only verified a contact (no account) answers to that one contact.
    /// </summary>
    private async Task<(string? Email, string? Phone)> IdentifiersAsync(CancellationToken ct)
    {
        if (currentUser.UserId is { } id && await users.GetByIdAsync(id, ct) is { } account)
            return (account.Email, account.PhoneE164);

        var contact = currentUser.Contact;
        if (string.IsNullOrEmpty(contact)) return (null, null);
        return currentUser.ContactType == "phone" ? (null, contact) : (contact, null);
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
