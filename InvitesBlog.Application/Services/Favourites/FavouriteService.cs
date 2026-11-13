using InvitesBlog.Application.Plans;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Favourites;
using InvitesBlog.Application.Dtos.Photos;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Services.Invites;
using InvitesBlog.Application.Services.Photos;
using InvitesBlog.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Services.Favourites;

/// <summary>Hearts: someone's Favourites, and the counts every album shows.</summary>
public interface IFavouriteService
{
    /// <summary>Hearts an item the caller can see. Idempotent.</summary>
    Task<FavouriteStateDto> AddAsync(Guid photoId, CancellationToken ct = default);

    /// <summary>Takes the heart back. Idempotent, and allowed whether or not the caller can still see it.</summary>
    Task<FavouriteStateDto> RemoveAsync(Guid photoId, CancellationToken ct = default);

    /// <summary>The caller's Favourites they can still see, newest heart first.</summary>
    Task<FavouritesPageDto> ListAsync(int page, int pageSize, CancellationToken ct = default);

    /// <summary>A zip of the named favourites the caller can still see.</summary>
    Task<PhotoArchive> PlanArchiveAsync(IReadOnlyCollection<Guid>? ids, CancellationToken ct = default);
}

/// <summary>
/// <para><b>Who may heart what.</b> Exactly who may see it, and the answer comes from the album reads
/// themselves (<see cref="IEventPhotoService.GetAsync"/> through the event, then
/// <see cref="IEventPhotoService.GetBucketAsync"/> through the album), rather than from a second copy of
/// their rules: restricted albums, the organiser-only phase, reports and blocks all apply here because
/// they apply there, and can never drift apart.</para>
///
/// <para>A heart is a reference, never a copy. When the item is deleted or the caller can no longer see
/// it, it leaves their Favourites; the row stays, so access coming back brings it back.</para>
/// </summary>
public sealed class FavouriteService(
    IRepository<Favourite> favourites,
    IRepository<EventPhoto> photos,
    IRepository<AppUser> users,
    IEventPhotoService albums,
    IInviteService invites,
    ICurrentUser currentUser,
    IUnitOfWork uow,
    ICampaignRepository? campaigns = null) : IFavouriteService
{
    public const int MaxPageSize = 200;

    private Guid Me => currentUser.UserId ?? throw new UnauthorizedException("Sign in to keep favourites.");

    public async Task<FavouriteStateDto> AddAsync(Guid photoId, CancellationToken ct = default)
    {
        var me = Me;
        var photo = await photos.Query().FirstOrDefaultAsync(p => p.Id == photoId && p.DeletedAt == null, ct)
                    ?? throw new NotFoundException("That photo is no longer there.");
        var album = await AlbumAsync(photo.CampaignId, photo.BucketId, ct);
        if (album is null || !album.Items.ContainsKey(photoId))
            throw new ForbiddenException("You can only keep photos from albums you can see.");

        if (!await favourites.AnyAsync(f => f.UserId == me && f.PhotoId == photoId, ct))
        {
            await favourites.AddAsync(new Favourite
            {
                Id = Guid.NewGuid(), UserId = me, PhotoId = photoId, CreatedAt = DateTimeOffset.UtcNow,
            }, ct);
            try
            {
                await uow.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                // A double tap racing itself: the other request already saved the same heart.
                if (!await favourites.AnyAsync(f => f.UserId == me && f.PhotoId == photoId, ct)) throw;
            }
        }
        return new FavouriteStateDto(await CountAsync(photoId, ct), true);
    }

    public async Task<FavouriteStateDto> RemoveAsync(Guid photoId, CancellationToken ct = default)
    {
        var me = Me;
        var rows = await favourites.Query(tracking: true)
            .Where(f => f.UserId == me && f.PhotoId == photoId).ToListAsync(ct);
        if (rows.Count > 0)
        {
            favourites.RemoveRange(rows);
            await uow.SaveChangesAsync(ct);
        }
        return new FavouriteStateDto(await CountAsync(photoId, ct), false);
    }

    public async Task<FavouritesPageDto> ListAsync(int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var visible = await VisibleAsync(ct);
        var items = visible.Skip((page - 1) * pageSize).Take(pageSize).ToList();
        return new FavouritesPageDto(items, visible.Count, page, pageSize);
    }

    public async Task<PhotoArchive> PlanArchiveAsync(IReadOnlyCollection<Guid>? ids, CancellationToken ct = default)
    {
        var wanted = ids is { Count: > 0 } ? ids.ToHashSet() : null;
        var chosen = (await VisibleAsync(ct)).Where(f => wanted is null || wanted.Contains(f.Id)).ToList();
        if (chosen.Count == 0)
            throw new BusinessRuleException("There are no favourites to download.", "no_photos");
        // Up to ten at a time; all of them, or more, with the account's own Premium or Studio, or when every one
        // chosen is in an event with a Gold or Platinum pass in force (each of those may be downloaded whole).
        if (!DownloadRules.WithinLimit(ids) && !await MayDownloadAllAsync(chosen, ct))
            throw DownloadRules.Refusal();
        return new PhotoArchive(Guid.Empty, "favourites.zip",
            chosen.OrderBy(f => f.CreatedAt).Select(f => f.Id).ToList(), chosen.Sum(f => f.SizeBytes));
    }

    private async Task<bool> MayDownloadAllAsync(IReadOnlyList<FavouriteDto> chosen, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        if (DownloadRules.AccountAllows(await users.GetByIdAsync(Me, ct), now)) return true;
        if (campaigns is null || chosen.Any(f => f.CampaignId is null)) return false;
        var ids = chosen.Select(f => f.CampaignId!.Value).Distinct().ToList();
        var events = await campaigns.Query().Where(c => ids.Contains(c.Id)).ToListAsync(ct);
        return events.Count == ids.Count && events.All(c => DownloadRules.EventAllows(c, now));
    }

    /// <summary>Every favourite the caller can still see, newest heart first. Each album's door is asked once.</summary>
    private async Task<List<FavouriteDto>> VisibleAsync(CancellationToken ct)
    {
        var me = Me;
        var mine = await favourites.Query()
            .Where(f => f.UserId == me)
            .Join(photos.Query().Where(p => p.DeletedAt == null), f => f.PhotoId, p => p.Id,
                (f, p) => new { f.PhotoId, f.CreatedAt, p.CampaignId, p.BucketId })
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(ct);

        var albumsSeen = new Dictionary<(Guid?, Guid?), Album?>();
        var result = new List<FavouriteDto>();
        foreach (var f in mine)
        {
            var key = (f.CampaignId, f.BucketId);
            if (!albumsSeen.TryGetValue(key, out var album))
                albumsSeen[key] = album = await AlbumAsync(f.CampaignId, f.BucketId, ct);
            if (album is null || !album.Items.TryGetValue(f.PhotoId, out var p)) continue;
            result.Add(new FavouriteDto(
                p.Id, p.Url, p.ThumbUrl, p.OriginalUrl, p.ContentType, p.Width, p.Height, p.UploaderName,
                false, p.CreatedAt, p.UploaderUserId, p.SizeBytes, p.FavouriteCount, true,
                f.BucketId, f.CampaignId, album.Title, f.CreatedAt));
        }
        return result;
    }

    private sealed record Album(string Title, Dictionary<Guid, EventPhotoDto> Items);

    /// <summary>
    /// What the caller sees of one album, through the event's door and then the album's own, or null
    /// when neither lets them in.
    /// </summary>
    private async Task<Album?> AlbumAsync(Guid? campaignId, Guid? bucketId, CancellationToken ct)
    {
        if (campaignId is { } cid)
        {
            try
            {
                var box = await albums.GetAsync(cid, await invites.MyGuestIdAsync(cid, ct), ct);
                return new Album(box.EventTitle, box.Photos.ToDictionary(p => p.Id));
            }
            catch (Exception e) when (e is ForbiddenException or NotFoundException) { }
        }
        if (bucketId is { } bid)
        {
            try
            {
                var box = await albums.GetBucketAsync(bid, ct);
                return new Album(box.EventTitle, box.Photos.ToDictionary(p => p.Id));
            }
            catch (Exception e) when (e is ForbiddenException or NotFoundException) { }
        }
        return null;
    }

    private Task<int> CountAsync(Guid photoId, CancellationToken ct) =>
        favourites.Query()
            .Where(f => f.PhotoId == photoId && users.Query().Any(u => u.Id == f.UserId && u.IsActive))
            .CountAsync(ct);
}
