using System.Text.RegularExpressions;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Common;
using InvitesBlog.Application.Delivery;
using InvitesBlog.Application.Dtos.Guidance;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Exceptions.Accounts;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Services.Guidance;

/// <summary>
/// Tips, tours and offers (docs/GUIDANCE.md): what the signed-in account has seen, whether tips are
/// on, how far it has got (the Getting started checklist), and the events it was newly added to as
/// a celebrant. Every member needs a signed-in account; anything else is a 401.
/// </summary>
public interface IGuidanceService
{
    Task<GuidanceDto> GetAsync(CancellationToken ct = default);
    Task MarkSeenAsync(MarkGuidanceSeenRequest req, CancellationToken ct = default);
    Task SetAsync(SetGuidanceRequest req, CancellationToken ct = default);
    Task ResetAsync(CancellationToken ct = default);

    /// <summary>
    /// Marks the signed-in account's celebrant row(s) on this event as seen, so it leaves <c>newRoles</c>.
    /// Does nothing when there are none: never an error, never a hint either way. 401 without an account.
    /// </summary>
    Task AcknowledgeRoleAsync(Guid campaignId, CancellationToken ct = default);
}

/// <summary>The bounds of docs/GUIDANCE.md section 2.</summary>
public static partial class GuidanceRules
{
    /// <summary>Most ids one POST /seen may carry.</summary>
    public const int MaxPerCall = 50;

    /// <summary>Most ids kept per account; past it the oldest go first.</summary>
    public const int MaxStored = 500;

    /// <summary>How long after the account was made welcome tips and tours are offered.</summary>
    public static readonly TimeSpan WelcomeFor = TimeSpan.FromDays(30);

    /// <summary>An id: lowercase letters, digits, dots and dashes, 1 to 64 long (e.g. "offer.role.{guid}").</summary>
    public static bool IsValidId(string? id) => id is not null && IdPattern().IsMatch(id);

