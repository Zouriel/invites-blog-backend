using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Photos;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Services.Faces;

/// <summary>
/// Face grouping in albums: switching it on and off, queueing photos, what a box shows of it, and naming and
/// opting out of groups. Finding the faces is <c>FaceIndexService</c> (in the API host, one photo at a time).
///
/// <para>Face data is biometric data, so every rule here is about not keeping it: only albums whose host
/// switched it on, only inside that album, deleted when the photo is, when the album is, when it's switched
/// off, and when someone opts out (then only the group's average stays, so new photos of them aren't grouped;
/// it goes with the album too). See docs/FUTURE-PLANS.md §9.</para>
/// </summary>
public interface IFaceGroupingService
{
    bool Available { get; }

    /// <summary>Switches it on (queueing every photo already there) or off (deleting every face, group and job). The caller has checked the host owns the album.</summary>
    Task SetAsync(MediaBucket bucket, bool on, bool guestsTold, Guid? by, CancellationToken ct, bool outsideRestrictedRegions = true);

    /// <summary>Queues a new photo when its album has grouping on. Added to the caller's unit of work; the caller saves.</summary>
    Task QueueAsync(EventPhoto photo, CancellationToken ct);

    /// <summary>Removes a photo's faces and job (the photo was removed). Saves.</summary>
    Task ForgetPhotosAsync(IReadOnlyCollection<Guid> photoIds, CancellationToken ct);

    /// <summary>What a box shows: the groups and, per photo, who is in it. Null when no album among the photos has it on.</summary>
    Task<(FaceGroupingDto? Dto, IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> PeopleByPhoto)> ForBoxAsync(
        IReadOnlyCollection<Guid> bucketIds, IReadOnlyList<EventPhoto> photos, bool canName, CancellationToken ct);

    /// <summary>Names a group (blank clears it).</summary>
    Task<FacePersonDto> NameAsync(Guid personId, string? name, IReadOnlyList<EventPhoto> photos, CancellationToken ct);

    /// <summary>"Ignore my face": deletes the group's faces and keeps it out of grouping.</summary>
    Task IgnoreAsync(Guid personId, CancellationToken ct, string? actor = null);

    /// <summary>
    /// Switches grouping off (deleting all face data) in albums with no new photos or changes for
    /// <paramref name="idle"/>: the written retention policy's inactivity limit. Returns how many.
    /// </summary>
    Task<int> ExpireIdleAsync(TimeSpan idle, CancellationToken ct);

    /// <summary>
    /// Switches grouping off (deleting all face data) in albums whose event no longer has a plan that includes it
    /// (Free, or a pass that ended). Returns how many.
    /// </summary>
    Task<int> ExpireUnpaidAsync(CancellationToken ct);

    /// <summary>"Same person as…": moves one group's faces into another of the same album; the first goes.</summary>
    Task<FacePersonDto> MergeAsync(Guid personId, Guid intoId, IReadOnlyList<EventPhoto> photos, CancellationToken ct);

    /// <summary>Whether this album groups faces: what the camera's notice asks.</summary>
    Task<bool> OnForBucketAsync(Guid bucketId, CancellationToken ct);

    /// <summary>Whether any album of this event groups faces.</summary>
    Task<bool> OnForCampaignAsync(Guid campaignId, CancellationToken ct);
}

