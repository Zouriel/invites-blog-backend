using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Infrastructure.Repositories;

/// <inheritdoc cref="IMediaBucketUsageRepository"/>
public sealed class MediaBucketUsageRepository(AppDbContext db) : IMediaBucketUsageRepository
{
    public async Task ReserveAsync(
        Guid quotaKey, Guid bucketId, long bytes, Func<CancellationToken, Task> ensureRoom,
        CancellationToken ct = default)
    {
        // A transaction-scoped ADVISORY lock rather than row locks. The check sums more than one row —
        // every bucket on the event, or every bucket on the account — and some of those rows are not
        // the one being written, so locking "the bucket" would not stop two uploads to two buckets
        // from both taking the last of an event's space. The lock is on what the space belongs to, and
        // it is released by the commit or rollback, so a failed check can never leave it held.
        //
        // The check runs on this same connection AFTER the lock is granted. Under READ COMMITTED each
        // statement sees everything committed before it started, so an upload that waited here reads
        // the figure the one ahead of it just committed.
        var joined = db.Database.CurrentTransaction is not null;
        var tx = joined ? null : await db.Database.BeginTransactionAsync(ct);
        try
        {
            await db.Database.ExecuteSqlAsync($"SELECT pg_advisory_xact_lock({LockId(quotaKey)})", ct);
            await ensureRoom(ct);
            await AddAsync(bucketId, bytes, ct);
            if (tx is not null) await tx.CommitAsync(ct);
        }
        finally
        {
            if (tx is not null) await tx.DisposeAsync();
        }
    }

    public Task AddAsync(Guid bucketId, long bytes, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        // "used_bytes = used_bytes + n" in the database, never a value computed here and written back.
        return db.MediaBuckets
            .Where(b => b.Id == bucketId)
            .ExecuteUpdateAsync(s => s
                .SetProperty(b => b.UsedBytes, b => b.UsedBytes + bytes < 0 ? 0 : b.UsedBytes + bytes)
                .SetProperty(b => b.UpdatedAt, now), ct);
    }

    /// <summary>Postgres advisory locks take a bigint; the key's first eight bytes are plenty to tell quotas apart.</summary>
    private static long LockId(Guid key) => BitConverter.ToInt64(key.ToByteArray(), 0);
}
