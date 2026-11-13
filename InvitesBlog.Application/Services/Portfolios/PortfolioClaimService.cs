using System.Net;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Plans;
using InvitesBlog.Application.Services.Campaigns;
using InvitesBlog.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Services.Portfolios;

/// <summary>
/// "These are mine" (docs/PORTFOLIO-HANDOFF.md, "Fix 2026-11-13 night"). A studio often shoots through its own QR
/// code or a guest link without signing in, so its photos arrive under a name ("Immy") and no account. The organiser,
/// once consent is on, sees the names in the event and claims the ones that are the studio: those photos become its
/// own uploads. Only uploads with no account can be claimed; someone who was signed in is never re-labelled.
/// </summary>
public interface IPortfolioClaimService
{
    Task<PortfolioUploadersDto> UploadersAsync(Guid campaignId, CancellationToken ct = default);
    Task<PortfolioClaimResultDto> ClaimAsync(Guid campaignId, string? key, CancellationToken ct = default);
    Task<PortfolioClaimResultDto> UnclaimAsync(Guid campaignId, string? key, CancellationToken ct = default);
}

public sealed record PortfolioUploaderGroupDto(
    string Key, string Name, int PhotoCount, int VideoCount, IReadOnlyList<string> ThumbUrls, bool Claimed, DateTimeOffset? ClaimedAt);

public sealed record PortfolioUploadersDto(bool Consented, IReadOnlyList<PortfolioUploaderGroupDto> Groups);

public sealed record PortfolioClaimResultDto(int Count, int RemovedFromPortfolio, string Message, PortfolioUploadersDto Uploaders);

public sealed record PortfolioClaimRequest(string? Key);

