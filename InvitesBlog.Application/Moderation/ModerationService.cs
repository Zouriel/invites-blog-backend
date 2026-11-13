using System.Net;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Services.Feed;
using InvitesBlog.Application.Services.MediaBuckets;
using InvitesBlog.Application.Services.Photos;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Application.Moderation;

public sealed record ReportContentRequest(string? Kind, Guid TargetId, string? Reason, string? Note);

/// <param name="Hidden">Whether the item is now hidden for the reporter (everything but a person).</param>
public sealed record ReportResultDto(bool Hidden, string Message);

public sealed record BlockedPersonDto(Guid UserId, string DisplayName, string? AvatarUrl, DateTimeOffset BlockedAt);

public sealed record AdminContentReportDto(
    Guid Id,
    string Kind,
    Guid TargetId,
    Guid? CampaignId,
    string? EventTitle,
    string Reason,
    string? Note,
    string? Snapshot,
    string Status,
    string? Resolution,
    string? ResolutionNote,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ResolvedAt,
    string ReporterName,
    string? ReporterEmail,
    Guid? SubjectUserId,
    string? SubjectName,
    string? SubjectEmail,
    bool SubjectActive,
    string? CurrentText,
    string? CurrentImageUrl,
    bool ItemGone,
    int ReportCount);

/// <param name="Action">dismiss | remove.</param>
public sealed record ResolveContentReportRequest(string? Action, string? Note);

/// <summary>
/// Reporting and blocking (App Store guideline 1.2, Google Play's user-generated content policy), and
/// the admin side of both. See docs/MODERATION.md for the rules in plain words.
/// </summary>
public interface IModerationService
{
    Task<ReportResultDto> ReportAsync(ReportContentRequest request, CancellationToken ct = default);

    Task<IReadOnlyList<BlockedPersonDto>> BlockedAsync(CancellationToken ct = default);
    Task BlockAsync(Guid userId, CancellationToken ct = default);
    Task UnblockAsync(Guid userId, CancellationToken ct = default);

    Task<IReadOnlyList<AdminContentReportDto>> ListAsync(string? status, CancellationToken ct = default);
    Task<AdminContentReportDto> ResolveAsync(Guid reportId, ResolveContentReportRequest request, CancellationToken ct = default);
    Task SetSuspendedAsync(Guid userId, bool suspended, CancellationToken ct = default);
}

