using System.Text.Json;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Privacy;
using InvitesBlog.Application.Exceptions.Privacy;
using InvitesBlog.Application.Security;
using InvitesBlog.Domain.Entities;

namespace InvitesBlog.Application.Services.Privacy;

/// <summary>
/// Guest self-service removal (§15.3). The removal token is the invite token; we resolve it by
/// hash → guest → campaign, then anonymize the guest and hash their contacts into the suppression
/// list so no inviter can re-message them. Every removal is audited.
/// </summary>
public sealed class PrivacyService(
    IInviteRepository invites,
    IGuestRepository guests,
    ICampaignRepository campaigns,
    ISuppressionRepository suppression,
    IRepository<AuditLog> auditLogs,
    IUnitOfWork uow) : IPrivacyService
{
    public async Task<PrivacyRemovalInfoDto> GetRemovalInfoAsync(string token, CancellationToken ct = default)
    {
        var (_, guest, campaign) = await ResolveAsync(token, ct);
        return new PrivacyRemovalInfoDto(
            guest.Name, campaign.Title,
            HasEmail: guest.Email is not null,
            HasPhone: guest.PhoneE164 is not null,
            AlreadyRemoved: guest.OptedOut);
    }

    public async Task<PrivacyRemovalResultDto> RemoveAsync(string token, CancellationToken ct = default)
    {
        var (_, guest, campaign) = await ResolveAsync(token, ct);
        if (guest.OptedOut) return new PrivacyRemovalResultDto(Removed: true);

        // Hash the contacts into the suppression list so future uploads cannot re-message them.
        await SuppressAsync(guest.Email, "email", ct);
        await SuppressAsync(guest.PhoneE164, "phone", ct);

        // Anonymize the guest; the inviter only sees "guest opted out" in the report.
        guest.OptedOut = true;
        guest.Email = null;
        guest.PhoneE164 = null;
        guest.PhoneRaw = null;
        guest.Name = "Removed guest";
        guest.MetadataJson = "{}";

        await auditLogs.AddAsync(new AuditLog
        {
            Id = Guid.NewGuid(),
            Action = "guest.remove",
            Actor = "guest",
            CampaignId = campaign.Id,
            DataJson = JsonSerializer.Serialize(new { guestId = guest.Id }),
            CreatedAt = DateTimeOffset.UtcNow
        }, ct);

        await uow.SaveChangesAsync(ct);
        return new PrivacyRemovalResultDto(Removed: true);
    }

    private async Task<(Invite Invite, Guest Guest, Campaign Campaign)> ResolveAsync(string token, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(token))
            throw new PrivacyInviteNotFoundException();

        var invite = await invites.GetByTokenHashAsync(TokenService.Hash(token), ct)
                     ?? throw new PrivacyInviteNotFoundException();
        // Tracked load so mutations in RemoveAsync are persisted on SaveChanges.
        var guest = await guests.GetByIdAsync(invite.GuestId, ct)
                    ?? throw new PrivacyInviteNotFoundException();
        var campaign = await campaigns.GetByIdAsync(invite.CampaignId, ct)
                       ?? throw new PrivacyInviteNotFoundException();
        return (invite, guest, campaign);
    }

    private async Task SuppressAsync(string? contact, string type, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(contact)) return;
        var hash = TokenService.HashContact(contact);
        if (await suppression.ExistsByHashAsync(hash, ct)) return;
        await suppression.AddAsync(new SuppressionEntry
        {
            Id = Guid.NewGuid(),
            ContactHash = hash,
            ContactType = type,
            CreatedAt = DateTimeOffset.UtcNow
        }, ct);
    }
}
