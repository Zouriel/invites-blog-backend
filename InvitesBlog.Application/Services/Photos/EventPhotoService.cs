using System.IO.Compression;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Common;
using InvitesBlog.Application.Dtos.Photos;
using InvitesBlog.Application.Events;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Services.Campaigns;
using InvitesBlog.Application.Services.MediaBuckets;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;

using InvitesBlog.Application.Plans;
namespace InvitesBlog.Application.Services.Photos;

/// <summary>The event photo box (§5): what guests shot at the party, for everyone who was there.</summary>
public interface IEventPhotoService
{
    /// <summary>
    /// The box as <paramref name="viewerGuestId"/> sees it. Pass the guest whose invitation is open
    /// when the caller is a guest, so their own photos come back deletable; pass null for a host,
    /// whose right to delete comes from moderation instead.
    /// </summary>
    Task<EventPhotoBoxDto> GetAsync(Guid campaignId, Guid? viewerGuestId, CancellationToken ct = default);

    /// <summary>
    /// Adds one photo, or one video. <paramref name="guestId"/> is the guest who took it — null when
    /// the host adds one themselves.
    /// </summary>
    /// <param name="poster">
    /// A still to stand in for a video in the grid, required for a video and ignored for a photo.
    ///
    /// <para><b>Why the client sends it.</b> Pulling a frame out of a video server-side means ffmpeg
    /// in the API image — a large dependency, a process per upload, and a decoder of untrusted media
    /// running next to the session. The browser that recorded the video already had the frame on
    /// screen, so it hands one over and the server stays a server. The cost is that a video without
    /// a poster is refused rather than guessed at, which is the right way round: a grid of black
    /// rectangles is worse than an upload that failed loudly.</para>
    /// </param>
    Task<EventPhotoDto> AddAsync(
        Guid campaignId, Guid? guestId, byte[] content, string contentType, string fileName,
        byte[]? poster = null, CancellationToken ct = default);

    /// <summary>
    /// A BUCKET's contents, for its owner.
    ///
    /// <para>Separate from <see cref="GetAsync"/> because a standalone bucket has no campaign to be
    /// read by, and its owner would otherwise have bought a product they cannot open. Authorized by
    /// owning the bucket rather than by owning an event — for a bucket attached to a campaign those
    /// are the same person, and for one that is not there is no event to ask about.</para>
    /// </summary>
    Task<EventPhotoBoxDto> GetBucketAsync(Guid bucketId, CancellationToken ct = default);

    /// <summary>Removes an item from a bucket. The bucket's owner only — a contributor cannot.</summary>
    Task DeleteFromBucketAsync(Guid bucketId, Guid photoId, CancellationToken ct = default);

    /// <summary>
    /// Adds one item on behalf of somebody who scanned a bucket's QR code.
    ///
    /// <para><b>Authorizes nothing.</b> The caller has already turned a printed token into this
    /// bucket id; a contributor has no account, no guest row and no session to check against. What
    /// this door does NOT do is as important as what it does — it never reads the bucket back to
    /// them, and the name it credits is a label, not an identity.</para>
    /// </summary>
    /// <param name="uploaderName">What to credit it to. Never trusted as proof of who anyone is.</param>
    Task<EventPhotoDto> AddToBucketAsync(
        Guid bucketId, Guid? campaignId, string? uploaderName, byte[] content, string contentType,
        string fileName, byte[]? poster = null, CancellationToken ct = default);

    /// <summary>
    /// Removes a photo. Allowed for the guest who took it (<paramref name="actingGuestId"/>) or for a
    /// host holding <see cref="Permissions.Photos.Moderate"/> over the campaign.
    /// </summary>
    Task DeleteAsync(Guid campaignId, Guid photoId, Guid? actingGuestId, CancellationToken ct = default);

