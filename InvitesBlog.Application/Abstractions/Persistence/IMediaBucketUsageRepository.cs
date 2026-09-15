namespace InvitesBlog.Application.Abstractions.Persistence;

/// <summary>
/// How a bucket's <c>UsedBytes</c> changes. Nowhere else may write it.
///
/// <para><b>Why it is not an ordinary property write.</b> Uploads arrive in parallel — a phone's
/// picker sends twenty at once. Reading the figure, adding to it and saving it back meant concurrent
/// uploads overwrote each other's increments: twenty uploads measured as a fraction of what they
/// stored, and a room check that trusted the understated figure let an account keep uploading past
/// the space its plan pays for.</para>
/// </summary>
public interface IMediaBucketUsageRepository
{
    /// <summary>
    /// Runs <paramref name="ensureRoom"/> and then adds <paramref name="bytes"/> to the bucket, while
    /// holding a lock every other reservation against the same <paramref name="quotaKey"/> waits for.
    /// Check and increment are one step as far as any other upload can tell, so two uploads can never
    /// both be admitted into the last of the space.
    /// </summary>
    /// <param name="quotaKey">
    /// What the space belongs to: the subscriber's account when a plan spans their events, otherwise
    /// the event. Every bucket whose usage the check sums must reserve under the same key.
    /// </param>
    /// <param name="ensureRoom">The room check. It throws to refuse, and nothing is added.</param>
    Task ReserveAsync(
        Guid quotaKey, Guid bucketId, long bytes, Func<CancellationToken, Task> ensureRoom,
        CancellationToken ct = default);

    /// <summary>
    /// Adds <paramref name="bytes"/> in a single UPDATE (negative to give some back), never taking the
    /// figure below zero.
    /// </summary>
    Task AddAsync(Guid bucketId, long bytes, CancellationToken ct = default);
}