public sealed class ModerationService(
    ICurrentUser currentUser,
    IRepository<ContentReport> reports,
    IRepository<UserBlock> blocks,
    IRepository<AppUser> users,
    IRepository<PostComment> comments,
    IRepository<EventPhoto> photos,
    ICampaignRepository campaigns,
    IGuestRepository guests,
    IFeedService feed,
    IMediaBucketService buckets,
    IEventPhotoService photoService,
    IReportMailService mail,
    IUnitOfWork uow) : IModerationService
{
    public static readonly string[] Reasons = ["abusive", "hateful", "sexual", "violent", "spam", "other"];

    public const int MaxNote = 1000;

    /// <summary>How many people one account may block. Generous; it stops a script, not a person.</summary>
    public const int MaxBlocks = 1000;

    // ---------- reporting ----------

    public async Task<ReportResultDto> ReportAsync(ReportContentRequest request, CancellationToken ct = default)
    {
        var me = await MeAsync(ct);
        var kind = request.Kind?.Trim().ToLowerInvariant();
        if (kind is null || !ContentReportKinds.All.Contains(kind))
            throw new BusinessRuleException("Choose what you're reporting.", "kind_required");
        var reason = request.Reason?.Trim().ToLowerInvariant();
        if (reason is null || !Reasons.Contains(reason))
            throw new BusinessRuleException("Choose what's wrong with it.", "reason_required");
        var note = request.Note?.Trim();
        if (note is { Length: > MaxNote }) note = note[..MaxNote];
        if (string.IsNullOrWhiteSpace(note)) note = null;

        var item = await FindAsync(kind, request.TargetId, me, ct);
        if (item.SubjectUserId == me.Id)
            throw new BusinessRuleException(
                kind == ContentReportKinds.User ? "You can't report yourself." : "That's yours. You can remove it instead.",
                "own_content");

        // One open report per person per thing; pressing again just updates the reason and note.
        var open = await reports.Query(tracking: true).FirstOrDefaultAsync(r =>
            r.ReporterUserId == me.Id && r.Kind == kind && r.TargetId == request.TargetId
            && r.Status == TemplateReportStatus.Open, ct);
        if (open is not null)
        {
            open.Reason = reason;
            open.Note = note;
        }
        else
        {
            open = new ContentReport
            {
                Id = Guid.NewGuid(),
                ReporterUserId = me.Id,
                Kind = kind,
                TargetId = request.TargetId,
                CampaignId = item.CampaignId,
                SubjectUserId = item.SubjectUserId,
                Reason = reason,
                Note = note,
                Snapshot = item.Snapshot is { Length: > 2000 } s ? s[..2000] : item.Snapshot,
                Status = TemplateReportStatus.Open,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            await reports.AddAsync(open, ct);
        }
        await uow.SaveChangesAsync(ct);

        var subject = item.SubjectUserId is { } sid ? await users.GetByIdAsync(sid, ct) : null;
        await mail.SendAsync(
            $"Report: {Describe(kind)} ({reason})",
            [
                ("What", Describe(kind)),
                ("Reason", reason),
                ("Note", note),
                ("Event", item.EventTitle),
                ("It says or shows", item.Snapshot),
                ("Whose", subject is null ? null : $"{subject.DisplayName} ({subject.Email ?? subject.PhoneE164})"),
                ("Reported by", $"{me.DisplayName} ({me.Email ?? me.PhoneE164})"),
            ],
            ct);

        var hidden = kind != ContentReportKinds.User;
        return new ReportResultDto(hidden, hidden
            ? "Thanks for telling us. It's hidden for you now, and we'll look at it within 24 hours."
            : "Thanks for telling us. We'll look within 24 hours. You can also block them so you don't see anything they post.");
    }

    private sealed record Item(Guid? CampaignId, Guid? SubjectUserId, string? Snapshot, string? EventTitle);

    /// <summary>The thing being reported, refused unless the reporter can actually see it.</summary>
    private async Task<Item> FindAsync(string kind, Guid id, AppUser me, CancellationToken ct)
    {
        switch (kind)
        {
            case ContentReportKinds.Comment:
            {
                var comment = await comments.Query().FirstOrDefaultAsync(c => c.Id == id, ct)
                              ?? throw new NotFoundException("That comment is no longer here.");
                if (!await feed.CanSeeAsync(comment.CampaignId, ct))
                    throw new NotFoundException("That comment is no longer here.");
                var title = (await campaigns.GetByIdAsync(comment.CampaignId, ct))?.Title;
                return new Item(comment.CampaignId, comment.UserId, comment.Body, title);
            }
            case ContentReportKinds.Post:
            {
                var campaign = await campaigns.GetByIdAsync(id, ct)
                               ?? throw new NotFoundException("That post is no longer here.");
                if (!await feed.CanSeeAsync(id, ct)) throw new NotFoundException("That post is no longer here.");
                return new Item(id, campaign.CreatedByUserId, campaign.PostCaption ?? campaign.Title, campaign.Title);
            }
            case ContentReportKinds.Photo:
            {
                var photo = await photos.Query().FirstOrDefaultAsync(p => p.Id == id && p.DeletedAt == null, ct)
                            ?? throw new NotFoundException("That photo is no longer here.");
                var visible = (photo.CampaignId is { } cid && await feed.CanSeeAsync(cid, ct))
                              || (photo.BucketId is { } bid && await buckets.MayViewAsync(bid, ct));
                if (!visible) throw new NotFoundException("That photo is no longer here.");
                var title = photo.CampaignId is { } c ? (await campaigns.GetByIdAsync(c, ct))?.Title : null;
                return new Item(photo.CampaignId, await UploaderAsync(photo, ct), photo.ThumbUrl, title);
            }
            default:
            {
                var user = await users.GetByIdAsync(id, ct) ?? throw new NotFoundException("That person isn't on invites.blog.");
                var snapshot = user.AvatarUrl is null ? user.DisplayName : $"{user.DisplayName} {user.AvatarUrl}";
                return new Item(null, user.Id, snapshot, null);
            }
        }
    }

    /// <summary>The account behind a photo: kept since uploads record it, otherwise the guest's account by contact.</summary>
    private async Task<Guid?> UploaderAsync(EventPhoto photo, CancellationToken ct)
    {
        if (photo.UploaderUserId is { } u) return u;
        if (photo.GuestId is not { } gid) return null;
        var guest = await guests.Query().FirstOrDefaultAsync(g => g.Id == gid, ct);
        if (guest is null) return null;
        var email = guest.Email?.Trim().ToLowerInvariant();
        var phone = guest.PhoneE164?.Trim();
        if (string.IsNullOrEmpty(email) && string.IsNullOrEmpty(phone)) return null;
        return await users.Query()
            .Where(x => (email != null && email != "" && x.Email == email) || (phone != null && phone != "" && x.PhoneE164 == phone))
            .Select(x => (Guid?)x.Id)
            .FirstOrDefaultAsync(ct);
    }

    // ---------- blocking ----------

    public async Task<IReadOnlyList<BlockedPersonDto>> BlockedAsync(CancellationToken ct = default)
    {
        var me = await MeAsync(ct);
        var rows = await blocks.Query()
            .Where(b => b.BlockerUserId == me.Id)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync(ct);
        var ids = rows.Select(r => r.BlockedUserId).ToList();
        var people = await users.Query()
            .Where(u => ids.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName, u.AvatarUrl })
            .ToDictionaryAsync(u => u.Id, ct);
        return rows
            .Select(r => people.TryGetValue(r.BlockedUserId, out var p)
                ? new BlockedPersonDto(r.BlockedUserId, p.DisplayName, p.AvatarUrl, r.CreatedAt)
                : new BlockedPersonDto(r.BlockedUserId, "Someone", null, r.CreatedAt))
            .ToList();
    }

    public async Task BlockAsync(Guid userId, CancellationToken ct = default)
    {
        var me = await MeAsync(ct);
        if (userId == me.Id) throw new BusinessRuleException("You can't block yourself.", "own_account");
        if (await users.GetByIdAsync(userId, ct) is null)
            throw new NotFoundException("That person isn't on invites.blog.");
        if (await blocks.Query().AnyAsync(b => b.BlockerUserId == me.Id && b.BlockedUserId == userId, ct)) return;
        if (await blocks.Query().CountAsync(b => b.BlockerUserId == me.Id, ct) >= MaxBlocks)
            throw new BusinessRuleException("You've blocked as many people as one account can.", "too_many_blocks");

        await blocks.AddAsync(new UserBlock
        {
            Id = Guid.NewGuid(), BlockerUserId = me.Id, BlockedUserId = userId, CreatedAt = DateTimeOffset.UtcNow,
        }, ct);
        try
        {
            await uow.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Pressed twice at once: fine if the other press already blocked them.
            if (!await blocks.Query().AnyAsync(b => b.BlockerUserId == me.Id && b.BlockedUserId == userId, CancellationToken.None))
                throw;
        }
    }

    public async Task UnblockAsync(Guid userId, CancellationToken ct = default)
    {
        var me = await MeAsync(ct);
        var row = await blocks.Query(tracking: true)
            .FirstOrDefaultAsync(b => b.BlockerUserId == me.Id && b.BlockedUserId == userId, ct);
        if (row is null) return;
        blocks.Remove(row);
        await uow.SaveChangesAsync(ct);
    }

    // ---------- admin ----------

    public async Task<IReadOnlyList<AdminContentReportDto>> ListAsync(string? status, CancellationToken ct = default)
    {
        var query = reports.Query();
        query = status switch
        {
            "resolved" => query.Where(r => r.Status == TemplateReportStatus.Resolved).OrderByDescending(r => r.ResolvedAt),
            "all" => query.OrderByDescending(r => r.CreatedAt),
            _ => query.Where(r => r.Status == TemplateReportStatus.Open).OrderBy(r => r.CreatedAt),
        };
        var rows = await query.Take(500).ToListAsync(ct);
        return await ToDtosAsync(rows, ct);
    }

    public async Task<AdminContentReportDto> ResolveAsync(
        Guid reportId, ResolveContentReportRequest request, CancellationToken ct = default)
    {
        var report = await reports.Query(tracking: true).FirstOrDefaultAsync(r => r.Id == reportId, ct)
                     ?? throw new NotFoundException("That report doesn't exist.", "report_not_found");
        var note = request.Note?.Trim();
        if (note is { Length: > MaxNote }) note = note[..MaxNote];
        var now = DateTimeOffset.UtcNow;

        string resolution;
        switch (request.Action)
        {
            case "dismiss":
                resolution = "dismissed";
                break;
            case "remove":
                await RemoveItemAsync(report, now, ct);
                resolution = "removed";
                break;
            default:
                throw new BusinessRuleException("Choose dismiss or remove.", "action_required");
        }

        // Removing something settles every open report about it, not just this one.
        var settled = request.Action == "dismiss"
            ? [report]
            : await reports.Query(tracking: true)
                .Where(r => r.Kind == report.Kind && r.TargetId == report.TargetId && r.Status == TemplateReportStatus.Open)
                .ToListAsync(ct);
        if (!settled.Contains(report)) settled.Add(report);
        foreach (var r in settled)
        {
            r.Status = TemplateReportStatus.Resolved;
            r.Resolution = resolution;
            r.ResolutionNote = string.IsNullOrWhiteSpace(note) ? null : note;
            r.ResolvedAt = now;
            r.ResolvedByUserId = currentUser.UserId;
        }
        await uow.SaveChangesAsync(ct);
        return (await ToDtosAsync([report], ct))[0];
    }

    private async Task RemoveItemAsync(ContentReport report, DateTimeOffset now, CancellationToken ct)
    {
        switch (report.Kind)
        {
            case ContentReportKinds.Comment:
                foreach (var c in await comments.Query(tracking: true)
                             .Where(c => (c.Id == report.TargetId || c.ParentId == report.TargetId) && c.DeletedAt == null)
                             .ToListAsync(ct))
                    c.DeletedAt = now;
                break;
            case ContentReportKinds.Post:
                var campaign = await campaigns.GetByIdAsync(report.TargetId, ct);
                if (campaign is not null && campaign.PostHiddenAt is null)
                {
                    campaign.PostHiddenAt = now;
                    campaigns.Update(campaign);
                }
                break;
            case ContentReportKinds.Photo:
                await photoService.RemoveForModerationAsync(report.TargetId, ct);
                break;
            case ContentReportKinds.User:
                // A person's name or picture: the picture goes and the name is reset. They can choose
                // new ones, which the next report would catch.
                var user = await users.Query(tracking: true).FirstOrDefaultAsync(u => u.Id == report.TargetId, ct);
                if (user is not null)
                {
                    user.AvatarUrl = null;
                    user.AvatarChosenAt = null;
                    user.DisplayName = "invites.blog member";
                }
                break;
        }
        await uow.SaveChangesAsync(ct);
    }

    public async Task SetSuspendedAsync(Guid userId, bool suspended, CancellationToken ct = default)
    {
        if (suspended && currentUser.UserId == userId)
            throw new BusinessRuleException("You can't suspend your own account.", "own_account");
        var user = await users.Query(tracking: true).FirstOrDefaultAsync(u => u.Id == userId, ct)
                   ?? throw new NotFoundException("That account doesn't exist.", "user_not_found");
        // A suspended account can't sign in, and its sessions stop working at once (see the
        // authentication handler). What it already posted stays until removed from the queue.
        user.IsActive = !suspended;
        await uow.SaveChangesAsync(ct);
    }

    private async Task<IReadOnlyList<AdminContentReportDto>> ToDtosAsync(IReadOnlyList<ContentReport> rows, CancellationToken ct)
    {
        var userIds = rows.Select(r => r.ReporterUserId)
            .Concat(rows.Select(r => r.SubjectUserId).OfType<Guid>())
            .Concat(rows.Where(r => r.Kind == ContentReportKinds.User).Select(r => r.TargetId))
            .Distinct().ToList();
        var people = await users.Query()
            .Where(u => userIds.Contains(u.Id))
            .Select(u => new { u.Id, u.DisplayName, u.Email, u.PhoneE164, u.IsActive, u.AvatarUrl })
            .ToDictionaryAsync(u => u.Id, ct);

        var campaignIds = rows.Select(r => r.CampaignId).OfType<Guid>()
            .Concat(rows.Where(r => r.Kind == ContentReportKinds.Post).Select(r => r.TargetId))
            .Distinct().ToList();
        var events = await campaigns.Query()
            .Where(c => campaignIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Title, c.PostCaption, c.PostHiddenAt })
            .ToDictionaryAsync(c => c.Id, ct);

        var commentIds = rows.Where(r => r.Kind == ContentReportKinds.Comment).Select(r => r.TargetId).ToList();
        var commentRows = await comments.Query()
            .Where(c => commentIds.Contains(c.Id))
            .Select(c => new { c.Id, c.Body, c.DeletedAt })
            .ToDictionaryAsync(c => c.Id, ct);

        var photoIds = rows.Where(r => r.Kind == ContentReportKinds.Photo).Select(r => r.TargetId).ToList();
        var photoRows = await photos.Query()
            .Where(p => photoIds.Contains(p.Id))
            .Select(p => new { p.Id, p.ThumbUrl, p.DeletedAt })
            .ToDictionaryAsync(p => p.Id, ct);

        var targets = rows.Select(r => r.TargetId).Distinct().ToList();
        var counts = (await reports.Query()
                .Where(r => targets.Contains(r.TargetId))
                .Select(r => new { r.Kind, r.TargetId })
                .ToListAsync(ct))
            .GroupBy(r => (r.Kind, r.TargetId))
            .ToDictionary(g => g.Key, g => g.Count());

        return rows.Select(r =>
        {
            people.TryGetValue(r.ReporterUserId, out var reporter);
            var subjectId = r.SubjectUserId;
            var subject = subjectId is { } s && people.TryGetValue(s, out var p) ? p : null;
            string? text = null, image = null;
            var gone = false;
            switch (r.Kind)
            {
                case ContentReportKinds.Comment:
                    if (commentRows.TryGetValue(r.TargetId, out var c)) { text = c.Body; gone = c.DeletedAt is not null; }
                    else gone = true;
                    break;
                case ContentReportKinds.Post:
                    if (events.TryGetValue(r.TargetId, out var e)) { text = e.PostCaption ?? e.Title; gone = e.PostHiddenAt is not null; }
                    else gone = true;
                    break;
                case ContentReportKinds.Photo:
                    if (photoRows.TryGetValue(r.TargetId, out var ph)) { image = ph.ThumbUrl; gone = ph.DeletedAt is not null; }
                    else gone = true;
                    break;
                case ContentReportKinds.User:
                    if (people.TryGetValue(r.TargetId, out var u)) { text = u.DisplayName; image = u.AvatarUrl; }
                    else gone = true;
                    break;
            }
            var title = r.CampaignId is { } cid && events.TryGetValue(cid, out var ev) ? ev.Title : null;
            return new AdminContentReportDto(
                r.Id, r.Kind, r.TargetId, r.CampaignId, title, r.Reason, r.Note, r.Snapshot,
                r.Status.ToString(), r.Resolution, r.ResolutionNote, r.CreatedAt, r.ResolvedAt,
                reporter?.DisplayName ?? "(deleted account)", reporter?.Email ?? reporter?.PhoneE164,
                subjectId, subject?.DisplayName, subject?.Email ?? subject?.PhoneE164, subject?.IsActive ?? false,
                text, image, gone, counts.GetValueOrDefault((r.Kind, r.TargetId)));
        }).ToList();
    }

    private static string Describe(string kind) => kind switch
    {
        ContentReportKinds.Post => "an event's post",
        ContentReportKinds.Comment => "a comment",
        ContentReportKinds.Photo => "a photo or video",
        _ => "a person",
    };

    private async Task<AppUser> MeAsync(CancellationToken ct)
    {
        var id = currentUser.UserId ?? throw new UnauthorizedException();
        return await users.GetByIdAsync(id, ct) ?? throw new UnauthorizedException();
    }
}