    [GeneratedRegex("^[a-z0-9.-]{1,64}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdPattern();

    /// <summary>
    /// <paramref name="stored"/> with <paramref name="add"/> appended (no repeats, order kept), trimmed to
    /// the newest <see cref="MaxStored"/>. An id seen again keeps its first place.
    /// </summary>
    public static List<string> Merge(IReadOnlyList<string> stored, IEnumerable<string> add)
    {
        var list = new List<string>(stored);
        var have = new HashSet<string>(stored, StringComparer.Ordinal);
        foreach (var id in add)
            if (have.Add(id)) list.Add(id);
        return list.Count > MaxStored ? list.GetRange(list.Count - MaxStored, MaxStored) : list;
    }

    /// <summary>
    /// Whether <paramref name="user"/>'s name is one they gave. Sign-up fills a placeholder when it has
    /// none: the email's local part, the phone number, "You" for an Apple relay address, and moderation
    /// renames an objectionable one to "invites.blog member". Those don't count.
    /// </summary>
    public static bool HasOwnName(AppUser user)
    {
        var name = user.DisplayName?.Trim();
        if (string.IsNullOrEmpty(name)) return false;
        if (name.Equals("You", StringComparison.OrdinalIgnoreCase)) return false;
        if (name.Equals("invites.blog member", StringComparison.OrdinalIgnoreCase)) return false;
        if (user.Email is { } email && email.Contains('@')
            && name.Equals(email.Split('@')[0], StringComparison.OrdinalIgnoreCase)) return false;
        if (user.PhoneE164 is { } phone && Digits(name) == Digits(phone) && Digits(name).Length > 0) return false;
        return true;

        static string Digits(string s) => new(s.Where(char.IsDigit).ToArray());
    }
}

public sealed class GuidanceService(
    ICurrentUser currentUser,
    IUnitOfWork uow,
    IRepository<AppUser> users,
    IRepository<Campaign> campaigns,
    IRepository<Inviter> inviters,
    IRepository<Guest> guests,
    IRepository<Invite> invites,
    IRepository<DeliveryAttempt> attempts,
    IRepository<EventPhoto> photos,
    IRepository<TemplateDesign> designs,
    IRepository<Template> templates,
    IRepository<CampaignCelebrant> celebrants) : IGuidanceService
{
    public async Task<GuidanceDto> GetAsync(CancellationToken ct = default)
    {
        var me = await CurrentAsync(tracking: false, ct);
        return new GuidanceDto(
            !me.GuidanceTipsOff,
            me.GuidanceSeen,
            me.CreatedAt + GuidanceRules.WelcomeFor,
            await ProgressAsync(me, ct),
            await NewRolesAsync(me, ct));
    }

    public async Task MarkSeenAsync(MarkGuidanceSeenRequest req, CancellationToken ct = default)
    {
        var me = await CurrentAsync(tracking: true, ct);
        var ids = req?.Ids ?? throw new BusinessRuleException("Send the ids that were seen.", "guidance_ids_required");
        if (ids.Count > GuidanceRules.MaxPerCall)
            throw new BusinessRuleException($"Send at most {GuidanceRules.MaxPerCall} ids at a time.", "guidance_too_many_ids");
        if (ids.Any(id => !GuidanceRules.IsValidId(id)))
            throw new BusinessRuleException("A tip id may use only a-z, 0-9, '.' and '-', up to 64 characters.", "guidance_id_invalid");
        if (ids.Count == 0) return;

        var merged = GuidanceRules.Merge(me.GuidanceSeen, ids);
        if (merged.SequenceEqual(me.GuidanceSeen, StringComparer.Ordinal)) return;
        // A new list rather than changing the old one in place: EF notices a replaced array for certain.
        me.GuidanceSeen = merged;
        await uow.SaveChangesAsync(ct);
    }

    public async Task SetAsync(SetGuidanceRequest req, CancellationToken ct = default)
    {
        if (req?.TipsOn is not { } on)
            throw new BusinessRuleException("Say whether tips are on.", "guidance_tips_on_required");
        var me = await CurrentAsync(tracking: true, ct);
        me.GuidanceTipsOff = !on;
        await uow.SaveChangesAsync(ct);
    }

    public async Task ResetAsync(CancellationToken ct = default)
    {
        var me = await CurrentAsync(tracking: true, ct);
        me.GuidanceSeen = [];
        me.GuidanceTipsOff = false;
        await uow.SaveChangesAsync(ct);
    }

    public async Task AcknowledgeRoleAsync(Guid campaignId, CancellationToken ct = default)
    {
        var userId = currentUser.UserId ?? throw new UnauthorizedException();
        var me = await users.GetByIdAsync(userId, ct) ?? throw new UnauthorizedException();
        var (email, phone) = Contacts(me);
        if (email is null && phone is null) return;

        var rows = await celebrants.Query(tracking: true)
            .Where(c => c.CampaignId == campaignId && c.AcknowledgedAt == null
                        && ((email != null && c.Email == email) || (phone != null && c.PhoneE164 == phone)))
            .ToListAsync(ct);
        if (rows.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var row in rows) row.AcknowledgedAt = now;
        await uow.SaveChangesAsync(ct);
    }

    // ----- Progress -------------------------------------------------------------------------------

    private async Task<GuidanceProgressDto> ProgressAsync(AppUser me, CancellationToken ct)
    {
        var hosted = await HostedAsync(me, ct);
        var ids = hosted.Select(h => h.Id).ToList();
        var any = ids.Count > 0;

        var hasGuests = any && await guests.Query().AnyAsync(g => ids.Contains(g.CampaignId), ct);

        // Sent: an invite went out (Sent, or Viewed which comes after it, or emailed at least once), the
        // host sent one by hand, or the event has an open link to share. Only asked when there are guests
        // or a link: nothing can have been sent otherwise.
        var hasSent = hosted.Any(h => h.HasOpenLink)
            || (hasGuests && await invites.Query().AnyAsync(i => ids.Contains(i.CampaignId)
                    && (i.Status == InviteStatus.Sent || i.Status == InviteStatus.Viewed || i.FirstEmailedAt != null), ct))
            || (hasGuests && await attempts.Query().AnyAsync(a => a.Channel.EndsWith(ManualSends.Suffix)
                    && invites.Query().Any(i => i.Id == a.InviteId && ids.Contains(i.CampaignId)), ct));

        var hasPhotoInAlbum = any && await photos.Query()
            .AnyAsync(p => p.CampaignId != null && ids.Contains(p.CampaignId.Value) && p.DeletedAt == null, ct);

        var hasDesign = await designs.Query().AnyAsync(d => d.OwnerUserId == me.Id, ct);

        return new GuidanceProgressDto(
            HasName: GuidanceRules.HasOwnName(me),
            HasPhoto: !string.IsNullOrWhiteSpace(me.AvatarUrl),
            HasEvent: any,
            HasGuests: hasGuests,
            HasSent: hasSent,
            HasPhotoInAlbum: hasPhotoInAlbum,
            HasDesign: hasDesign);
    }

    private sealed record Hosted(Guid Id, bool HasOpenLink);

    /// <summary>The events this account organises: it started them, or it is their host (Inviter).</summary>
    private async Task<IReadOnlyList<Hosted>> HostedAsync(AppUser me, CancellationToken ct)
    {
        var inviterIds = await InviterIdsAsync(me, ct);
        return await campaigns.Query()
            .Where(c => c.CreatedByUserId == me.Id || (c.InviterId != null && inviterIds.Contains(c.InviterId.Value)))
            .Select(c => new Hosted(c.Id, c.OpenLinkCode != null))
            .ToListAsync(ct);
    }

    private async Task<List<Guid>> InviterIdsAsync(AppUser me, CancellationToken ct)
    {
        var email = me.Email;
        var phone = me.PhoneE164;
        if (string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(phone)) return [];
        return await inviters.Query()
            .Where(i => (email != null && i.Email == email) || (phone != null && i.PhoneE164 == phone))
            .Select(i => i.Id)
            .ToListAsync(ct);
    }

    // ----- New roles ------------------------------------------------------------------------------

    private async Task<IReadOnlyList<NewRoleDto>> NewRolesAsync(AppUser me, CancellationToken ct)
    {
        var (email, phone) = Contacts(me);
        if (email is null && phone is null) return [];

        var rows = await celebrants.Query()
            .Where(c => c.AcknowledgedAt == null
                        && ((email != null && c.Email == email) || (phone != null && c.PhoneE164 == phone)))
            .Join(campaigns.Query(), c => c.CampaignId, e => e.Id, (c, e) => new
            {
                c.CampaignId, c.CanManage, c.CreatedAt,
                e.Title, e.CustomContentJson, e.TemplateId, e.Status, e.CreatedByUserId, e.InviterId,
            })
            .Where(x => x.Status != CampaignStatus.Cancelled)
            .ToListAsync(ct);
        if (rows.Count == 0) return [];

        // Someone who organises the event isn't told they were added to it.
        var inviterIds = await InviterIdsAsync(me, ct);
        rows = rows.Where(r => r.CreatedByUserId != me.Id && !(r.InviterId is { } i && inviterIds.Contains(i))).ToList();
        if (rows.Count == 0) return [];

        var templateIds = rows.Select(r => r.TemplateId).Distinct().ToList();
        var posters = await templates.Query().Where(t => templateIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, t => t.PreviewImageUrl, ct);

        // The organiser's name: the account that started it, else the host named on it.
        var creatorIds = rows.Where(r => r.CreatedByUserId != null).Select(r => r.CreatedByUserId!.Value).Distinct().ToList();
        var creatorNames = await users.Query().Where(u => creatorIds.Contains(u.Id) && u.IsActive)
            .ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
        var hostIds = rows.Where(r => r.InviterId != null).Select(r => r.InviterId!.Value).Distinct().ToList();
        var hostNames = await inviters.Query().Where(i => hostIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, i => i.Name, ct);

        return rows
            // One card per event, even when both the email and the phone row match.
            .GroupBy(r => r.CampaignId)
            .Select(g =>
            {
                var r = g.OrderByDescending(x => x.CanManage).First();
                var addedBy = (r.CreatedByUserId is { } u ? creatorNames.GetValueOrDefault(u) : null)
                              ?? (r.InviterId is { } h ? hostNames.GetValueOrDefault(h) : null);
                return new NewRoleDto(
                    r.CampaignId, r.Title,
                    InvitesBlog.Application.Campaigns.CampaignCover.Read(r.CustomContentJson) ?? TemplatePoster.OrNull(posters.GetValueOrDefault(r.TemplateId)),
                    g.Any(x => x.CanManage) ? "full" : "look",
                    string.IsNullOrWhiteSpace(addedBy) ? null : addedBy.Trim(),
                    g.Min(x => x.CreatedAt));
            })
            .OrderByDescending(x => x.AddedAt)
            .ToList();
    }

    // ----- Helpers --------------------------------------------------------------------------------

    /// <summary>The account's contacts as celebrant rows store them (email lowercased, phone E.164).</summary>
    private static (string? Email, string? Phone) Contacts(AppUser me)
    {
        var email = string.IsNullOrWhiteSpace(me.Email) ? null : me.Email.Trim().ToLowerInvariant();
        var phone = string.IsNullOrWhiteSpace(me.PhoneE164) ? null : me.PhoneE164.Trim();
        return (email, phone);
    }

    private async Task<AppUser> CurrentAsync(bool tracking, CancellationToken ct)
    {
        var id = currentUser.UserId ?? throw new UnauthorizedException();
        var user = await users.Query(tracking).FirstOrDefaultAsync(u => u.Id == id, ct) ?? throw new UnauthorizedException();
        if (!user.IsActive) throw new AccountSuspendedException();
        return user;
    }
}
