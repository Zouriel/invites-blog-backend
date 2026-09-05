using System.Text.Json;
using System.Text.Json.Nodes;
using FluentValidation;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Invites;
using InvitesBlog.Application.Dtos.Otp;
using InvitesBlog.Application.Rsvp;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Exceptions.Invites;
using InvitesBlog.Application.Security;
using InvitesBlog.Application.Services.Otp;
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
    IRepository<VerifiedContactLink> contactLinks,
    IRepository<InviteTrustedIp> trustedIps,
    IRepository<EventPhoto> photos,
    IOtpService otp,
    IUnitOfWork uow,
    ICurrentUser currentUser,
    IConfiguration config,
    IValidator<RsvpRequest> rsvpValidator) : IInviteService
{
    /// <summary>Max concurrently-trusted IPs per personal invite link — see class doc on <see cref="InviteTrustedIp"/>.</summary>
    private const int MaxTrustedIps = 3;

    public async Task<object> GetByTokenAsync(string token, string? ipAddress, InviteRenderer render, CancellationToken ct = default)
    {
        var hash = TokenService.Hash(token);
        var invite = await invites.GetByTokenHashAsync(hash, ct)
            ?? throw new InviteNotFoundException();

        var campaign = await campaigns.GetByIdAsync(invite.CampaignId, ct)
            ?? throw new InviteNotFoundException();

        if (campaign.Status == CampaignStatus.Cancelled)
            return new InviteCancelledResponse(true, "This event has been cancelled.");

        // Personal-link IP binding: the first-ever open of THIS invite auto-trusts its IP; any later
        // open from an IP not among the (up to 3) already trusted needs a reauth OTP first. This
        // supersedes the old campaign-wide IsSensitive/RequiresOtp toggle (§4.9.1) for personal links
        // — every link gets this now, not just campaigns a host flagged sensitive. Invite.RequiresOtp
        // is left in the schema (still settable via IsSensitive) but no longer consulted here.
        if (!await IsTrustedIpAsync(invite.Id, ipAddress, ct))
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
            RsvpQuestions.Parse(campaign.RsvpQuestionsJson), invite.Id);
    }

    /// <summary>
    /// The render half of <see cref="GetByTokenAsync"/>, without the access check — see the interface.
    /// Deliberately mirrors what that method does once its checks have passed, including the frozen
    /// manifest and pinned package the campaign carries.
    /// </summary>
    public async Task<InviteRenderData?> RenderAuthorizedAsync(
        Guid inviteId, string inviteLink, InviteRenderer render, CancellationToken ct = default)
    {
        var invite = await invites.GetByIdAsync(inviteId, ct);
        if (invite is null) return null;

        var campaign = await campaigns.GetByIdAsync(invite.CampaignId, ct);
        var guest = await guests.GetByIdAsync(invite.GuestId, ct);
        if (campaign is null || guest is null) return null;

        var template = await templates.GetByIdAsync(campaign.TemplateId, ct);
        if (template is null) return null;

        var inviter = campaign.InviterId is null
            ? null : await inviters.GetByIdAsync(campaign.InviterId.Value, ct);

        var payload = render(campaign, template, guest, invite, inviteLink,
            inviter?.Name, inviter?.PhoneE164, inviter?.Email);

        return new InviteRenderData(
            payload.PackageUrl, payload.Data, payload.RequiresOtp, campaign.Status.ToString());
    }

    public async Task<RsvpResultResponse> RsvpAuthorizedAsync(
        Guid inviteId, RsvpRequest req, CancellationToken ct = default)
    {
        await rsvpValidator.ValidateAndThrowAsync(req, ct);
        var invite = await invites.GetByIdAsync(inviteId, ct) ?? throw new InviteNotFoundException();
        return await RecordRsvpAsync(invite, req, ct);
    }

    public async Task<RsvpResultResponse> RsvpAsync(string token, string? ipAddress, RsvpRequest req, CancellationToken ct = default)
    {
        await rsvpValidator.ValidateAndThrowAsync(req, ct);

        var hash = TokenService.Hash(token);
        var invite = await invites.GetByTokenHashAsync(hash, ct)
            ?? throw new InviteNotFoundException();

        // Same IP-trust gate as viewing (see GetByTokenAsync) — otherwise the reauth challenge on
        // /by-token/{token} could be skipped entirely by RSVPing straight through this endpoint.
        if (!await IsTrustedIpAsync(invite.Id, ipAddress, ct))
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

    public async Task<(Guid CampaignId, Guid GuestId)?> InviteSubjectAsync(
        Guid inviteId, CancellationToken ct = default)
    {
        var invite = await invites.GetByIdAsync(inviteId, ct);
        return invite is null ? null : (invite.CampaignId, invite.GuestId);
    }

    /// <summary>
    /// The palette the guest's own invitation renders in.
    ///
    /// <para>The pages either side of the invitation used to paint themselves in one fixed dark gold,
    /// which matched exactly the templates that happened to be dark and gold. Every other campaign
    /// sent its guests from their invitation to an RSVP form that looked like a different product.
    /// The colours are read from the same two places the invitation reads them: the frozen manifest's
    /// declared defaults, with the campaign's own theme choices laid over the top.</para>
    /// </summary>
    public async Task<GuestThemeResponse?> GuestThemeAsync(Guid inviteId, CancellationToken ct = default)
    {
        var invite = await invites.GetByIdAsync(inviteId, ct);
        if (invite is null) return null;

        var campaign = await campaigns.GetByIdAsync(invite.CampaignId, ct);
        if (campaign is null) return null;

        var guest = await guests.GetByIdAsync(invite.GuestId, ct);

        // The template's authored defaults, from the manifest the campaign froze at booking.
        var declared = ParseJsonObject(campaign.TemplateManifestJson)?["theme"] as JsonObject;
        var accent = declared?["accentColor"]?.ToString();
        var background = declared?["backgroundColor"]?.ToString();
        var text = declared?["textColor"]?.ToString();

        // The inviter's choices, shared first and then anything scoped to this guest's role.
        foreach (var layer in ThemeLayers(campaign.ThemeOverridesJson, guest?.Role))
        {
            if (layer["accentColor"]?.ToString() is { Length: > 0 } a) accent = a;
            if (layer["backgroundColor"]?.ToString() is { Length: > 0 } b) background = b;
            if (layer["textColor"]?.ToString() is { Length: > 0 } t) text = t;
        }

        return new GuestThemeResponse(accent, background, text);
    }

    /// <summary>
    /// The override objects to apply, in order. A flat object is the old shape and counts as shared;
    /// the newer shape splits <c>shared</c> from per-role blocks. Mirrors the renderer's resolution so
    /// the pages either side of an invitation cannot disagree with it about the colours.
    /// </summary>
    private static IEnumerable<JsonObject> ThemeLayers(string themeJson, string? role)
    {
        if (ParseJsonObject(themeJson) is not { } theme) yield break;

        var structured = theme["shared"] is JsonObject || theme["roles"] is JsonObject;
        if (!structured) { yield return theme; yield break; }

        if (theme["shared"] is JsonObject shared) yield return shared;

        if (!string.IsNullOrWhiteSpace(role) && theme["roles"] is JsonObject byRole)
        {
            var match = byRole.FirstOrDefault(kv =>
                string.Equals(kv.Key, role, StringComparison.OrdinalIgnoreCase)).Value;
            if (match is JsonObject scoped) yield return scoped;
        }
    }

    private static JsonObject? ParseJsonObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonNode.Parse(json) as JsonObject; }
        catch (JsonException) { return null; }
    }

    public async Task<Guid?> MyGuestIdAsync(Guid campaignId, CancellationToken ct = default)
    {
        var (email, phone) = await IdentifiersAsync(ct);
        if (email is null && phone is null) return null;

        // Opted-out guests are included deliberately, unlike the invitation lookup: removing yourself
        // from a mailing list is not the same as giving up the photos you took at the party.
        var guestList = await guests.ListByCampaignAsync(campaignId, includeOptedOut: true, ct);
        return guestList.FirstOrDefault(g => Owns(g, email, phone))?.Id;
    }

    /// <summary>
    /// Resolve the OTP-authenticated caller's invite for a shared campaign link (<c>/e/{campaignId}</c>).
    /// Guest-list-only: the verified email must match a guest on the campaign, otherwise access is refused.
    /// The invite row is created on first view (no upfront dispatch needed).
    /// </summary>
    public async Task<Guid> ResolveMyInviteIdAsync(Guid campaignId, CancellationToken ct = default)
    {
        var (email, phone) = await IdentifiersAsync(ct);
        if (email is null && phone is null) throw new UnauthorizedException();

        var campaign = await campaigns.GetByIdAsync(campaignId, ct) ?? throw new InviteNotFoundException();
        if (campaign.Status == CampaignStatus.Cancelled) throw new InviteNotFoundException();

        // Guest-list-only, matched on the VERIFIED identifier — the same rule GetMyInviteAsync uses.
        var guestList = await guests.ListByCampaignAsync(campaignId, includeOptedOut: false, ct);
        var guest = guestList.FirstOrDefault(g => Owns(g, email, phone))
            ?? throw new InviteNotFoundException();

        var invite = await invites.GetByGuestIdAsync(guest.Id, ct);
        if (invite is null)
        {
            invite = new Invite
            {
                Id = Guid.NewGuid(),
                CampaignId = campaignId,
                GuestId = guest.Id,
                // token_hash is NOT NULL; this path never uses it, but it must not be guessable.
                TokenHash = TokenService.Hash(TokenService.GenerateToken()),
                Status = InviteStatus.Sent,
                RsvpStatus = RsvpStatus.NoResponse,
                CreatedAt = DateTimeOffset.UtcNow
            };
            await invites.AddAsync(invite, ct);
        }

        if (invite.ViewedAt is null)
        {
            invite.ViewedAt = DateTimeOffset.UtcNow;
            if (invite.Status != InviteStatus.Viewed) invite.Status = InviteStatus.Viewed;
        }
        await uow.SaveChangesAsync(ct);

        return invite.Id;
    }

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

        // What each invitation looks like, so the inbox can show them as a grid rather than a list of
        // titles. The template preview is the FALLBACK; the host's chosen cover wins — see below.
        var templateIds = campaignList.Select(c => c.TemplateId).Distinct().ToList();
        var previews = await templates.Query()
            .Where(t => templateIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.PreviewImageUrl, ct);

        // One grouped count for the page, not one query per invitation.
        // Standalone-bucket photographs have no campaign and belong to no invitation's count, so
        // they are excluded before grouping — see the same shape in AccountService.
        var photoCounts = await photos.Query()
            .Where(p => p.CampaignId != null && campaignIds.Contains(p.CampaignId.Value) && p.DeletedAt == null)
            .GroupBy(p => p.CampaignId!.Value)
            .Select(g => new { CampaignId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.CampaignId, x => x.Count, ct);

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
                    c.InviterId is { } iid ? inviterNames.GetValueOrDefault(iid) : null,
                    // The host's own cover first: a template preview is a marketing poster rendered
                    // from demo content, so leading with it showed a stranger's name on the tile.
                    Application.Campaigns.CampaignCover.Read(c.CustomContentJson)
                        ?? previews.GetValueOrDefault(c.TemplateId),
                    photoCounts.GetValueOrDefault(c.Id));
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

        // An invitee with no account answers to the one contact they verified — plus any second
        // contact they have since PROVED is theirs (VerifiedContactLink). That is what lets someone
        // invited by email open their inbox with the phone number a different host had for them.
        // Only proven links count: guest rows pair contacts too, but nobody checks those.
        if (currentUser.ContactType == "phone")
        {
            var byPhone = await contactLinks.FirstOrDefaultAsync(l => l.PhoneE164 == contact, ct);
            return (byPhone?.Email, contact);
        }

        var byEmail = await contactLinks.FirstOrDefaultAsync(l => l.Email == contact, ct);
        return (contact, byEmail?.PhoneE164);
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

    public async Task<InviteReauthRequestedResponse> RequestReauthAsync(string token, CancellationToken ct = default)
    {
        var invite = await invites.GetByTokenHashAsync(TokenService.Hash(token), ct)
            ?? throw new InviteNotFoundException();
        var guest = await guests.GetByIdAsync(invite.GuestId, ct)
            ?? throw new InviteNotFoundException();

        // The link is already user-bound — the caller never types a contact in. The code goes to
        // whatever the guest row has on file: email preferred (it's also the invite delivery channel),
        // phone as fallback (phone is OTP-only — never a delivery channel of its own here).
        string channel;
        OtpChallengeResponse challenge;
        if (!string.IsNullOrWhiteSpace(guest.Email))
        {
            channel = "email";
            challenge = await otp.RequestAsync(
                new SendOtpRequest("email", null, guest.Email, null), OtpPurpose.InviteReauth, ct);
        }
        else if (!string.IsNullOrWhiteSpace(guest.PhoneE164))
        {
            channel = "sms";
            challenge = await otp.RequestAsync(
                new SendOtpRequest("sms", guest.PhoneE164, null, null), OtpPurpose.InviteReauth, ct);
        }
        else
        {
            // No contact on file at all — shouldn't normally happen (a guest needs one to have been
            // dispatched to in the first place), but nothing to reauth against.
            throw new InviteNotFoundException();
        }

        return new InviteReauthRequestedResponse(challenge.ChallengeId, challenge.ExpiresInSeconds, channel);
    }

    public async Task<object> VerifyReauthAsync(
        string token, string? ipAddress, VerifyOtpRequest req, InviteRenderer render, CancellationToken ct = default)
    {
        var invite = await invites.GetByTokenHashAsync(TokenService.Hash(token), ct)
            ?? throw new InviteNotFoundException();

        // Consumes the challenge — proves the caller is really reachable at whatever contact
        // RequestReauthAsync sent the code to. The invite itself already says who this is; the OTP
        // only proves the person opening it right now can be reached there. Deliberately doesn't call
        // OtpService.VerifyAsync — this must NOT mint the normal 30-day account JWT (see class docs on
        // IInviteService.VerifyReauthAsync: this reauth is scoped to just this one invite).
        await otp.VerifyContactAsync(req, ct);

        if (!string.IsNullOrWhiteSpace(ipAddress))
            await TrustIpAsync(invite.Id, ipAddress, ct);

        return await GetByTokenAsync(token, ipAddress, render, ct);
    }

    /// <summary>
    /// True if the IP is already trusted for this invite (bumping its last-seen time), or if this is
    /// the very first open ever (which auto-trusts it). False means the caller must reauthenticate.
    /// </summary>
    private async Task<bool> IsTrustedIpAsync(Guid inviteId, string? ipAddress, CancellationToken ct)
    {
        // No IP visible (e.g. forwarded-headers misconfigured) — never silently trust; this is exactly
        // the situation the whole feature is meant to gate.
        if (string.IsNullOrWhiteSpace(ipAddress)) return false;

        var trusted = await trustedIps.ListAsync(t => t.InviteId == inviteId, ct);
        var match = trusted.FirstOrDefault(t => t.IpAddress == ipAddress);
        if (match is not null)
        {
            match.LastSeenAt = DateTimeOffset.UtcNow;
            await uow.SaveChangesAsync(ct);
            return true;
        }
        if (trusted.Count > 0) return false; // known invite, unrecognized IP — needs reauth

        await trustedIps.AddAsync(new InviteTrustedIp
        {
            Id = Guid.NewGuid(), InviteId = inviteId, IpAddress = ipAddress,
            FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow,
        }, ct);
        await uow.SaveChangesAsync(ct);
        return true;
    }

    /// <summary>Trusts a new IP for an invite post-reauth, evicting the least-recently-seen of the
    /// existing <see cref="MaxTrustedIps"/> first if the slot list is already full.</summary>
    private async Task TrustIpAsync(Guid inviteId, string ipAddress, CancellationToken ct)
    {
        var trusted = await trustedIps.ListAsync(t => t.InviteId == inviteId, ct);
        if (trusted.Any(t => t.IpAddress == ipAddress)) return; // already trusted — nothing to do

        if (trusted.Count >= MaxTrustedIps)
            trustedIps.Remove(trusted.OrderBy(t => t.LastSeenAt).First());

        await trustedIps.AddAsync(new InviteTrustedIp
        {
            Id = Guid.NewGuid(), InviteId = inviteId, IpAddress = ipAddress,
            FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow,
        }, ct);
        await uow.SaveChangesAsync(ct);
    }
}
