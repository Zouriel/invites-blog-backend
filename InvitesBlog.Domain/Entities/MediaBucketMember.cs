namespace InvitesBlog.Domain.Entities;

/// <summary>
/// Which of an event's guests may look into one of its buckets.
///
/// <para><b>A pivot, not a list.</b> It holds a <see cref="GuestId"/> and nothing else about the
/// person — no name, no email, no phone. Those live on the <see cref="Guest"/> row, which is the
/// whole point: a bucket's audience is drawn FROM the guest list rather than copied beside it. An
/// earlier version of this table keyed on the contact itself and was deleted for exactly that
/// reason — two lists answering one question drift the moment somebody edits one of them. Removing
/// a guest from the event now removes their access with them, by cascade, because there is only one
/// row that says who they are.</para>
///
/// <para><b>Empty means everyone.</b> A bucket with no rows here is open to the whole guest list;
/// what closes it is <see cref="MediaBucket.IsRestricted"/>, not the absence of rows. Absence has to
/// mean "unrestricted" rather than "nobody" because every bucket that existed before this table did
/// has none — the alternative is a migration that makes every photograph on the platform invisible
/// at once. See the remarks on that flag for how the two work together.</para>
///
/// <para><b><see cref="CampaignId"/> is here to be constrained, not to be read.</b> It is redundant
/// with the bucket's and the guest's, and it is stored so both foreign keys can be composite:
/// <c>(campaign_id, bucket_id)</c> and <c>(campaign_id, guest_id)</c>. That makes it structurally
/// impossible to admit a guest of one event to another event's bucket — an invariant that would
/// otherwise be an application check, and application checks are missed on the third call site.</para>
/// </summary>
public sealed class MediaBucketMember
{
    public Guid BucketId { get; set; }

    /// <summary>The guest row admitted. Cascades: off the list, out of the bucket.</summary>
    public Guid GuestId { get; set; }

    /// <summary>The event both of the above belong to. See the class remarks — this is a constraint.</summary>
    public Guid CampaignId { get; set; }

    public DateTimeOffset AddedAt { get; set; }
}
