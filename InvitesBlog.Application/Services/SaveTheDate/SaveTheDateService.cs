using System.Text.Json;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Campaigns;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Security;
using InvitesBlog.Application.Services.Campaigns;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Services.SaveTheDate;

/// <summary>What "Make the invitation" returns: the new invitation to carry on building.</summary>
public sealed record MadeInvitationDto(Guid CampaignId, bool AlreadyMade, int GuestsCopied);

public interface ISaveTheDateService
{
    /// <summary>
    /// Starts the real invitation from a save the date: same name, day, place and people. Asked
    /// twice, it returns the one already made.
    /// </summary>
    Task<MadeInvitationDto> MakeInvitationAsync(Guid saveTheDateId, CancellationToken ct = default);
}

/// <summary>
/// A save the date and its invitation are two campaigns, one wedding. Making the invitation copies
/// what the host already told us and moves what they paid for, so nothing is typed or bought twice:
///
/// <list type="bullet">
/// <item>the guest list, with each guest's "already emailed" mark, so emailing them the invitation
/// doesn't count against the allowance a second time (SendingAllowanceService counts per invite);</item>
/// <item>the pass and any extra emails added — one pool per wedding, not one per campaign;</item>
/// <item>the people it is for, the host, the venue link and the wording.</item>
/// </list>
/// </summary>
public sealed class SaveTheDateService(
    ICampaignService campaignService,
    ICampaignOwnershipService ownership,
    ICampaignRepository campaigns,
    IGuestRepository guests,
    IInviteRepository invites,
    IRepository<CampaignCelebrant> celebrants,
    IRepository<AuditLog> auditLogs,
    IUnitOfWork uow) : ISaveTheDateService
{
    public async Task<MadeInvitationDto> MakeInvitationAsync(Guid saveTheDateId, CancellationToken ct = default)
    {
        if (!await ownership.OwnsAsync(saveTheDateId, ct))
            throw new ForbiddenException("That save the date isn't yours.");
        var std = await campaigns.Query(tracking: true).FirstOrDefaultAsync(c => c.Id == saveTheDateId, ct)
                  ?? throw new NotFoundException("That save the date no longer exists.");
        if (std.Kind != CampaignKind.SaveTheDate)
            throw new BusinessRuleException("This is already an invitation.", "not_a_save_the_date");

        if (std.InvitationCampaignId is { } madeId
            && await campaigns.Query().AnyAsync(c => c.Id == madeId && c.Status != CampaignStatus.Cancelled, ct))
            return new MadeInvitationDto(madeId, AlreadyMade: true, GuestsCopied: 0);

        var created = await campaignService.CreateBareAsync(std.Title, std.EventStartAt, ct, CampaignKind.Invitation, std.AllDay);
        var invitation = await campaigns.Query(tracking: true).FirstAsync(c => c.Id == created.CampaignId, ct);
        var now = DateTimeOffset.UtcNow;

        // What the host told us once. The wording goes too: couple names and the place are the same
        // wedding's, and a field the new design doesn't have is simply never read.
        invitation.EventEndAt = std.EventEndAt;
        invitation.EventType = std.EventType;
        invitation.CustomContentJson = std.CustomContentJson;
        invitation.InviterId = std.InviterId;
        invitation.VenueId = std.VenueId;
        invitation.IsSensitive = std.IsSensitive;
        invitation.CreatedByUserId = std.CreatedByUserId ?? invitation.CreatedByUserId;

        // What they paid for moves with the wedding.
        invitation.EventPass = std.EventPass;
        invitation.EventPassUntil = std.EventPassUntil;
        invitation.PaidInviteCapacity += std.PaidInviteCapacity;
        std.EventPass = EventPassKind.None;
        std.EventPassUntil = null;
        std.PaidInviteCapacity = 0;

        std.InvitationCampaignId = invitation.Id;
        std.UpdatedAt = now;
        invitation.UpdatedAt = now;

        foreach (var c in await celebrants.Query().Where(c => c.CampaignId == std.Id).ToListAsync(ct))
            await celebrants.AddAsync(new CampaignCelebrant
            {
                Id = Guid.NewGuid(), CampaignId = invitation.Id, Name = c.Name, Email = c.Email,
                PhoneE164 = c.PhoneE164, CanManage = c.CanManage, NotifiedAt = c.NotifiedAt, CreatedAt = now,
            }, ct);

        var emailedAt = (await invites.ListByCampaignAsync(std.Id, ct))
            .Where(i => i.FirstEmailedAt != null)
            .ToDictionary(i => i.GuestId, i => i.FirstEmailedAt);
        var list = await guests.ListByCampaignAsync(std.Id, includeOptedOut: true, ct);
        foreach (var g in list)
        {
            var copy = new Guest
            {
                Id = Guid.NewGuid(), CampaignId = invitation.Id, Email = g.Email, PhoneE164 = g.PhoneE164,
                PhoneRaw = g.PhoneRaw, Name = g.Name, Role = g.Role, Roles = [.. g.Roles], Gender = g.Gender,
                MetadataJson = g.MetadataJson, OptedOut = g.OptedOut, CreatedAt = now,
            };
            await guests.AddAsync(copy, ct);

            // The invite row exists only to carry the mark; finalize gives it a real token when the
            // invitation goes out, the same as for any guest added before sending.
            if (emailedAt.GetValueOrDefault(g.Id) is { } at)
                await invites.AddAsync(new Invite
                {
                    Id = Guid.NewGuid(), CampaignId = invitation.Id, GuestId = copy.Id,
                    TokenHash = TokenService.Hash(TokenService.GenerateToken()),
                    RequiresOtp = invitation.IsSensitive, Status = InviteStatus.Created,
                    RsvpStatus = RsvpStatus.NoResponse, FirstEmailedAt = at, CreatedAt = now,
                }, ct);
        }

        await auditLogs.AddAsync(new AuditLog
        {
            Id = Guid.NewGuid(),
            Action = "save_the_date.invitation",
            CampaignId = std.Id,
            DataJson = JsonSerializer.Serialize(new { invitation = invitation.Id, guests = list.Count, pass = invitation.EventPass.ToString() }),
            CreatedAt = now,
        }, ct);
        await uow.SaveChangesAsync(ct);
        return new MadeInvitationDto(invitation.Id, AlreadyMade: false, GuestsCopied: list.Count);
    }
}
