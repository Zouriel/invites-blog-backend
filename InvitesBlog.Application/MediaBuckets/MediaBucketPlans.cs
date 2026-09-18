namespace InvitesBlog.Application.MediaBuckets;

/// <summary>
/// The unit a media bucket's size is measured in. What a bucket holds and costs is the event's plan
/// (<see cref="Plans.PlanCatalog"/>); the per-bucket price tiers that used to live here were never
/// sold and are gone.
/// </summary>
public static class MediaBucketPlans
{
    /// <summary>
    /// Gigabytes, in bytes. 1024-based rather than 1000-based because the number this is compared
    /// against is a file size measured the same way — a "10 GB" bucket that filled up at 9.31 GB by
    /// the only measure the customer can see would read as us shortchanging them.
    /// </summary>
    public const long BytesPerGb = 1024L * 1024 * 1024;
}
