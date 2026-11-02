using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Moderation;

/// <summary>The kinds of thing a person can report. Templates have their own queue.</summary>
public static class ContentReportKinds
{
    public const string Post = "post";
    public const string Comment = "comment";
    public const string Photo = "photo";
    public const string User = "user";

    public static readonly string[] All = [Post, Comment, Photo, User];
}

/// <summary>
/// What one viewer has asked not to see: everything from the people they blocked, and each thing they
/// reported. Read once per page and applied to the feed, comments and albums.
/// </summary>
public sealed class ModerationView
{
    public static readonly ModerationView Empty = new(new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<Guid>(), new HashSet<Guid>());

    public ModerationView(
        IReadOnlySet<Guid> blockedUsers, IReadOnlySet<Guid> blockedGuests,
        IReadOnlySet<Guid> hiddenPosts, IReadOnlySet<Guid> hiddenComments, IReadOnlySet<Guid> hiddenPhotos)
    {
        BlockedUsers = blockedUsers;
        BlockedGuests = blockedGuests;
        HiddenPosts = hiddenPosts;
        HiddenComments = hiddenComments;
        HiddenPhotos = hiddenPhotos;
    }

    /// <summary>Accounts this viewer blocked.</summary>
    public IReadOnlySet<Guid> BlockedUsers { get; }

    /// <summary>Guest-list rows that belong to a blocked account (same email or phone), for photos from before uploads kept the account.</summary>
    public IReadOnlySet<Guid> BlockedGuests { get; }

    public IReadOnlySet<Guid> HiddenPosts { get; }
    public IReadOnlySet<Guid> HiddenComments { get; }
    public IReadOnlySet<Guid> HiddenPhotos { get; }

    public bool IsEmpty => BlockedUsers.Count == 0 && HiddenPosts.Count == 0 && HiddenComments.Count == 0 && HiddenPhotos.Count == 0;

    public bool HidesComment(PostComment c) => HiddenComments.Contains(c.Id) || BlockedUsers.Contains(c.UserId);

    public bool HidesPhoto(EventPhoto p) =>
        HiddenPhotos.Contains(p.Id)
        || (p.UploaderUserId is { } u && BlockedUsers.Contains(u))
        || (p.UploaderUserId is null && p.GuestId is { } g && BlockedGuests.Contains(g));

    /// <summary>An event's post: reported by this viewer, or made by someone they blocked.</summary>
    public bool HidesPost(Guid campaignId, Guid? hostUserId) =>
        HiddenPosts.Contains(campaignId) || (hostUserId is { } h && BlockedUsers.Contains(h));
}

public interface IModerationViewService
{
    /// <summary>The signed-in viewer's view; empty for anyone not signed in.</summary>
    Task<ModerationView> ForCurrentAsync(CancellationToken ct = default);

    Task<ModerationView> ForAsync(Guid? userId, CancellationToken ct = default);

    /// <summary>Whether <paramref name="recipient"/> blocked <paramref name="actor"/>: their pushes are dropped.</summary>
    Task<bool> HasBlockedAsync(Guid recipient, Guid actor, CancellationToken ct = default);
}

public sealed class ModerationViewService(
    ICurrentUser currentUser,
    IRepository<UserBlock> blocks,
    IRepository<ContentReport> reports,
    IRepository<AppUser> users,
    IGuestRepository guests) : IModerationViewService
{
    public Task<ModerationView> ForCurrentAsync(CancellationToken ct = default) => ForAsync(currentUser.UserId, ct);

    public async Task<ModerationView> ForAsync(Guid? userId, CancellationToken ct = default)
    {
        if (userId is not { } me) return ModerationView.Empty;

        var blocked = (await blocks.Query()
                .Where(b => b.BlockerUserId == me)
                .Select(b => b.BlockedUserId)
                .ToListAsync(ct))
            .ToHashSet();

        var mine = await reports.Query()
            .Where(r => r.ReporterUserId == me && r.Kind != ContentReportKinds.User)
            .Select(r => new { r.Kind, r.TargetId })
            .ToListAsync(ct);

        var blockedGuests = new HashSet<Guid>();
        if (blocked.Count > 0)
        {
            var contacts = await users.Query()
                .Where(u => blocked.Contains(u.Id))
                .Select(u => new { u.Email, u.PhoneE164 })
                .ToListAsync(ct);
            var emails = contacts.Select(c => c.Email?.Trim().ToLowerInvariant())
                .Where(e => !string.IsNullOrEmpty(e)).Cast<string>().ToList();
            var phones = contacts.Select(c => c.PhoneE164?.Trim())
                .Where(p => !string.IsNullOrEmpty(p)).Cast<string>().ToList();
            if (emails.Count > 0 || phones.Count > 0)
                blockedGuests = (await guests.Query()
                        .Where(g => (g.Email != null && emails.Contains(g.Email)) || (g.PhoneE164 != null && phones.Contains(g.PhoneE164)))
                        .Select(g => g.Id)
                        .ToListAsync(ct))
                    .ToHashSet();
        }

        HashSet<Guid> Of(string kind) => mine.Where(r => r.Kind == kind).Select(r => r.TargetId).ToHashSet();
        return new ModerationView(blocked, blockedGuests,
            Of(ContentReportKinds.Post), Of(ContentReportKinds.Comment), Of(ContentReportKinds.Photo));
    }

    public Task<bool> HasBlockedAsync(Guid recipient, Guid actor, CancellationToken ct = default) =>
        blocks.Query().AnyAsync(b => b.BlockerUserId == recipient && b.BlockedUserId == actor, ct);
}