public sealed class FaceGroupingService(
    IRepository<MediaBucket> buckets,
    IRepository<PhotoFace> faces,
    IRepository<FacePerson> people,
    IRepository<FaceJob> jobs,
    IRepository<EventPhoto> photos,
    IUnitOfWork uow,
    IFaceAnalyzer? analyzer = null,
    IRepository<AuditLog>? audit = null,
    Plans.IPlanService? plans = null,
    Push.IPushQueue? push = null) : IFaceGroupingService
{

    public bool Available => analyzer?.Available ?? false;

    public static bool IsPicture(EventPhoto p) => p.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A record of every switch and opt-out (who, when, which album, which notice), never any face data: what we
    /// show if anyone asks how consent and opt-outs were handled.
    /// </summary>
    private async Task AuditAsync(string action, MediaBucket? bucket, Guid bucketId, string? actor, object data, CancellationToken ct)
    {
        if (audit is null) return;
        await audit.AddAsync(new AuditLog
        {
            Id = Guid.NewGuid(),
            Action = action,
            Actor = actor,
            CampaignId = bucket?.CampaignId,
            DataJson = System.Text.Json.JsonSerializer.Serialize(new { bucketId, notice = NoticeVersion, details = data }),
            CreatedAt = DateTimeOffset.UtcNow,
        }, ct);
    }

    /// <summary>Which wording of the notices and confirmations was in force (bump when they change).</summary>
    public const string NoticeVersion = "2026-11-11";

    public async Task SetAsync(MediaBucket bucket, bool on, bool guestsTold, Guid? by, CancellationToken ct, bool outsideRestrictedRegions = true)
    {
        var now = DateTimeOffset.UtcNow;
        if (on)
        {
            if (!Available) throw new InvalidStateException("Face grouping isn't available right now.", "faces_unavailable");
            if (!guestsTold)
                throw new BusinessRuleException("Tick the box to confirm your guests are told photos are grouped by face.", "faces_guests_not_told");
            if (plans is not null && !Plans.PlanRules.IncludesFaceGrouping(await plans.ForCampaignAsync(bucket.CampaignId, ct)))
                throw new BusinessRuleException("Grouping photos by face comes with any pass or Premium.", "faces_needs_plan");
            if (!outsideRestrictedRegions)
                throw new BusinessRuleException(
                    "Face grouping isn't available for events in Illinois, Texas or Washington (USA). Tick the box if this event isn't there.",
                    "faces_restricted_region");
            if (bucket.FaceGroupingOn) return;
            // Start clean: no face or group from an earlier time it was on (or a sweep that hadn't finished)
            // carries over. Jobs still queued are kept and the missing ones added below.
            await ClearAsync(bucket.Id, ct, keepJobs: true);
            bucket.FaceGroupingOn = true;
            bucket.FaceGroupingConfirmedAt = now;
            bucket.FaceGroupingConfirmedBy = by;
            // The host hears once when this switch-on's first pass is done (FaceNoticeService), and gets fresh
            // warnings before it ends.
            bucket.FacesReadyPushedAt = null;
            bucket.FacesEndNoticeFor = null;
            bucket.FacesEndNoticeStage = 0;
            bucket.UpdatedAt = now;
            buckets.Update(bucket);

            var queued = await jobs.Query().Where(j => j.BucketId == bucket.Id).Select(j => j.Id).ToListAsync(ct);
            var pictures = await photos.Query()
                .Where(p => p.BucketId == bucket.Id && p.DeletedAt == null && p.ContentType.StartsWith("image/"))
                .OrderBy(p => p.CreatedAt)
                .Select(p => p.Id).ToListAsync(ct);
            foreach (var id in pictures.Except(queued))
                await jobs.AddAsync(NewJob(id, bucket.Id, now), ct);
            await AuditAsync("faces.on", bucket, bucket.Id, by?.ToString(), new { guestsTold, outsideRestrictedRegions }, ct);
            await uow.SaveChangesAsync(ct);
            return;
        }

        var wasOn = bucket.FaceGroupingOn;
        bucket.FaceGroupingOn = false;
        bucket.UpdatedAt = now;
        buckets.Update(bucket);
        await ClearAsync(bucket.Id, ct);
        if (wasOn) await AuditAsync("faces.off", bucket, bucket.Id, by?.ToString(), new { }, ct);
        await uow.SaveChangesAsync(ct);
    }

    /// <summary>Everything face-related in an album, gone (added to the unit of work).</summary>
    private async Task ClearAsync(Guid bucketId, CancellationToken ct, bool keepJobs = false)
    {
        faces.RemoveRange(await faces.Query(tracking: true).Where(f => f.BucketId == bucketId).ToListAsync(ct));
        people.RemoveRange(await people.Query(tracking: true).Where(p => p.BucketId == bucketId).ToListAsync(ct));
        if (!keepJobs) jobs.RemoveRange(await jobs.Query(tracking: true).Where(j => j.BucketId == bucketId).ToListAsync(ct));
    }

    private static FaceJob NewJob(Guid photoId, Guid bucketId, DateTimeOffset now) => new()
    {
        Id = photoId,
        BucketId = bucketId,
        Status = FaceJobStatus.Waiting,
        NextAttemptAt = now,
        CreatedAt = now,
        UpdatedAt = now,
    };

    public async Task QueueAsync(EventPhoto photo, CancellationToken ct)
    {
        if (photo.BucketId is not { } bucketId || !IsPicture(photo)) return;
        var on = await buckets.Query().AnyAsync(b => b.Id == bucketId && b.FaceGroupingOn, ct);
        if (!on) return;
        await jobs.AddAsync(NewJob(photo.Id, bucketId, DateTimeOffset.UtcNow), ct);
    }

    public async Task ForgetPhotosAsync(IReadOnlyCollection<Guid> photoIds, CancellationToken ct)
    {
        if (photoIds.Count == 0) return;
        var ids = photoIds.ToList();
        var gone = await faces.Query(tracking: true).Where(f => ids.Contains(f.PhotoId)).ToListAsync(ct);
        var waiting = await jobs.Query(tracking: true).Where(j => ids.Contains(j.Id)).ToListAsync(ct);
        if (gone.Count == 0 && waiting.Count == 0) return;
        faces.RemoveRange(gone);
        jobs.RemoveRange(waiting);
        await uow.SaveChangesAsync(ct);
    }

    public async Task<(FaceGroupingDto? Dto, IReadOnlyDictionary<Guid, IReadOnlyList<Guid>> PeopleByPhoto)> ForBoxAsync(
        IReadOnlyCollection<Guid> bucketIds, IReadOnlyList<EventPhoto> shown, bool canName, CancellationToken ct)
    {
        var empty = (IReadOnlyDictionary<Guid, IReadOnlyList<Guid>>)new Dictionary<Guid, IReadOnlyList<Guid>>();
        var ids = bucketIds.Distinct().ToList();
        var onIds = await buckets.Query().Where(b => ids.Contains(b.Id) && b.FaceGroupingOn).Select(b => b.Id).ToListAsync(ct);
        if (onIds.Count == 0) return (null, empty);

        var waiting = await jobs.Query().CountAsync(j => onIds.Contains(j.BucketId) && j.Status != FaceJobStatus.Failed, ct);
        var groups = await people.Query().Where(p => onIds.Contains(p.BucketId) && !p.Ignored)
            .Select(p => new { p.Id, p.Name }).ToListAsync(ct);
        var rows = await faces.Query().Where(f => onIds.Contains(f.BucketId))
            .Select(f => new { f.PhotoId, f.PersonId, f.X, f.Y, f.W, f.H, f.Score }).ToListAsync(ct);

        var byId = shown.ToDictionary(p => p.Id);
        var live = groups.Select(g => g.Id).ToHashSet();
        rows = rows.Where(r => byId.ContainsKey(r.PhotoId) && live.Contains(r.PersonId)).ToList();
        var peopleByPhoto = rows.GroupBy(r => r.PhotoId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<Guid>)g.Select(r => r.PersonId).Distinct().ToList());

        var list = new List<FacePersonDto>();
        foreach (var g in groups)
        {
            var mine = rows.Where(r => r.PersonId == g.Id).ToList();
            var count = mine.Select(r => r.PhotoId).Distinct().Count();
            // Every group, even one seen once: the People tab is where the host names them. Clients offer only
            // named people as filters.
            if (count == 0) continue;
            // The clearest look at them: the biggest face the detector was sure of.
            var cover = mine.OrderByDescending(r => r.W * r.H * r.Score).First();
            list.Add(new FacePersonDto(g.Id, g.Name, count, byId[cover.PhotoId].ThumbUrl, cover.X, cover.Y, cover.W, cover.H));
        }
        list = list.OrderByDescending(p => p.Name is not null).ThenByDescending(p => p.PhotoCount).ToList();
        return (new FaceGroupingDto(true, canName, waiting, list), peopleByPhoto);
    }

    public async Task<FacePersonDto> NameAsync(Guid personId, string? name, IReadOnlyList<EventPhoto> shown, CancellationToken ct)
    {
        var person = await people.Query(tracking: true).FirstOrDefaultAsync(p => p.Id == personId && !p.Ignored, ct)
                     ?? throw new NotFoundException("That group is no longer there.");
        var clean = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        if (clean is { Length: > FacePerson.MaxNameLength }) clean = clean[..FacePerson.MaxNameLength].TrimEnd();
        // A name the album already has is the same person: one group, not two with the same name.
        if (clean is not null)
        {
            var same = await people.Query().Where(p => p.BucketId == person.BucketId && p.Id != personId && !p.Ignored && p.Name != null)
                .Select(p => new { p.Id, p.Name }).ToListAsync(ct);
            var match = same.FirstOrDefault(p => string.Equals(p.Name, clean, StringComparison.OrdinalIgnoreCase));
            if (match is not null) return await MergeAsync(personId, match.Id, shown, ct);
        }
        person.Name = clean;
        person.UpdatedAt = DateTimeOffset.UtcNow;
        people.Update(person);
        await uow.SaveChangesAsync(ct);
        var (dto, _) = await ForBoxAsync([person.BucketId], shown, true, ct);
        return dto?.People.FirstOrDefault(p => p.Id == personId)
               ?? throw new NotFoundException("That group is no longer there.");
    }

    public async Task<FacePersonDto> MergeAsync(Guid personId, Guid intoId, IReadOnlyList<EventPhoto> shown, CancellationToken ct)
    {
        if (personId == intoId) throw new BusinessRuleException("That's already the same person.", "faces_same_person");
        var from = await people.Query(tracking: true).FirstOrDefaultAsync(p => p.Id == personId && !p.Ignored, ct)
                   ?? throw new NotFoundException("That group is no longer there.");
        var into = await people.Query(tracking: true).FirstOrDefaultAsync(p => p.Id == intoId && !p.Ignored, ct)
                   ?? throw new NotFoundException("That person is no longer there.");
        if (from.BucketId != into.BucketId) throw new NotFoundException("That person is no longer there.");

        foreach (var face in await faces.Query(tracking: true).Where(f => f.PersonId == personId).ToListAsync(ct))
        {
            face.PersonId = intoId;
            faces.Update(face);
        }
        // The average of all their faces: the two averages, weighted by how many faces made each.
        var total = Math.Max(1, into.FaceCount + from.FaceCount);
        var c = new float[Math.Max(into.Centroid.Length, from.Centroid.Length)];
        for (var i = 0; i < c.Length; i++)
            c[i] = ((i < into.Centroid.Length ? into.Centroid[i] * into.FaceCount : 0)
                    + (i < from.Centroid.Length ? from.Centroid[i] * from.FaceCount : 0)) / total;
        into.Centroid = c;
        into.FaceCount += from.FaceCount;
        into.Name ??= from.Name;
        into.UpdatedAt = DateTimeOffset.UtcNow;
        people.Update(into);
        await uow.SaveChangesAsync(ct);
        people.Remove(from);
        await uow.SaveChangesAsync(ct);

        var (dto, _) = await ForBoxAsync([into.BucketId], shown, true, ct);
        return dto?.People.FirstOrDefault(p => p.Id == intoId)
               ?? throw new NotFoundException("That person is no longer there.");
    }

    public Task<bool> OnForBucketAsync(Guid bucketId, CancellationToken ct) =>
        buckets.Query().AnyAsync(b => b.Id == bucketId && b.FaceGroupingOn, ct);

    public Task<bool> OnForCampaignAsync(Guid campaignId, CancellationToken ct) =>
        buckets.Query().AnyAsync(b => b.CampaignId == campaignId && b.FaceGroupingOn, ct);

    public async Task<int> ExpireIdleAsync(TimeSpan idle, CancellationToken ct)
    {
        var cutoff = DateTimeOffset.UtcNow - idle;
        var on = await buckets.Query(tracking: true).Where(b => b.FaceGroupingOn && b.UpdatedAt < cutoff).ToListAsync(ct);
        var expired = 0;
        foreach (var bucket in on)
        {
            var recent = await photos.Query().AnyAsync(p => p.BucketId == bucket.Id && p.CreatedAt >= cutoff, ct);
            if (recent) continue;
            bucket.FaceGroupingOn = false;
            buckets.Update(bucket);
            await ClearAsync(bucket.Id, ct);
            await AuditAsync("faces.expired", bucket, bucket.Id, "retention", new { idleDays = (int)idle.TotalDays }, ct);
            await uow.SaveChangesAsync(ct);
            push?.Enqueue(new Push.FacesSwitchedOff(bucket.Id, Push.FacesOffReason.Idle));
            expired++;
        }
        return expired;
    }

    public async Task<int> ExpireUnpaidAsync(CancellationToken ct)
    {
        if (plans is null) return 0;
        var on = await buckets.Query(tracking: true).Where(b => b.FaceGroupingOn).ToListAsync(ct);
        var expired = 0;
        foreach (var bucket in on)
        {
            var plan = await plans.ForCampaignAsync(bucket.CampaignId, ct);
            if (Plans.PlanRules.IncludesFaceGrouping(plan)) continue;
            bucket.FaceGroupingOn = false;
            bucket.UpdatedAt = DateTimeOffset.UtcNow;
            buckets.Update(bucket);
            await ClearAsync(bucket.Id, ct);
            await AuditAsync("faces.plan_ended", bucket, bucket.Id, "plan", new { plan = plan.Kind.ToString() }, ct);
            await uow.SaveChangesAsync(ct);
            push?.Enqueue(new Push.FacesSwitchedOff(bucket.Id, Push.FacesOffReason.PlanEnded));
            expired++;
        }
        return expired;
    }

    public async Task IgnoreAsync(Guid personId, CancellationToken ct, string? actor = null)
    {
        var person = await people.Query(tracking: true).FirstOrDefaultAsync(p => p.Id == personId, ct)
                     ?? throw new NotFoundException("That group is no longer there.");
        if (person.Ignored) return;
        // Only the average stays, to keep new photos of them out of grouping; no name, no faces, no boxes.
        person.Ignored = true;
        person.Name = null;
        person.UpdatedAt = DateTimeOffset.UtcNow;
        person.IgnoredAt = person.UpdatedAt;
        person.IgnoredByUserId = Guid.TryParse(actor, out var by) ? by : null;
        people.Update(person);
        faces.RemoveRange(await faces.Query(tracking: true).Where(f => f.PersonId == personId).ToListAsync(ct));
        await AuditAsync("faces.opt_out", null, person.BucketId, actor, new { }, ct);
        await uow.SaveChangesAsync(ct);
    }
}

