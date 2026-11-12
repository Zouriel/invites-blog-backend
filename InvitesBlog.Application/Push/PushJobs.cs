using InvitesBlog.Domain.Enums;

namespace InvitesBlog.Application.Push;

/// <summary>
/// Something that happened which an account holder would want on their phone. Only ids travel: the
/// worker reads the rest from the database after the action that raised it has finished, so a job
/// never holds a request's data and never slows it. <see cref="ActorUserId"/> is whoever caused it,
/// who is never notified about their own action.
/// </summary>
public abstract record PushJob(Guid? ActorUserId);

/// <summary>The host sent the event's invitations: these guests now have it in their inbox.</summary>
public sealed record InvitationsSent(Guid CampaignId, IReadOnlyList<Guid> GuestIds, Guid? ActorUserId) : PushJob(ActorUserId);

/// <summary>A guest answered an invitation.</summary>
public sealed record GuestReplied(Guid InviteId, RsvpStatus Status, Guid? ActorUserId) : PushJob(ActorUserId);

/// <summary>
/// Someone commented on an event's post, or replied to a comment there. <see cref="RepliedToCommentId"/> is the
/// comment they pressed reply on, which may itself be a reply: replies are kept one level deep, so the new
/// comment's parent is the thread's first comment, and the reply's own author would otherwise never hear.
/// </summary>
public sealed record CommentAdded(Guid CommentId, Guid? ActorUserId, Guid? RepliedToCommentId = null) : PushJob(ActorUserId);

/// <summary>Someone was added to an event as one of the people it is for.</summary>
public sealed record CelebrantAdded(Guid CelebrantId, Guid? ActorUserId) : PushJob(ActorUserId);

/// <summary>Why face grouping was switched off without the host doing it.</summary>
public enum FacesOffReason
{
    /// <summary>No new photos or changes for the retention limit (Faces:IdleAlbumDays).</summary>
    Idle,
    /// <summary>The event's pass or Premium ended, so its plan no longer includes face grouping.</summary>
    PlanEnded,
}

/// <summary>Face grouping in an album was switched off automatically and its face data deleted: the host hears why.</summary>
public sealed record FacesSwitchedOff(Guid BucketId, FacesOffReason Reason) : PushJob((Guid?)null);

/// <summary>
/// Hands a job to the background push worker. Never blocks, never throws, never touches the database:
/// whatever raised the job has already happened and must not be failed or slowed by its notification.
/// </summary>
public interface IPushQueue
{
    void Enqueue(PushJob job);
}

/// <summary>Push is switched off: jobs are dropped.</summary>
public sealed class NoPushQueue : IPushQueue
{
    public void Enqueue(PushJob job) { }
}