/// <summary>Tells the owner about a new report straight away, by email. Never fails the report itself.</summary>
public interface IReportMailService
{
    Task SendAsync(string subject, IReadOnlyList<(string Label, string? Value)> lines, CancellationToken ct = default);
}

public sealed class ReportMailService(
    IEmailSender email,
    Microsoft.Extensions.Configuration.IConfiguration config,
    Microsoft.Extensions.Logging.ILogger<ReportMailService> logger) : IReportMailService
{
    public async Task SendAsync(string subject, IReadOnlyList<(string Label, string? Value)> lines, CancellationToken ct = default)
    {
        var to = config["Moderation:ReportEmail"];
        if (string.IsNullOrWhiteSpace(to)) to = config["Admin:Email"];
        if (string.IsNullOrWhiteSpace(to))
        {
            logger.LogWarning("A report came in but no Moderation:ReportEmail is set, so nobody was emailed.");
            return;
        }

        var queue = $"{Common.AppUrls.AppBase(config)}/admin/reports";
        var rows = string.Concat(lines
            .Where(l => !string.IsNullOrWhiteSpace(l.Value))
            .Select(l => $"<p><strong>{WebUtility.HtmlEncode(l.Label)}:</strong> {WebUtility.HtmlEncode(l.Value)}</p>"));
        var html = $"<div style=\"font-family:sans-serif\"><h2>{WebUtility.HtmlEncode(subject)}</h2>{rows}"
                   + $"<p><a href=\"{WebUtility.HtmlEncode(queue)}\">Open the reports queue</a>. Act within 24 hours.</p></div>";
        try
        {
            await email.SendAsync(new EmailMessage(to.Trim(), $"invites.blog {subject}", html), ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            logger.LogError(e, "Couldn't email the owner about a report; it is still in the queue.");
        }
    }
}
