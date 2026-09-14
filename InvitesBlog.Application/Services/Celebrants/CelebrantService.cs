using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Campaigns;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Phones;
using InvitesBlog.Application.Services.Campaigns;
using InvitesBlog.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace InvitesBlog.Application.Services.Celebrants;

/// <summary>The people an event is for. See <see cref="CampaignCelebrant"/>.</summary>
public interface ICelebrantService
{
    Task<IReadOnlyList<CelebrantDto>> ListAsync(Guid campaignId, CancellationToken ct = default);
    Task<IReadOnlyList<CelebrantDto>> AddAsync(Guid campaignId, AddCelebrantRequest req, CancellationToken ct = default);
    Task<IReadOnlyList<CelebrantDto>> RemoveAsync(Guid campaignId, Guid celebrantId, CancellationToken ct = default);

    /// <summary>Gives or takes full access. Only the organiser may; a celebrant can't promote another.</summary>
    Task<IReadOnlyList<CelebrantDto>> SetAccessAsync(
        Guid campaignId, Guid celebrantId, SetCelebrantAccessRequest req, CancellationToken ct = default);

    Task<IReadOnlyList<CelebrantDto>> NotifyAsync(Guid campaignId, Guid celebrantId, CancellationToken ct = default);
}

public sealed class CelebrantService(
    ICampaignOwnershipService ownership,
    ICampaignRepository campaigns,
    IRepository<CampaignCelebrant> celebrants,
    IRepository<AppUser> users,
    IInviterRepository inviters,
    ICurrentUser currentUser,
    IEmailSender email,
    PhoneNormalizer phones,
    IUnitOfWork uow,
    IConfiguration config) : ICelebrantService
{
    public async Task<IReadOnlyList<CelebrantDto>> ListAsync(Guid campaignId, CancellationToken ct = default)
    {
        await RequireAsync(campaignId, CampaignAccess.Manager, ct);
        return await ListInternalAsync(campaignId, ct);
    }

    public async Task<IReadOnlyList<CelebrantDto>> AddAsync(
        Guid campaignId, AddCelebrantRequest req, CancellationToken ct = default)
    {
        await RequireAsync(campaignId, CampaignAccess.Manager, ct);

        var name = (req.Name ?? string.Empty).Trim();
        if (name.Length == 0) throw new BusinessRuleException("Add their name.", "celebrant_name_required");
        if (name.Length > 120) name = name[..120];

        var mail = string.IsNullOrWhiteSpace(req.Email) ? null : req.Email.Trim().ToLowerInvariant();
        if (mail is not null && (!mail.Contains('@') || mail.StartsWith('@') || mail.EndsWith('@')))
            throw new BusinessRuleException("That email doesn't look right.", "celebrant_email_invalid");

        string? phone = null;
        if (!string.IsNullOrWhiteSpace(req.Phone))
        {
            var normalized = phones.Normalize(req.Phone);
            if (!normalized.IsUsable || string.IsNullOrWhiteSpace(normalized.E164))
                throw new BusinessRuleException("That phone number doesn't look right.", "celebrant_phone_invalid");
            phone = normalized.E164;
        }

        if (mail is null && phone is null)
            throw new BusinessRuleException(
                "Add their email or phone number, so the event can reach their account.", "celebrant_contact_required");

        // Adding the organiser changes nothing: they already have the event. Checked against the event's
        // own organiser rather than only the caller, because the builder calls with the campaign's
        // possession token and carries no account.
        if (await IsOrganiserAsync(campaignId, mail, phone, ct))
            return await ListInternalAsync(campaignId, ct);

        var existing = await celebrants.Query()
            .AnyAsync(c => c.CampaignId == campaignId
                           && ((mail != null && c.Email == mail) || (phone != null && c.PhoneE164 == phone)), ct);
        if (existing)
            throw new BusinessRuleException("That person is already on this event.", "celebrant_exists");

        var celebrant = new CampaignCelebrant
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            Name = name,
            Email = mail,
            PhoneE164 = phone,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        await celebrants.AddAsync(celebrant, ct);

        if (req.Notify && mail is not null) await SendHeadsUpAsync(campaignId, celebrant, ct);

        await uow.SaveChangesAsync(ct);
        return await ListInternalAsync(campaignId, ct);
    }

    public async Task<IReadOnlyList<CelebrantDto>> RemoveAsync(
        Guid campaignId, Guid celebrantId, CancellationToken ct = default)
    {
        await RequireAsync(campaignId, CampaignAccess.Manager, ct);
        var celebrant = await FindAsync(campaignId, celebrantId, ct);
        celebrants.Remove(celebrant);
        await uow.SaveChangesAsync(ct);
        return await ListInternalAsync(campaignId, ct);
    }

    public async Task<IReadOnlyList<CelebrantDto>> SetAccessAsync(
        Guid campaignId, Guid celebrantId, SetCelebrantAccessRequest req, CancellationToken ct = default)
    {
        await RequireAsync(campaignId, CampaignAccess.Organiser, ct);
        var celebrant = await FindAsync(campaignId, celebrantId, ct);
        celebrant.CanManage = req.CanManage;
        celebrants.Update(celebrant);
        await uow.SaveChangesAsync(ct);
        return await ListInternalAsync(campaignId, ct);
    }

    public async Task<IReadOnlyList<CelebrantDto>> NotifyAsync(
        Guid campaignId, Guid celebrantId, CancellationToken ct = default)
    {
        await RequireAsync(campaignId, CampaignAccess.Manager, ct);
        var celebrant = await FindAsync(campaignId, celebrantId, ct);
        if (celebrant.Email is null)
            throw new BusinessRuleException(
                "We can only send the heads-up by email for now. Add their email first.", "celebrant_no_email");

        await SendHeadsUpAsync(campaignId, celebrant, ct);
        celebrants.Update(celebrant);
        await uow.SaveChangesAsync(ct);
        return await ListInternalAsync(campaignId, ct);
    }

    private async Task SendHeadsUpAsync(Guid campaignId, CampaignCelebrant celebrant, CancellationToken ct)
    {
        var campaign = await campaigns.GetByIdAsync(campaignId, ct)
                       ?? throw new NotFoundException("That event no longer exists.");
        var link = $"{(config["Urls:InviterBase"] ?? "http://localhost:4200").TrimEnd('/')}/inbox?tab=mine";
        var name = System.Net.WebUtility.HtmlEncode(celebrant.Name);
        var title = System.Net.WebUtility.HtmlEncode(campaign.Title);

        var html =
            "<div style=\"font-family:-apple-system,'Segoe UI',Roboto,Arial,sans-serif;max-width:520px;margin:0 auto;padding:24px;color:#2a1420\">" +
            $"<p style=\"font-size:16px;line-height:1.6\">Hi {name},</p>" +
            $"<p style=\"font-size:16px;line-height:1.6\">You've been added to <strong>{title}</strong> on invites.blog. " +
            "Sign in with this email to see who's coming and all the photos from the event.</p>" +
            $"<p style=\"text-align:center;margin:28px 0\"><a href=\"{link}\" style=\"display:inline-block;background:#1b3d59;color:#fff;text-decoration:none;padding:14px 30px;border-radius:999px;font-weight:600\">Open the event</a></p>" +
            "<p style=\"font-size:12px;color:#8a5c72;line-height:1.6\">Sent via invites.blog</p></div>";

        await email.SendAsync(new EmailMessage(
            To: celebrant.Email!,
            Subject: $"You've been added to {campaign.Title}",
            Html: html,
            Stream: EmailStream.System,
            Tags: new[] { new KeyValuePair<string, string>("kind", "celebrant_added") }), ct);

        celebrant.NotifiedAt = DateTimeOffset.UtcNow;
    }

    private async Task<bool> IsOrganiserAsync(Guid campaignId, string? mail, string? phone, CancellationToken ct)
    {
        var campaign = await campaigns.GetByIdAsync(campaignId, ct);
        var contacts = new List<(string? Email, string? Phone)>();

        foreach (var id in new[] { currentUser.UserId, campaign?.CreatedByUserId }.OfType<Guid>().Distinct())
            if (await users.GetByIdAsync(id, ct) is { } account)
                contacts.Add((account.Email, account.PhoneE164));

        if (campaign?.InviterId is { } inviterId && await inviters.GetByIdAsync(inviterId, ct) is { } inviter)
            contacts.Add((inviter.Email, inviter.PhoneE164));

        return contacts.Any(c =>
            (mail is not null && string.Equals(c.Email?.Trim(), mail, StringComparison.OrdinalIgnoreCase))
            || (phone is not null && c.Phone == phone));
    }

    private async Task RequireAsync(Guid campaignId, CampaignAccess level, CancellationToken ct)
    {
        if (await ownership.AccessAsync(campaignId, ct) < level)
            throw new ForbiddenException(level == CampaignAccess.Organiser
                ? "Only the person who organised this event can change that."
                : "That event isn't yours.");
    }

    private async Task<CampaignCelebrant> FindAsync(Guid campaignId, Guid celebrantId, CancellationToken ct) =>
        await celebrants.Query(tracking: true)
            .FirstOrDefaultAsync(c => c.Id == celebrantId && c.CampaignId == campaignId, ct)
        ?? throw new NotFoundException("That person isn't on this event.");

    private async Task<IReadOnlyList<CelebrantDto>> ListInternalAsync(Guid campaignId, CancellationToken ct) =>
        (await celebrants.Query()
            .Where(c => c.CampaignId == campaignId)
            .OrderBy(c => c.CreatedAt)
            .ToListAsync(ct))
        .Select(c => new CelebrantDto(c.Id, c.Name, c.Email, c.PhoneE164, c.CanManage, c.NotifiedAt))
        .ToList();
}
