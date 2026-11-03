namespace InvitesBlog.Application.Plans;

/// <summary>
/// What stays when an event goes back to Free after its pass (or its organiser's Premium) ends.
///
/// <para>Owner, 2026-11-03: "passes if not paid will be reduced to unsubed size and everything above
/// it gets removed". The event keeps up to Free's space, for good; only what doesn't fit is removed.
/// The invitation, its design and the guest replies are never touched: only album photos and videos
/// count. What is kept, in this order, until the space is full: the event's cover photos, the ones
/// the organiser picked to keep, then the earliest uploads. Something that doesn't fit is skipped and
/// the next one is tried, so a large video doesn't push out many small photos after it.</para>
///
/// <para>Albums beyond Free's one album are not merged or removed: they keep whatever of their
/// photos made the cut, and stay read-only like everything else on an event whose upload window has
/// passed. Moving photos between albums would change who may see them.</para>
/// </summary>
public static class MediaTrim
{
    /// <summary>One live photo or video, as the planner needs it.</summary>
    public sealed record Item(Guid Id, long SizeBytes, DateTimeOffset CreatedAt, bool PickedToKeep);

    /// <summary>The cut: what goes, and how many bytes that frees.</summary>
    public sealed record Plan(IReadOnlyList<Guid> Remove, long RemoveBytes, long KeepBytes);

    public static Plan For(IEnumerable<Item> items, IReadOnlyCollection<Guid> coverIds, long budget)
    {
        var ordered = items
            .OrderBy(i => coverIds.Contains(i.Id) ? 0 : i.PickedToKeep ? 1 : 2)
            .ThenBy(i => i.CreatedAt)
            .ThenBy(i => i.Id)
            .ToList();

        var kept = 0L;
        var remove = new List<Guid>();
        var removeBytes = 0L;
        foreach (var item in ordered)
        {
            var size = Math.Max(0, item.SizeBytes);
            if (kept + size <= budget)
            {
                kept += size;
                continue;
            }
            remove.Add(item.Id);
            removeBytes += size;
        }
        return new Plan(remove, removeBytes, kept);
    }
}