/// <summary>
/// Putting a new face in a group: the closest group's average, if close enough, else a new group. Pure, so the
/// rule can be tested with numbers. Fingerprints are unit length, so closeness is a dot product (cosine).
/// </summary>
public static class FaceClustering
{
    /// <summary>
    /// How close to a group's average a face must be to join it. SFace scores the same person ~0.75-0.9 and
    /// relatives up to ~0.4 on the reference photos (docs: OpenCV's own same-person line is 0.363).
    /// </summary>
    public const float DefaultThreshold = 0.45f;

    /// <summary>Dot product: the cosine for two fingerprints, which are unit length.</summary>
    public static float Similarity(float[] a, float[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        var s = 0f;
        for (var i = 0; i < n; i++) s += a[i] * b[i];
        return s;
    }

    /// <summary>Cosine between a fingerprint and a group's average (which is shorter than unit length).</summary>
    public static float Cosine(float[] face, float[] average)
    {
        var norm = MathF.Sqrt(average.Sum(x => x * x));
        return norm > 0 ? Similarity(face, average) / norm : 0;
    }

    /// <summary>The closest group at or above the threshold, ignored groups included (the caller drops those faces).</summary>
    public static FacePerson? Match(float[] embedding, IEnumerable<FacePerson> groups, float threshold = DefaultThreshold)
    {
        FacePerson? best = null;
        var bestScore = threshold;
        foreach (var g in groups)
        {
            var s = Cosine(embedding, g.Centroid);
            if (s >= bestScore)
            {
                best = g;
                bestScore = s;
            }
        }
        return best;
    }

    /// <summary>
    /// Adds a face to a group's average: the true mean of its fingerprints (not re-normalised, so merging two
    /// groups can weight them exactly; <see cref="Cosine"/> compares against it).
    /// </summary>
    public static void Add(FacePerson group, float[] embedding)
    {
        var n = group.FaceCount;
        var c = group.Centroid.Length == embedding.Length ? group.Centroid.ToArray() : new float[embedding.Length];
        for (var i = 0; i < c.Length; i++) c[i] = (c[i] * n + embedding[i]) / (n + 1);
        group.Centroid = c;
        group.FaceCount = n + 1;
    }
}
