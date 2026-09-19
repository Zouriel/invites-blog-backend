using System.Net.Mail;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Common;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Plans;
using InvitesBlog.Application.Services.Campaigns;
using InvitesBlog.Application.Services.MediaBuckets;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Services.Venues;

/// <summary>A venue as its owner and staff see it: its face, its people, its events and its space.</summary>
/// <param name="IsOwner">Whether the caller owns it; only the owner changes its name, logo and staff.</param>
/// <param name="PlanActive">Whether the owner's Venue plan is in force. New events need it.</param>
/// <param name="Code">What a couple enters on their own event to hold it here.</param>
public sealed record VenueDto(
    Guid Id, string Name, string? Place, string? LogoUrl, bool IsOwner, bool PlanActive, DateTimeOffset? PlanEndsAt,
    long AccountBytes, long UsedBytes, IReadOnlyList<VenueStaffDto> Staff, IReadOnlyList<VenueEventDto> Events,
    string? Code = null);

/// <summary>The venue an event is held at, as its host sees it.</summary>
public sealed record EventVenueDto(Guid Id, string Name, string? Place, string? LogoUrl, bool PlanActive);

public sealed record LinkEventVenueRequest(string Code);

public sealed record VenueStaffDto(Guid Id, string Email, string? Name, DateTimeOffset CreatedAt);

/// <param name="HasInvitation">Whether the event has an invitation, or is albums only.</param>
public sealed record VenueEventDto(
    Guid CampaignId, string Title, DateTimeOffset EventStartAt, int Albums, int Photos, long UsedBytes, bool HasInvitation);

public sealed record UpdateVenueProfileRequest(string Name, string? Place);
public sealed record AddVenueStaffRequest(string Email, string? Name);
public sealed record CreateVenueEventRequest(string Title, DateTimeOffset EventDate);

public interface IVenueService
{
    /// <summary>The venue the caller owns or works at. Made on first visit for an account on the Venue plan.</summary>
    Task<VenueDto> GetAsync(CancellationToken ct = default);

    Task<VenueDto> UpdateAsync(UpdateVenueProfileRequest req, CancellationToken ct = default);
    Task<VenueDto> SetLogoAsync(byte[] content, string contentType, string fileName, CancellationToken ct = default);
    Task<VenueDto> RemoveLogoAsync(CancellationToken ct = default);
    Task<VenueDto> AddStaffAsync(AddVenueStaffRequest req, CancellationToken ct = default);
    Task<VenueDto> RemoveStaffAsync(Guid staffId, CancellationToken ct = default);

    /// <summary>A new event at the venue, with its first album. Owner or staff, while the plan is in force.</summary>
    Task<VenueEventDto> CreateEventAsync(CreateVenueEventRequest req, CancellationToken ct = default);

    /// <summary>The venue an event is held at, or null. For the event's host.</summary>
    Task<EventVenueDto?> ForEventAsync(Guid campaignId, CancellationToken ct = default);

    /// <summary>
    /// Holds the host's own event at a venue, by the code the venue gave them. The organiser only:
    /// it lets the venue's staff run the event, guest list included.
    /// </summary>
    Task<EventVenueDto> LinkEventAsync(Guid campaignId, LinkEventVenueRequest req, CancellationToken ct = default);

    /// <summary>Takes the event back from the venue. The organiser only.</summary>
    Task UnlinkEventAsync(Guid campaignId, CancellationToken ct = default);
}