    /// <summary>
    /// Writes the originals as a zip into <paramref name="destination"/>. Pass the ids to take some of
    /// them, or null/empty for the whole box.
    ///
    /// <para>Gated by the same two doors as <see cref="GetAsync"/> and nothing more: anyone who can
    /// SEE the gallery can keep what is in it. Everyone in these photographs was at the same party.</para>
    /// </summary>
    /// <param name="begin">
    /// Called once with the filename to offer, after the caller is authorized and there is known to be
    /// something to send, and before a single byte is written. It returns the stream to write into.
    /// The handshake exists because a response's headers are gone the moment the body starts — a name
    /// worked out afterwards would never reach the browser.
    /// </param>
    Task WriteArchiveAsync(
        Guid campaignId, Guid? viewerGuestId, IReadOnlyCollection<Guid>? ids,
        Func<string, Stream> begin, CancellationToken ct = default);
}

/// <inheritdoc cref="IEventPhotoService"/>
public sealed class EventPhotoService(
    IRepository<EventPhoto> photos,
    ICampaignRepository campaigns,
    IGuestRepository guestRepository,
    ICampaignOwnershipService ownership,
    ICurrentUser currentUser,
    IStorageService storage,
    IImageOptimizer imageOptimizer,
    IMediaBucketService bucketService,
    IUnitOfWork uow,
    IRepository<Venue> venues,
    Plans.IPlanService plans) : IEventPhotoService
{
    /// <summary>
    /// What a tap opens. NOT what a download hands over — the original is kept for that, so this is
    /// free to be a screen-sized copy rather than a compromise between viewing and keeping.
    /// </summary>
    private const int ViewEdge = 2048;

    /// <summary>
    /// Grid size. The box is the one screen guaranteed to render hundreds of images at once, on a
    /// phone, at an event — serving the viewing size into a 120px tile is what makes it crawl.
    /// </summary>
    private const int ThumbEdge = 400;

    /// <summary>
    /// The one ceiling in this file, and it is on videos only.
    ///
    /// <para>Photographs are deliberately uncapped below — nobody's memory of the night should be
    /// refused for being large. A video is a different quantity: the upload is buffered into a byte
    /// array before it reaches here, so an unbounded one is a phone being able to decide how much of
    /// the API's memory it takes. The camera stops recording well inside this, so the cap is a
    /// backstop against a caller that is not the camera rather than a limit anyone should meet.</para>
    /// </summary>
    private const long MaxVideoBytes = 256L * 1024 * 1024;

    /// <summary>One stored derivative: where it went, what it weighed, and how big it was.</summary>
    private sealed record Derivative(string Url, long Bytes, int Width, int Height);

    public async Task<EventPhotoBoxDto> GetAsync(Guid campaignId, Guid? viewerGuestId, CancellationToken ct = default)
    {
        var campaign = await campaigns.GetByIdAsync(campaignId, ct)
                       ?? throw new NotFoundException("That event no longer exists.");

        var moderates = await MayHostAsync(campaignId, ct);

        // Thirty days after a plan runs out, only the organiser can still look.
        if (!moderates && await bucketService.PhaseForCampaignAsync(campaignId, ct) >= MediaPhase.OrganiserOnly)
            throw new ForbiddenException("The photos from this event are only available to its organiser now.");
        var view = await ViewForAsync(campaignId, moderates, viewerGuestId, ct);

        var live = (await photos.Query()
                .Where(p => p.CampaignId == campaignId && p.DeletedAt == null)
                .OrderByDescending(p => p.CreatedAt)
                .ToListAsync(ct))
            .Where(p => view is null || view.MaySee(p.BucketId))
            .ToList();

        // The same three questions StoreAsync asks, asked in the same order — cancelled, then the
        // night. This used to report only the cancellation, so an event six months past still offered
        // "Add media", and pressing it produced the refusal the box should have shown instead.
        // The bucket's own window, so the box and the bucket cannot disagree about whether it is
        // still open — a Wedding pass's five-day album would otherwise be told it had closed after one.
        var windowDays = await bucketService.WindowForCampaignAsync(campaignId, ct);

        var venue = campaign.VenueId is { } venueId ? await venues.GetByIdAsync(venueId, ct) : null;
        // The plan's side of the same answer the writer gives: its cover and its space.
        var plan = await plans.ForCampaignAsync(campaignId, ct);
        var eventUsed = await bucketService.EventUsedBytesAsync(campaignId, ct);

        var closed =
            campaign.Status == CampaignStatus.Cancelled
                ? "This event has been cancelled. Everything already added is still here."
                : campaign.Kind == CampaignKind.SaveTheDate
                    ? InvitesBlog.Application.Campaigns.SaveTheDates.NoAlbumMessage
                : plan.Phase != Plans.MediaPhase.Active
                    ? "This event's photos are no longer collecting. Everything already added is still here for now."
                : !EventDayWindow.IsOpen(campaign.EventStartAt, DateTimeOffset.UtcNow, windowDays)
                    ? DateTimeOffset.UtcNow < campaign.EventStartAt
                        ? "This one isn't open yet — it opens the day before the event."
                        : "This one has closed. Everything already added is still here."
                : eventUsed >= plan.EventBytes
                    ? "This event's space is full. Everything already added is still here."
                    : null;

        return new EventPhotoBoxDto(
            campaign.Id,
            campaign.Title,
            live.Count,
            closed is null,
            live.Select(p => new EventPhotoDto(
                p.Id, p.Url, p.ThumbUrl, p.OriginalUrl, p.ContentType, p.Width, p.Height, p.UploaderName,
                moderates || (viewerGuestId is { } me && p.GuestId == me),
                p.CreatedAt)).ToList(),
            closed,
            venue?.Name,
            venue?.LogoUrl,
            campaign.Kind == CampaignKind.SaveTheDate);
    }

    public async Task<EventPhotoDto> AddAsync(
        Guid campaignId, Guid? guestId, byte[] content, string contentType, string fileName,
        byte[]? poster = null, CancellationToken ct = default)
    {
        var campaign = await campaigns.GetByIdAsync(campaignId, ct)
                       ?? throw new NotFoundException("That event no longer exists.");

        // Who is adding this decides which door they came through: a guest must be ON the guest list,
        // and anyone without a guest row is claiming to be the host and has to prove it.
        if (guestId is null
            ? !await MayHostAsync(campaignId, ct)
            : !await IsGuestOfAsync(campaignId, guestId, ct))
            throw new ForbiddenException("You can only add photos to an event you were invited to.");

        // A cancelled event's box is a read-only archive. Whatever was already shot stays; nothing new
        // arrives, so a cancelled party cannot keep accruing storage against the host's name.
        if (campaign.Status == CampaignStatus.Cancelled)
            throw new BusinessRuleException("This event has been cancelled.", "campaign_cancelled");

        var bucket = await bucketService.ForCampaignAsync(campaign.Id, ct);
        var credit = guestId is null ? null : await GuestNameAsync(guestId.Value, ct);

        return await StoreAsync(
            bucket.Id, campaign.Id, guestId, credit, content, contentType, fileName, poster, ct);
    }

    public async Task<EventPhotoBoxDto> GetBucketAsync(Guid bucketId, CancellationToken ct = default)
    {
        // The VIEW door, not the ownership one: a bucket's contents are what its members are for, and
        // gating them on ownership is what left everybody who filled a standalone bucket locked out
        // of it. Managing it — renaming, resizing, handing out codes — still needs ownership.
        var bucket = await bucketService.ViewAsync(bucketId, ct);
        var mine = await bucketService.OwnsAsync(bucketId, ct);

        var live = await photos.Query()
            .Where(p => p.BucketId == bucketId && p.DeletedAt == null)
            .OrderByDescending(p => p.CreatedAt)
            .ToListAsync(ct);

        return new EventPhotoBoxDto(
            bucket.CampaignId ?? Guid.Empty,
            bucket.Title,
            live.Count,
            // Full, out of term, or off its night: the owner can still look at what is already in it.
            // Only adding stops. The night was missing here — the same gap the campaign box had.
            bucket.IsOpen && !bucket.Expired && bucket.EventUsedBytes < bucket.CapacityBytes,
            live.Select(p => new EventPhotoDto(
                p.Id, p.Url, p.ThumbUrl, p.OriginalUrl, p.ContentType, p.Width, p.Height,
                p.UploaderName,
                // The owner may remove anything in their own bucket. A member may look and download
                // and nothing else — these are photographs of an occasion that is not theirs to
                // curate, and the moderation right belongs to whoever the occasion belonged to.
                mine,
                p.CreatedAt)).ToList(),
            // Same sentence the writer would have given, and in the order that matters: "this closed
            // a week ago" is more useful than "this is full" when both are true.
            !bucket.IsOpen
                ? DateTimeOffset.UtcNow < bucket.EventDate
                    ? "This one isn't open yet — it opens the day before the event."
                    : "This one has closed. Everything already added is still here."
                : bucket.Expired
                    ? "This event's plan has ended. Everything already here is kept for now."
                    : bucket.EventUsedBytes >= bucket.CapacityBytes
                        ? "This event is out of space. See the plans for more."
                        : null,
            bucket.VenueName,
            bucket.VenueLogoUrl);
    }

    public async Task DeleteFromBucketAsync(
        Guid bucketId, Guid photoId, CancellationToken ct = default)
    {
        await bucketService.GetAsync(bucketId, ct);   // ownership, or it throws

        var photo = await photos.Query(tracking: true)
            .FirstOrDefaultAsync(p => p.Id == photoId && p.BucketId == bucketId, ct)
            ?? throw new NotFoundException("That photo is no longer here.");

        if (photo.DeletedAt is not null) return;   // deleting twice is not an error

        photo.DeletedAt = DateTimeOffset.UtcNow;
        photos.Update(photo);
        await uow.SaveChangesAsync(ct);
    }

    public async Task<EventPhotoDto> AddToBucketAsync(
        Guid bucketId, Guid? campaignId, string? uploaderName, byte[] content, string contentType,
        string fileName, byte[]? poster = null, CancellationToken ct = default)
    {
        // NO DOOR HERE, on purpose. The only caller is the QR contribution endpoint, and by the time
        // it gets here it has already resolved a printed token to this exact bucket and checked the
        // code has not been revoked. Re-deriving that from a contributor would mean inventing an
        // identity for somebody who deliberately has none.
        return await StoreAsync(
            bucketId, campaignId, guestId: null, uploaderName, content, contentType, fileName,
            poster, ct);
    }

    /// <summary>
    /// Everything an upload does once it is known to be allowed: validate the media, make the
    /// derivatives, write the objects, and record the row and what it cost.
    ///
    /// <para>Shared by both doors deliberately. The rules about what a photo box will accept — the
    /// video's poster, the format, the absent size cap, the EXIF that always goes — are properties of
    /// the box, not of who happened to be adding, and having them in one place is what stops the two
    /// callers drifting into accepting different things.</para>
    /// </summary>
    private async Task<EventPhotoDto> StoreAsync(
        Guid bucketId, Guid? campaignId, Guid? guestId, string? uploaderName, byte[] content,
        string contentType, string fileName, byte[]? poster, CancellationToken ct)
    {
        if (content.Length == 0)
            throw new BusinessRuleException("That photo file is empty.", "empty_image");

        var isVideo = contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(contentType)
            || (!isVideo && !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)))
            throw new BusinessRuleException("Only photos and videos can be uploaded here.", "not_media");

        if (isVideo)
        {
            if (poster is null || poster.Length == 0)
                throw new BusinessRuleException(
                    "That video arrived without a still to show for it.", "video_without_poster");
            if (content.Length > MaxVideoBytes)
                throw new BusinessRuleException("That video is too long to upload.", "video_too_large");
            if (!ImageSniffer.IsPhoto(poster, PosterType))
                throw new BusinessRuleException(ImageSniffer.Refusal, ImageSniffer.RefusalCode);
        }
        // Judged by the BYTES, on every door into a box — host, guest, QR contributor and bucket page
        // all arrive here. What is stored is served back on the app's own origin under the type it was
        // uploaded as, and an SVG there is a document that runs script, not a photograph. "image/*" was
        // the whole check before, and it said yes to exactly that.
        else if (!ImageSniffer.IsPhoto(content, contentType))
            throw new BusinessRuleException(ImageSniffer.Refusal, ImageSniffer.RefusalCode);

        // Whether it is the night at all. Before the quota, because "this closed a week ago" is the
        // more useful answer than "this is full" when both are true.
        //
        // THIS APPLIES TO THE HOST TOO, and that is deliberate rather than an oversight to tidy up.
        // It is a change from how the photo box behaved before buckets, where the host could add from
        // their dashboard at any time. A bucket is an occasion: it is open on its night and then it is
        // a record of one, and an owner who could keep adding to it indefinitely would make the date
        // a suggestion. If this ever looks like a bug, it was asked for — check before "fixing" it.
        await bucketService.EnsureOpenAsync(bucketId, ct);

        // Whether it will fit, checked BEFORE a single object is written: the alternative is
        // discovering a bucket is full after uploading 200 MB into it, which costs the storage anyway
        // and leaves a half-written row to unpick.
        //
        // The estimate is the raw upload — exact for a video, which is stored as it arrived, and
        // close for a photograph, whose three derivatives together land near the original. It only
        // has to be honest enough to refuse an upload that clearly does not fit; the real figure is
        // settled below from what was actually stored.
        //
        // RESERVED, not merely checked: the estimate is counted against the bucket in the same step
        // as the check, under a lock. Checking first and counting at the end let twenty parallel
        // uploads all pass the check on the same figure and all be stored.
        await bucketService.ReserveRoomAsync(bucketId, content.Length, ct);

        // NO SIZE CAP on a photograph, deliberately. Size limits belong on the images a TEMPLATE
        // renders, where a huge upload buys nothing a browser can show and costs every guest the
        // download. This is the opposite case: it is somebody's own photograph of the night, and
        // there is no version of "your memory of the party was too big" that is the right answer.
        // What that costs is storage, which is what R2 (§1) and the bucket's quota are for.
        //
        // Three objects, three jobs: the shot as taken, a screen-sized copy to open, and a grid tile.
        // All three drop EXIF — which matters more here than anywhere else in the product, because
        // these are photographs OF other people's guests and the GPS tag in a camera roll would
        // otherwise publish where someone's wedding was to anyone who saves a picture of it.
        EventPhoto photo;
        try
        {
            var ext = Path.GetExtension(fileName);
            if (string.IsNullOrWhiteSpace(ext)) ext = MediaFileTypes.ExtensionFor(contentType);

            var id = Guid.NewGuid();
            // Keyed by BUCKET, not by campaign: a standalone bucket has no campaign to key by, and the
            // bucket is what owns the bytes and is charged for them either way.
            var stem = $"buckets/{bucketId:N}/media/{id:N}";

            string originalUrl, url, thumbUrl;
            long sizeBytes;
            int width, height;

            // Every derivative is independent of every other one, so they are made and written TOGETHER
            // rather than one after another.
            //
            // The wall time here is almost entirely the round trip to object storage, not the picture
            // work: the same 12-megapixel photograph takes about 0.8s against storage on the same machine
            // and about 8s against R2, and doing three PUTs in a row is three of those round trips spent
            // for nothing. That whole time a contributor is standing at a party holding the phone it is
            // uploading from, which is the worst place in this product to be slow.
            async Task<Derivative> Write(Func<OptimizedImage> make, string key, string type)
            {
                // Off the request thread: decoding and re-encoding a 12MP JPEG is real CPU, and holding
                // it here would serialise the three anyway.
                var image = await Task.Run(make, ct);
                var stored = await storage.PutAsync(key, image.Content, type, ct);
                return new Derivative(stored, image.Content.Length, image.Width, image.Height);
            }

            if (isVideo)
            {
                // ONE object, pointed at twice. There is no smaller viewing copy to make without
                // transcoding, and storing the same file under two keys would double what a party's
                // videos cost for nothing. The tile is the only derived thing a video has.
                var clip = storage.PutAsync($"{stem}{ext}", content, contentType, ct);
                var tile = Write(() => imageOptimizer.Optimize(poster!, PosterType, ThumbEdge),
                    $"{stem}_t.jpg", PosterType);
                // Never uploaded — it is read only for the dimensions the clip itself was shot at.
                var still = Task.Run(() => imageOptimizer.Preserve(poster!, PosterType), ct);

                await Task.WhenAll(clip, tile, still);

                url = originalUrl = clip.Result;
                thumbUrl = tile.Result.Url;

                sizeBytes = content.Length + tile.Result.Bytes;
                // The frame's size IS the video's — it was drawn from it — so this stays the dimensions
                // of the thing itself rather than of the tile standing in for it.
                width = still.Result.Width;
                height = still.Result.Height;
            }
            else
            {
                var original = Write(() => imageOptimizer.Preserve(content, contentType),
                    $"{stem}_o{ext}", contentType);
                var view = Write(() => imageOptimizer.Optimize(content, contentType, ViewEdge),
                    $"{stem}{ext}", contentType);
                var thumb = Write(() => imageOptimizer.Optimize(content, contentType, ThumbEdge),
                    $"{stem}_t{ext}", contentType);

                await Task.WhenAll(original, view, thumb);

                originalUrl = original.Result.Url;
                url = view.Result.Url;
                thumbUrl = thumb.Result.Url;

                sizeBytes = original.Result.Bytes + view.Result.Bytes + thumb.Result.Bytes;
                width = original.Result.Width;
                height = original.Result.Height;
            }

            photo = new EventPhoto
            {
                Id = id,
                CampaignId = campaignId,
                BucketId = bucketId,
                GuestId = guestId,
                UploaderName = uploaderName,
                OriginalUrl = originalUrl,
                Url = url,
                ThumbUrl = thumbUrl,
                ContentType = contentType,
                SizeBytes = sizeBytes,
                // The shot's own dimensions, not the viewing copy's — this is what the photo IS.
                Width = width,
                Height = height,
                CreatedAt = DateTimeOffset.UtcNow
            };

            await photos.AddAsync(photo, ct);
            await uow.SaveChangesAsync(ct);
        }
        catch
        {
            // Nothing was recorded, so the space reserved for it is given back. Not the request's
            // token: a guest who gave up and closed the page must not keep the space they never used.
            await bucketService.CountUsageAsync(bucketId, -content.Length, CancellationToken.None);
            throw;
        }

        // Settled to what was actually written, not what was estimated above.
        await bucketService.CountUsageAsync(bucketId, photo.SizeBytes - content.Length, ct);

        return new EventPhotoDto(
            photo.Id, photo.Url, photo.ThumbUrl, photo.OriginalUrl, photo.ContentType,
            photo.Width, photo.Height, photo.UploaderName, true, photo.CreatedAt);
    }

    public async Task DeleteAsync(
        Guid campaignId, Guid photoId, Guid? actingGuestId, CancellationToken ct = default)
    {
        var photo = await photos.Query(tracking: true)
            .FirstOrDefaultAsync(p => p.Id == photoId && p.CampaignId == campaignId, ct)
            ?? throw new NotFoundException("That photo is no longer here.");

        if (photo.DeletedAt is not null) return;   // already gone; deleting twice is not an error

        var mine = actingGuestId is { } me && photo.GuestId == me;
        if (!mine && !await MayHostAsync(campaignId, ct))
            throw new ForbiddenException("Only the host, or whoever took it, can remove this photo.");

        photo.DeletedAt = DateTimeOffset.UtcNow;
        photos.Update(photo);
        await uow.SaveChangesAsync(ct);
    }

    public async Task WriteArchiveAsync(
        Guid campaignId, Guid? viewerGuestId, IReadOnlyCollection<Guid>? ids,
        Func<string, Stream> begin, CancellationToken ct = default)
    {
        var campaign = await campaigns.GetByIdAsync(campaignId, ct)
                       ?? throw new NotFoundException("That event no longer exists.");

        var hosts = await MayHostAsync(campaignId, ct);
        if (!hosts && await bucketService.PhaseForCampaignAsync(campaignId, ct) >= MediaPhase.OrganiserOnly)
            throw new ForbiddenException("The photos from this event are only available to its organiser now.");

        var view = await ViewForAsync(campaignId, hosts, viewerGuestId, ct);

        var wanted = ids is { Count: > 0 } ? ids.ToHashSet() : null;
        // Filtered by bucket BEFORE the emptiness check, so a guest shut out of every bucket is told
        // there is nothing to download rather than handed an empty zip — and so naming a hidden
        // photo's id in ?ids= gets nothing either.
        var chosen = (await photos.Query()
                .Where(p => p.CampaignId == campaignId && p.DeletedAt == null)
                .Where(p => wanted == null || wanted.Contains(p.Id))
                .OrderBy(p => p.CreatedAt)
                .ToListAsync(ct))
            .Where(p => view is null || view.MaySee(p.BucketId))
            .ToList();

        if (chosen.Count == 0)
            throw new BusinessRuleException("There are no photos to download.", "no_photos");

        // Everything that can refuse this request has now refused it, so the response may commit.
        var destination = begin(ArchiveName(campaign.Title));

        // ZipArchive never touches the response body directly. The body has buffering off, and Kestrel
        // refuses synchronous writes to it — and ZipArchive writes synchronously in places no async API
        // reaches: an entry's data descriptor on dispose (even through DisposeAsync, on an unseekable
        // stream) and the central directory at the end. Every download used to fail with
        // "Synchronous operations are disallowed" after the headers had gone, so it could only ever
        // arrive as a truncated file.
        //
        // So the archive writes into a buffer, and the buffer is emptied into the body asynchronously
        // after every entry. It still streams: what is held at once is one photograph, which the
        // storage read below is holding anyway — never the archive.
        //
        // leaveOpen: the buffer is ours to drain after the archive's final write.
        // NoCompression because every entry is already a JPEG — deflating it again spends CPU per
        // megabyte to save almost nothing, and this runs while somebody waits.
        var buffer = new DrainingStream(destination);
        using (var zip = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var index = 0;

            foreach (var photo in chosen)
            {
                index++;
                var key = StorageKeyOf(photo.OriginalUrl) ?? StorageKeyOf(photo.Url);
                if (key is null) continue;

                var bytes = await storage.GetAsync(key, ct);
                // One object that has gone from storage must not cost the guest the other ninety-nine.
                if (bytes is null || bytes.Length == 0) continue;

                var entry = zip.CreateEntry(EntryName(photo, index, key, used), CompressionLevel.NoCompression);
                entry.LastWriteTime = photo.CreatedAt;
                using (var into = entry.Open())
                    into.Write(bytes);   // into the buffer; the descriptor lands there on dispose

                await buffer.DrainAsync(ct);
            }
        }

        // The central directory, written when the archive closed.
        await buffer.DrainAsync(ct);
    }

    /// <summary>
    /// A write-only stream that holds what it is given until <see cref="DrainAsync"/> hands it on.
    ///
    /// <para>Unseekable on purpose, like the response body it stands in front of: ZipArchive then
    /// counts positions itself instead of seeking back to patch entry headers, which is what lets the
    /// buffer be emptied between entries without the archive noticing.</para>
    /// </summary>
    private sealed class DrainingStream(Stream destination) : Stream
    {
        private readonly MemoryStream _pending = new();

        public async Task DrainAsync(CancellationToken ct)
        {
            if (_pending.Length == 0) return;
            await destination.WriteAsync(_pending.GetBuffer().AsMemory(0, (int)_pending.Length), ct);
            await destination.FlushAsync(ct);
            _pending.SetLength(0);
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override void Write(byte[] buffer, int offset, int count) => _pending.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => _pending.Write(buffer);
        public override void WriteByte(byte value) => _pending.WriteByte(value);
        // Nothing to push synchronously: the bytes go on at the next drain.
        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    /// <summary>
    /// The door to the box, and what the caller may see once through it: <c>null</c> for everything,
    /// otherwise the buckets this guest is allowed into.
    ///
    /// <para>Moderators and the people the event is for see every photograph. A guest sees only the
    /// buckets they may view — a bucket the organiser restricted, with this guest switched off, stays
    /// shut here too. It used to be checked only at the bucket's own door, which left this campaign
    /// door handing the same photographs to the same guest.</para>
    /// </summary>
    private async Task<GuestBucketView?> ViewForAsync(
        Guid campaignId, bool moderates, Guid? viewerGuestId, CancellationToken ct)
    {
        // The people the event is for look at it too, without moderating.
        if (moderates || await ownership.AccessAsync(campaignId, ct) >= CampaignAccess.Celebrant)
            return null;

        if (!await IsGuestOfAsync(campaignId, viewerGuestId, ct))
            throw new ForbiddenException("This photo box belongs to an event you're not on.");

        return await bucketService.GuestViewAsync(campaignId, viewerGuestId!.Value, ct);
    }

    /// <summary>
    /// The stored object's key, recovered from its public URL. Keys always begin at
    /// <c>campaigns/</c>, which holds whether the URL is a production-relative path or the absolute
    /// one the asset domain serves — so this survives the storage backend changing underneath it.
    /// </summary>
    private static string? StorageKeyOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        // Two roots, not one. Photos from before buckets live under campaigns/; everything uploaded
        // since lives under buckets/{id}/media/. Looking only for campaigns/ skipped every photo
        // uploaded into a bucket, so a download came back as a valid but EMPTY zip.
        var starts = new[] { "buckets/", "campaigns/" }
            .Select(root => url.IndexOf(root, StringComparison.Ordinal))
            .Where(i => i >= 0)
            .ToList();
        return starts.Count == 0 ? null : url[starts.Min()..].Split('?')[0];
    }

    /// <summary>
    /// What each photo is called inside the zip. Numbered so the order they were taken in survives
    /// unzipping, and credited where we know who took it.
    /// </summary>
    private static string EntryName(EventPhoto photo, int index, string key, HashSet<string> used)
    {
        var ext = Path.GetExtension(key);
        if (string.IsNullOrWhiteSpace(ext)) ext = ".jpg";

        var who = Sanitize(photo.UploaderName);
        var name = string.IsNullOrEmpty(who) ? $"{index:D3}{ext}" : $"{index:D3}-{who}{ext}";

        // A zip with two identical names is a zip that unpacks to one file in some readers.
        var candidate = name;
        var attempt = 1;
        while (!used.Add(candidate))
            candidate = $"{Path.GetFileNameWithoutExtension(name)}-{++attempt}{ext}";
        return candidate;
    }

    private static string ArchiveName(string? title)
    {
        var stem = Sanitize(title);
        return string.IsNullOrEmpty(stem) ? "event-photos.zip" : $"{stem}-photos.zip";
    }

    /// <summary>
    /// Reduces a title or a person's name to something every filesystem accepts. Deliberately strict
    /// — a guest called their photo whatever their phone called it, and a zip entry name is a path.
    /// </summary>
    private static string Sanitize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var cleaned = new string(value.Trim()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-')
            .ToArray());
        while (cleaned.Contains("--", StringComparison.Ordinal))
            cleaned = cleaned.Replace("--", "-", StringComparison.Ordinal);
        return cleaned.Trim('-')[..Math.Min(cleaned.Trim('-').Length, 40)];
    }

    /// <summary>
    /// The host's door: holds the moderation right AND owns this campaign. Both halves matter — the
    /// permission says "may moderate a photo box", never "may moderate THIS one", and every customer
    /// account holds it.
    /// </summary>
    private async Task<bool> MayHostAsync(Guid campaignId, CancellationToken ct) =>
        currentUser.HasPermission(Permissions.Photos.Moderate) && await ownership.OwnsAsync(campaignId, ct);

    /// <summary>
    /// The guest's door. The id arrives from a caller that already authenticated it — the render
    /// cookie, or the OTP identity — but it is still checked against this campaign here, so a guest
    /// authenticated for one event cannot read another event's box by passing its id.
    /// </summary>
    private async Task<bool> IsGuestOfAsync(Guid campaignId, Guid? guestId, CancellationToken ct)
    {
        if (guestId is not { } id) return false;
        var guest = await guestRepository.GetByIdAsync(id, ct);
        return guest is not null && guest.CampaignId == campaignId;
    }

    /// <summary>
    /// The credit, read once and stored with the photo. Looked up through the repository rather than
    /// taken from the caller, so a guest cannot post under someone else's name.
    /// </summary>
    private async Task<string?> GuestNameAsync(Guid guestId, CancellationToken ct)
    {
        var guest = await guestRepository.GetByIdAsync(guestId, ct);
        return string.IsNullOrWhiteSpace(guest?.Name) ? null : guest.Name;
    }

    /// <summary>What a poster frame is, whatever the video around it turned out to be.</summary>
    private const string PosterType = "image/jpeg";
}