public sealed class PortfolioClaimService(
    ICurrentUser currentUser,
    IRepository<AppUser> users,
    IRepository<EventPhoto> eventPhotos,
    IRepository<PortfolioConsent> consents,
    IRepository<PortfolioPhotoClaim> claims,
    ICampaignOwnershipService ownership,
    IPortfolioService portfolio,
    IUnitOfWork uow) : IPortfolioClaimService
{
    private const int Thumbs = 4;

    public async Task<PortfolioUploadersDto> UploadersAsync(Guid campaignId, CancellationToken ct = default)
    {
        var me = await GuardAsync(campaignId, needConsent: false, ct);
        return await DtoAsync(me, campaignId, ct);
    }

    public async Task<PortfolioClaimResultDto> ClaimAsync(Guid campaignId, string? key, CancellationToken ct = default)
    {
        var me = await GuardAsync(campaignId, needConsent: true, ct);
        var rows = (await UnownedAsync(campaignId, tracking: true, ct)).Where(x => KeyOf(x) == key).ToList();
        if (rows.Count == 0) throw NotThere();

        var now = DateTimeOffset.UtcNow;
        // Claiming the same name again (new uploads since) adds to the claim already in force.
        var claim = await claims.Query(tracking: true)
            .Where(x => x.CampaignId == campaignId && x.UserId == me && x.Key == key && x.UndoneAt == null)
            .FirstOrDefaultAsync(ct);
        if (claim is null)
        {
            claim = new PortfolioPhotoClaim
            {
                Id = Guid.NewGuid(), UserId = me, CampaignId = campaignId, Key = key!,
                Name = Trim(NameOf(rows), 200), CreatedAt = now,
            };
            await claims.AddAsync(claim, ct);
        }
        claim.PhotoCount += rows.Count;
        foreach (var x in rows)
        {
            x.UploaderUserId = me;
            x.ClaimId = claim.Id;
        }
        await uow.SaveChangesAsync(ct);

        var message = rows.Count == 1
            ? "1 photo is now yours. You can add it to your portfolio."
            : $"{rows.Count} photos are now yours. You can add them to your portfolio.";
        return new PortfolioClaimResultDto(rows.Count, 0, message, await DtoAsync(me, campaignId, ct));
    }

    public async Task<PortfolioClaimResultDto> UnclaimAsync(Guid campaignId, string? key, CancellationToken ct = default)
    {
        var me = await GuardAsync(campaignId, needConsent: false, ct);
        var active = await claims.Query(tracking: true)
            .Where(x => x.CampaignId == campaignId && x.UserId == me && x.Key == key && x.UndoneAt == null)
            .ToListAsync(ct);
        if (active.Count == 0) throw NotThere();
        var ids = active.Select(x => (Guid?)x.Id).ToList();
        var rows = await eventPhotos.Query(tracking: true).Where(x => x.CampaignId == campaignId && ids.Contains(x.ClaimId)).ToListAsync(ct);

        // Out of the portfolio first: they are no longer the studio's own photos.
        var removed = await portfolio.RemoveSourcesAsync(rows.Select(x => x.Id).ToList(), ct);
        foreach (var x in rows)
        {
            x.UploaderUserId = null;
            x.ClaimId = null;
        }
        var now = DateTimeOffset.UtcNow;
        foreach (var c in active) c.UndoneAt = now;
        await uow.SaveChangesAsync(ct);

        var message = (rows.Count == 1 ? "1 photo is no longer marked as yours." : $"{rows.Count} photos are no longer marked as yours.")
                      + (removed == 0 ? "" : removed == 1 ? " 1 left your portfolio." : $" {removed} left your portfolio.");
        return new PortfolioClaimResultDto(rows.Count, removed, message, await DtoAsync(me, campaignId, ct));
    }

    /// <summary>Studio in force, the event's organiser, and (to claim) consent given. Returns the caller's id.</summary>
    private async Task<Guid> GuardAsync(Guid campaignId, bool needConsent, CancellationToken ct)
    {
        var id = currentUser.UserId ?? throw new UnauthorizedException();
        var user = await users.GetByIdAsync(id, ct) ?? throw new UnauthorizedException();
        if (!AddOns.StudioActive(user, DateTimeOffset.UtcNow)) throw PortfolioService.StudioRequired();
        if (await ownership.AccessAsync(campaignId, ct) != CampaignAccess.Organiser)
            throw new CodedRuleException("Only the event's organiser can do this.", "not_organiser", HttpStatusCode.Forbidden);
        if (needConsent && !await consents.AnyAsync(x => x.UserId == id && x.CampaignId == campaignId, ct))
            throw new CodedRuleException(
                "First confirm, in the album's settings, that the people this event is for agreed to its photos being shown.",
                "no_consent", HttpStatusCode.Forbidden);
        return id;
    }

    private async Task<PortfolioUploadersDto> DtoAsync(Guid me, Guid campaignId, CancellationToken ct)
    {
        var groups = new List<(PortfolioUploaderGroupDto Dto, DateTimeOffset Latest)>();
        foreach (var g in (await UnownedAsync(campaignId, tracking: false, ct)).GroupBy(KeyOf).Where(g => g.Key is not null))
            groups.Add((Group(g.Key!, g.ToList(), null), g.Max(x => x.CreatedAt)));

        var active = await claims.Query()
            .Where(x => x.CampaignId == campaignId && x.UserId == me && x.UndoneAt == null).ToListAsync(ct);
        if (active.Count > 0)
        {
            var ids = active.Select(x => (Guid?)x.Id).ToList();
            var rows = await eventPhotos.Query()
                .Where(x => x.CampaignId == campaignId && x.DeletedAt == null && ids.Contains(x.ClaimId) && x.UploaderUserId == me)
                .ToListAsync(ct);
            foreach (var c in active.GroupBy(x => x.Key))
            {
                var mine = rows.Where(r => c.Any(x => x.Id == r.ClaimId)).ToList();
                if (mine.Count == 0) continue;
                // A name claimed and then used again later shows twice: claimed, and the new ones still waiting (same key).
                groups.Add((Group(c.Key, mine, c.Min(x => x.CreatedAt)) with { Name = c.First().Name }, mine.Max(x => x.CreatedAt)));
            }
        }

        var consented = await consents.AnyAsync(x => x.UserId == me && x.CampaignId == campaignId, ct);
        var ordered = groups.OrderByDescending(x => x.Dto.PhotoCount + x.Dto.VideoCount).ThenBy(x => x.Dto.Name, StringComparer.OrdinalIgnoreCase)
            .Select(x => x.Dto).ToList();
        return new PortfolioUploadersDto(consented, ordered);
    }

    private static PortfolioUploaderGroupDto Group(string key, IReadOnlyList<EventPhoto> rows, DateTimeOffset? claimedAt)
    {
        var videos = rows.Count(IsVideo);
        var newest = rows.OrderByDescending(x => x.CreatedAt).ToList();
        // Photos first in the preview: a video's tile is a frame, but a photo says more about who took it.
        var thumbs = newest.Where(x => !IsVideo(x)).Concat(newest.Where(IsVideo)).Take(Thumbs).Select(x => x.ThumbUrl).ToList();
        return new PortfolioUploaderGroupDto(key, NameOf(rows), rows.Count - videos, videos, thumbs, claimedAt is not null, claimedAt);
    }

    /// <summary>The event's live uploads that no account made: what can be claimed.</summary>
    private async Task<List<EventPhoto>> UnownedAsync(Guid campaignId, bool tracking, CancellationToken ct) =>
        await eventPhotos.Query(tracking)
            .Where(x => x.CampaignId == campaignId && x.DeletedAt == null && x.UploaderUserId == null)
            .ToListAsync(ct);

    /// <summary>"guest:{id}" for a guest-link upload, "name:{lower-case name}" for a QR/camera one; null when neither (an old host upload).</summary>
    internal static string? KeyOf(EventPhoto x) =>
        x.GuestId is { } g ? $"guest:{g}"
        : string.IsNullOrWhiteSpace(x.UploaderName) ? null
        : $"name:{Trim(x.UploaderName, 120).ToLowerInvariant()}";

    private static string NameOf(IReadOnlyList<EventPhoto> rows) =>
        rows.OrderByDescending(x => x.CreatedAt).Select(x => x.UploaderName?.Trim()).FirstOrDefault(n => !string.IsNullOrEmpty(n)) ?? "A guest";

    private static bool IsVideo(EventPhoto x) => x.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);

    private static string Trim(string s, int max)
    {
        var t = string.Join(' ', s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return t.Length > max ? t[..max] : t;
    }

    private static NotFoundException NotThere() =>
        new("Those photos aren't there any more. Refresh and try again.", "uploader_not_found");
}