public sealed class VenueService(
    ICurrentUser currentUser,
    IRepository<AppUser> users,
    IRepository<Venue> venues,
    IRepository<VenueStaff> staff,
    ICampaignRepository campaigns,
    ITemplateRepository templates,
    IRepository<MediaBucket> buckets,
    IRepository<EventPhoto> photos,
    ICampaignService campaignService,
    ICampaignOwnershipService ownership,
    IMediaBucketService bucketService,
    IPlanService plans,
    IStorageService storage,
    IImageOptimizer imageOptimizer,
    IUnitOfWork uow) : IVenueService
{
    private const int MaxLogoBytes = 5 * 1024 * 1024;
    private const int MaxStaff = 50;

    public async Task<VenueDto> GetAsync(CancellationToken ct = default) =>
        await DescribeAsync(await MineAsync(ct), ct);

    public async Task<EventVenueDto?> ForEventAsync(Guid campaignId, CancellationToken ct = default)
    {
        if (await ownership.AccessAsync(campaignId, ct) == CampaignAccess.None)
            throw new ForbiddenException("That event isn't yours.");
        var campaign = await campaigns.GetByIdAsync(campaignId, ct) ?? throw new NotFoundException("That event no longer exists.");
        return campaign.VenueId is { } id && await venues.GetByIdAsync(id, ct) is { } venue
            ? await DescribeForEventAsync(venue, ct)
            : null;
    }

    public async Task<EventVenueDto> LinkEventAsync(Guid campaignId, LinkEventVenueRequest req, CancellationToken ct = default)
    {
        if (await ownership.AccessAsync(campaignId, ct) != CampaignAccess.Organiser)
            throw new ForbiddenException("Only the event's host can hold it at a venue.");
        var code = (req.Code ?? "").Trim().ToUpperInvariant();
        var venue = string.IsNullOrEmpty(code) ? null : await venues.Query().FirstOrDefaultAsync(v => v.Code == code, ct);
        if (venue is null)
            throw new BusinessRuleException("That code doesn't match a venue. Check it with them.", "venue_code_unknown");
        if (!await PlanActiveAsync(venue, ct))
            throw new BusinessRuleException($"{venue.Name}'s plan isn't active, so it can't take events right now.", "venue_plan_ended");

        var campaign = await campaigns.Query(tracking: true).FirstOrDefaultAsync(c => c.Id == campaignId, ct)
                       ?? throw new NotFoundException("That event no longer exists.");
        campaign.VenueId = venue.Id;
        campaign.UpdatedAt = DateTimeOffset.UtcNow;
        campaigns.Update(campaign);
        await uow.SaveChangesAsync(ct);
        // The venue's albums collect for as long as a Wedding pass's.
        await bucketService.RaiseWindowsToPlanAsync(campaignId, ct);
        return await DescribeForEventAsync(venue, ct);
    }

    public async Task UnlinkEventAsync(Guid campaignId, CancellationToken ct = default)
    {
        if (await ownership.AccessAsync(campaignId, ct) != CampaignAccess.Organiser)
            throw new ForbiddenException("Only the event's host can change its venue.");
        var campaign = await campaigns.Query(tracking: true).FirstOrDefaultAsync(c => c.Id == campaignId, ct)
                       ?? throw new NotFoundException("That event no longer exists.");
        campaign.VenueId = null;
        campaign.UpdatedAt = DateTimeOffset.UtcNow;
        campaigns.Update(campaign);
        await uow.SaveChangesAsync(ct);
    }

    private async Task<EventVenueDto> DescribeForEventAsync(Venue venue, CancellationToken ct) =>
        new(venue.Id, venue.Name, venue.Place, venue.LogoUrl, await PlanActiveAsync(venue, ct));

    /// <summary>A short code a couple can read off a card: no 0/O or 1/I to confuse.</summary>
    private async Task EnsureCodeAsync(Venue venue, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(venue.Code)) return;
        const string letters = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        string code;
        do
        {
            code = new string(Enumerable.Range(0, 6)
                .Select(_ => letters[System.Security.Cryptography.RandomNumberGenerator.GetInt32(letters.Length)]).ToArray());
        } while (await venues.AnyAsync(v => v.Code == code, ct));
        venue.Code = code;
        venues.Update(venue);
        await uow.SaveChangesAsync(ct);
    }

    public async Task<VenueDto> UpdateAsync(UpdateVenueProfileRequest req, CancellationToken ct = default)
    {
        var venue = await OwnedAsync(ct);
        var name = req.Name?.Trim();
        if (string.IsNullOrWhiteSpace(name)) throw new BusinessRuleException("Give the venue a name.", "venue_name_required");
        venue.Name = name.Length > 120 ? name[..120] : name;
        var place = req.Place?.Trim();
        venue.Place = string.IsNullOrWhiteSpace(place) ? null : place.Length > 120 ? place[..120] : place;
        venue.UpdatedAt = DateTimeOffset.UtcNow;
        venues.Update(venue);
        await uow.SaveChangesAsync(ct);
        return await DescribeAsync(venue, ct);
    }

    public async Task<VenueDto> SetLogoAsync(byte[] content, string contentType, string fileName, CancellationToken ct = default)
    {
        var venue = await OwnedAsync(ct);
        if (content.Length == 0) throw new BusinessRuleException("The image file is empty.", "empty_image");
        if (content.Length > MaxLogoBytes) throw new BusinessRuleException("A logo must be 5 MB or smaller.", "image_too_large");
        // By the bytes as well as the label: the logo is served on the app's own origin, and an SVG
        // there is a script-bearing document, not a picture.
        if (!ImageSniffer.IsPhoto(content, contentType))
            throw new BusinessRuleException(ImageSniffer.Refusal, ImageSniffer.RefusalCode);

        var optimized = imageOptimizer.Optimize(content, contentType, 512);
        var ext = Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(ext)) ext = MediaFileTypes.ExtensionFor(contentType);
        venue.LogoUrl = await storage.PutAsync($"venues/{venue.Id:N}/logo-{Guid.NewGuid():N}{ext}", optimized.Content, contentType, ct);
        venue.UpdatedAt = DateTimeOffset.UtcNow;
        venues.Update(venue);
        await uow.SaveChangesAsync(ct);
        return await DescribeAsync(venue, ct);
    }

    public async Task<VenueDto> RemoveLogoAsync(CancellationToken ct = default)
    {
        var venue = await OwnedAsync(ct);
        venue.LogoUrl = null;
        venue.UpdatedAt = DateTimeOffset.UtcNow;
        venues.Update(venue);
        await uow.SaveChangesAsync(ct);
        return await DescribeAsync(venue, ct);
    }

    public async Task<VenueDto> AddStaffAsync(AddVenueStaffRequest req, CancellationToken ct = default)
    {
        var venue = await OwnedAsync(ct);
        var email = req.Email?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email) || !MailAddress.TryCreate(email, out _))
            throw new BusinessRuleException("That doesn't look like an email address.", "email_invalid");
        if (await staff.AnyAsync(s => s.VenueId == venue.Id && s.Email == email, ct))
            throw new BusinessRuleException("They're already on your staff.", "staff_exists");
        if (await staff.CountAsync(s => s.VenueId == venue.Id, ct) >= MaxStaff)
            throw new BusinessRuleException($"A venue can have up to {MaxStaff} staff.", "staff_limit");

        var name = req.Name?.Trim();
        await staff.AddAsync(new VenueStaff
        {
            Id = Guid.NewGuid(),
            VenueId = venue.Id,
            Email = email,
            Name = string.IsNullOrWhiteSpace(name) ? null : name.Length > 120 ? name[..120] : name,
            CreatedAt = DateTimeOffset.UtcNow,
        }, ct);
        await uow.SaveChangesAsync(ct);
        return await DescribeAsync(venue, ct);
    }

    public async Task<VenueDto> RemoveStaffAsync(Guid staffId, CancellationToken ct = default)
    {
        var venue = await OwnedAsync(ct);
        var row = await staff.Query(tracking: true).FirstOrDefaultAsync(s => s.Id == staffId && s.VenueId == venue.Id, ct)
                  ?? throw new NotFoundException("They're no longer on your staff.");
        staff.Remove(row);
        await uow.SaveChangesAsync(ct);
        return await DescribeAsync(venue, ct);
    }

    public async Task<VenueEventDto> CreateEventAsync(CreateVenueEventRequest req, CancellationToken ct = default)
    {
        var venue = await MineAsync(ct);
        if (!await PlanActiveAsync(venue, ct))
            throw new BusinessRuleException("The venue's plan has ended, so it can't start new events.", "venue_plan_ended");

        var created = await campaignService.CreateBareAsync(req.Title, req.EventDate, ct);
        var campaign = await campaigns.Query(tracking: true).FirstAsync(c => c.Id == created.CampaignId, ct);
        campaign.VenueId = venue.Id;
        campaign.UpdatedAt = DateTimeOffset.UtcNow;
        campaigns.Update(campaign);
        await uow.SaveChangesAsync(ct);

        await bucketService.CreateForCampaignAsync(campaign.Id, ct);
        return (await EventsAsync(venue.Id, ct)).First(e => e.CampaignId == campaign.Id);
    }

    /// <summary>The venue the caller owns, or works at. An account on the Venue plan gets one on its first visit.</summary>
    private async Task<Venue> MineAsync(CancellationToken ct)
    {
        var me = currentUser.UserId ?? throw new ForbiddenException("Sign in to open your venue.");
        var owned = await venues.Query(tracking: true).FirstOrDefaultAsync(v => v.OwnerUserId == me, ct);
        if (owned is not null) return owned;

        var account = await users.GetByIdAsync(me, ct) ?? throw new ForbiddenException("Sign in to open your venue.");
        var email = account.Email?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(email))
        {
            var workAt = await staff.Query().Where(s => s.Email == email).Select(s => (Guid?)s.VenueId).FirstOrDefaultAsync(ct);
            if (workAt is { } id && await venues.GetByIdAsync(id, ct) is { } place) return place;
        }

        if (account.SubscriptionTier != SubscriptionTier.Venue
            || !PlanRules.IsActive(account.SubscriptionTier, account.SubscriptionEndsAt, DateTimeOffset.UtcNow))
            throw new ForbiddenException("This page comes with the Venue plan.", "venue_required");

        var now = DateTimeOffset.UtcNow;
        var venue = new Venue
        {
            Id = Guid.NewGuid(),
            OwnerUserId = me,
            Name = string.IsNullOrWhiteSpace(account.DisplayName) ? "Your venue" : account.DisplayName,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await venues.AddAsync(venue, ct);
        await uow.SaveChangesAsync(ct);
        return venue;
    }

    private async Task<Venue> OwnedAsync(CancellationToken ct)
    {
        var venue = await MineAsync(ct);
        if (venue.OwnerUserId != currentUser.UserId)
            throw new ForbiddenException("Only the venue's owner can change this.", "venue_owner_only");
        return venue;
    }

    private async Task<bool> PlanActiveAsync(Venue venue, CancellationToken ct) =>
        await users.GetByIdAsync(venue.OwnerUserId, ct) is { SubscriptionTier: SubscriptionTier.Venue } owner
        && PlanRules.IsActive(owner.SubscriptionTier, owner.SubscriptionEndsAt, DateTimeOffset.UtcNow);

    private async Task<VenueDto> DescribeAsync(Venue venue, CancellationToken ct)
    {
        await EnsureCodeAsync(venue, ct);
        var owner = await users.GetByIdAsync(venue.OwnerUserId, ct);
        var isOwner = venue.OwnerUserId == currentUser.UserId;
        var people = isOwner
            ? await staff.Query().Where(s => s.VenueId == venue.Id).OrderBy(s => s.CreatedAt)
                .Select(s => new VenueStaffDto(s.Id, s.Email, s.Name, s.CreatedAt)).ToListAsync(ct)
            : [];
        return new VenueDto(
            venue.Id, venue.Name, venue.Place, venue.LogoUrl, isOwner,
            await PlanActiveAsync(venue, ct), owner?.SubscriptionEndsAt,
            PlanCatalog.VenueAccountBytes, await plans.VenueUsedBytesAsync(venue.Id, ct),
            people, await EventsAsync(venue.Id, ct), venue.Code);
    }

    private async Task<IReadOnlyList<VenueEventDto>> EventsAsync(Guid venueId, CancellationToken ct)
    {
        var rows = await campaigns.Query()
            .Where(c => c.VenueId == venueId && c.Status != CampaignStatus.Cancelled)
            .OrderByDescending(c => c.EventStartAt)
            .Select(c => new { c.Id, c.Title, c.EventStartAt, c.TemplateId })
            .ToListAsync(ct);
        if (rows.Count == 0) return [];

        var ids = rows.Select(r => r.Id).ToList();
        var templateIds = rows.Select(r => r.TemplateId).Distinct().ToList();
        // An albums-only event hangs off a placeholder template whose slug says so (CreateBareAsync).
        var bare = await templates.Query()
            .Where(t => templateIds.Contains(t.Id) && t.Slug.StartsWith("bare-"))
            .Select(t => t.Id)
            .ToListAsync(ct);
        var albums = await buckets.Query()
            .Where(b => ids.Contains(b.CampaignId))
            .GroupBy(b => b.CampaignId)
            .Select(g => new { g.Key, Count = g.Count(), Used = g.Sum(b => b.UsedBytes) })
            .ToDictionaryAsync(x => x.Key, ct);
        var photoCounts = await photos.Query()
            .Where(p => p.CampaignId != null && ids.Contains(p.CampaignId!.Value) && p.DeletedAt == null)
            .GroupBy(p => p.CampaignId!.Value)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        return rows.Select(r => new VenueEventDto(
            r.Id, r.Title, r.EventStartAt,
            albums.GetValueOrDefault(r.Id)?.Count ?? 0,
            photoCounts.GetValueOrDefault(r.Id),
            albums.GetValueOrDefault(r.Id)?.Used ?? 0,
            !bare.Contains(r.TemplateId))).ToList();
    }
}
