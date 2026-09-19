using System.Security.Claims;
using System.Text.Json;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Common;
using InvitesBlog.Application.Dtos.Admin;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Filters.Admin;
using InvitesBlog.Application.Security;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using Microsoft.EntityFrameworkCore;

using InvitesBlog.Application.Plans;
using InvitesBlog.Domain.Enums;
namespace InvitesBlog.Application.Services.Admin;

/// <summary>
/// Admin business logic (full-RBAC): inspecting users/roles/permissions, granting and revoking the
/// grantable roles, and paged suppression + audit review. Admins sign in like everyone else
/// (AccountService, POST /api/auth/login) — there is no separate admin login.
/// </summary>
public sealed class AdminService(
    IRepository<AppUser> users,
    IRepository<Role> roles,
    IRepository<Permission> permissions,
    ISuppressionRepository suppression,
    IRepository<AuditLog> auditLogs,
    IRepository<UserRole> userRoles,
    ICurrentUser currentUser,
    IUnitOfWork uow,
    ICampaignRepository campaigns,
    IRepository<PassCredit> credits,
    MediaBuckets.IMediaBucketService buckets,
    IPlanService plans,
    ISendingAllowanceService allowances) : IAdminService
{
    public async Task<PagedResult<AdminUserDto>> ListUsersAsync(AdminUserFilter filter, CancellationToken ct = default)
    {
        var query = users.Query();

        if (filter.IsActive is { } active)
            query = query.Where(u => u.IsActive == active);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.ToLower();
            query = query.Where(u =>
                (u.Email != null && u.Email.ToLower().Contains(term)) ||
                u.DisplayName.ToLower().Contains(term));
        }

        switch (filter.Plan?.Trim().ToLowerInvariant())
        {
            case "studio":
                query = query.Where(u => u.SubscriptionTier == SubscriptionTier.Studio);
                break;
            case "venue":
                query = query.Where(u => u.SubscriptionTier == SubscriptionTier.Venue);
                break;
            case "passes":
                query = query.Where(u => credits.Query().Any(c => c.OwnerUserId == u.Id && c.UsedOnCampaignId == null));
                break;
        }

        var total = await query.CountAsync(ct);
        var page = await query
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .OrderBy(u => u.Email)
            .Skip(filter.Skip).Take(filter.PageSize)
            .ToListAsync(ct);

        var ids = page.Select(u => u.Id).ToList();
        var held = (await credits.Query()
                .Where(c => ids.Contains(c.OwnerUserId) && c.UsedOnCampaignId == null)
                .Select(c => new { c.OwnerUserId, c.Kind })
                .ToListAsync(ct))
            .GroupBy(c => c.OwnerUserId)
            .ToDictionary(g => g.Key, g => (g.Count(c => c.Kind == EventPassKind.Party), g.Count(c => c.Kind == EventPassKind.Wedding)));
        var items = page.Select(u => Describe(u, held.GetValueOrDefault(u.Id))).ToList();

        return PagedResult<AdminUserDto>.Create(items, total, filter);
    }

    public async Task<IReadOnlyList<AdminRoleDto>> ListRolesAsync(CancellationToken ct = default)
    {
        var list = await roles.Query()
            .Include(r => r.RolePermissions).ThenInclude(rp => rp.Permission)
            .OrderBy(r => r.Name)
            .ToListAsync(ct);

        return list.Select(r => new AdminRoleDto(
            r.Id, r.Name, r.Description, r.IsSystem,
            r.RolePermissions.Select(rp => rp.Permission.Name).OrderBy(n => n).ToList())).ToList();
    }

    public async Task<IReadOnlyList<AdminPermissionDto>> ListPermissionsAsync(CancellationToken ct = default) =>
        await permissions.Query()
            .OrderBy(p => p.Group).ThenBy(p => p.Name)
            .Select(p => new AdminPermissionDto(p.Id, p.Name, p.Group, p.Description))
            .ToListAsync(ct);

    public async Task<PagedResult<SuppressionEntryDto>> ListSuppressionAsync(SuppressionFilter filter, CancellationToken ct = default)
    {
        var query = suppression.Query();

        if (!string.IsNullOrWhiteSpace(filter.ContactType))
            query = query.Where(s => s.ContactType == filter.ContactType);

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(s => s.CreatedAt)
            .Skip(filter.Skip).Take(filter.PageSize)
            .Select(s => new SuppressionEntryDto(s.Id, s.ContactHash, s.ContactType, s.CreatedAt))
            .ToListAsync(ct);

        return PagedResult<SuppressionEntryDto>.Create(items, total, filter);
    }

    public async Task<PagedResult<AuditLogDto>> ListAuditAsync(AuditLogFilter filter, CancellationToken ct = default)
    {
        var query = auditLogs.Query();

        if (!string.IsNullOrWhiteSpace(filter.Action))
            query = query.Where(a => a.Action == filter.Action);
        if (filter.CampaignId is { } campaignId)
            query = query.Where(a => a.CampaignId == campaignId);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.ToLower();
            query = query.Where(a => a.Action.ToLower().Contains(term));
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderByDescending(a => a.CreatedAt)
            .Skip(filter.Skip).Take(filter.PageSize)
            .Select(a => new AuditLogDto(a.Id, a.Action, a.Actor, a.CampaignId, a.DataJson, a.CreatedAt))
            .ToListAsync(ct);

        return PagedResult<AuditLogDto>.Create(items, total, filter);
    }

    /// <summary>
    /// The one write on the admin surface: hand somebody a role, or take it back.
    ///
    /// <para>It refuses four things, and all four are about not being able to break the platform
    /// from a settings page:</para>
    /// <list type="bullet">
    ///   <item><b>A role that is not grantable.</b> Inviter, Invitee and Public describe how a
    ///     caller arrived rather than something an account holds; a row granting one is read by
    ///     nothing, which is worse than an error because it looks like it worked.</item>
    ///   <item><b>Taking Admin off yourself.</b> The single likeliest way to lock everyone out of
    ///     this page is to experiment with the toggle on your own row.</item>
    ///   <item><b>Taking Admin off the last admin.</b> Same outcome by a slower route, and the
    ///     seeder only restores the ONE address it is configured with.</item>
    ///   <item><b>An account that is not there.</b></item>
    /// </list>
    ///
    /// <para>Granting twice and revoking twice are both fine and change nothing: this says what
    /// should be true afterwards, not what to do, so a double-clicked toggle cannot half-apply.</para>
    /// </summary>
    public async Task<AdminUserDto> SetUserRoleAsync(
        Guid userId, SetUserRoleRequest req, CancellationToken ct = default)
    {
        var name = (req.Role ?? string.Empty).Trim();

        // Matched case-insensitively but compared against the canonical list, so "subscriber" works
        // from a hand-written request while "Subscribers" does not quietly become a new role.
        var canonical = Roles.Grantable.FirstOrDefault(
            r => string.Equals(r, name, StringComparison.OrdinalIgnoreCase));
        if (canonical is null)
            throw new BusinessRuleException(
                $"'{name}' isn't a role that can be granted by hand.", "role_not_grantable");

        var user = await users.Query(tracking: true)
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new NotFoundException("That account no longer exists.");

        var role = await roles.FirstOrDefaultAsync(r => r.Name == canonical, ct)
                   ?? throw new NotFoundException($"The {canonical} role hasn't been seeded yet.");

        var held = user.UserRoles.FirstOrDefault(ur => ur.RoleId == role.Id);
        if (req.Granted == (held is not null)) return Describe(user, await UnusedCreditsAsync(user.Id, ct));

        if (!req.Granted && canonical == Roles.Admin)
        {
            if (currentUser.UserId == userId)
                throw new BusinessRuleException(
                    "You can't take the admin role off yourself.", "cannot_demote_self");

            var admins = await userRoles.CountAsync(ur => ur.RoleId == role.Id, ct);
            if (admins <= 1)
                throw new BusinessRuleException(
                    "That's the last admin — grant it to somebody else first.", "last_admin");
        }

        // The Role navigation is set as well as the id. Describe() reads it back to report the
        // account as it now stands, and on a row added in this instant nothing has loaded it — an
        // untracked navigation here is a null reference on the way OUT, after the write succeeded.
        if (req.Granted) user.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = role.Id, Role = role });
        else user.UserRoles.Remove(held!);

        // Who may hold what is exactly the kind of change somebody needs to be able to reconstruct
        // afterwards, so it is recorded before it is saved rather than left to the database's memory.
        await auditLogs.AddAsync(new AuditLog
        {
            Id = Guid.NewGuid(),
            Action = req.Granted ? "admin.role.grant" : "admin.role.revoke",
            Actor = currentUser.UserId?.ToString() ?? "admin",
            DataJson = JsonSerializer.Serialize(new { user = user.Id, role = canonical }),
            CreatedAt = DateTimeOffset.UtcNow,
        }, ct);

        await uow.SaveChangesAsync(ct);
        return Describe(user, await UnusedCreditsAsync(user.Id, ct));
    }

    private AdminUserDto Describe(AppUser u, (int Party, int Wedding) held) => new(
        u.Id, u.Email, u.DisplayName, u.IsActive,
        u.UserRoles.Select(ur => ur.Role?.Name).OfType<string>().OrderBy(n => n).ToList(),
        u.SubscriptionTier.ToString(),
        u.SubscriptionEndsAt,
        PlanRules.IsActive(u.SubscriptionTier, u.SubscriptionEndsAt, DateTimeOffset.UtcNow),
        held.Party + held.Wedding,
        held.Party,
        held.Wedding);

    private async Task<(int Party, int Wedding)> UnusedCreditsAsync(Guid userId, CancellationToken ct) => (
        await credits.CountAsync(c => c.OwnerUserId == userId && c.UsedOnCampaignId == null && c.Kind == EventPassKind.Party, ct),
        await credits.CountAsync(c => c.OwnerUserId == userId && c.UsedOnCampaignId == null && c.Kind == EventPassKind.Wedding, ct));

    /// <summary>
    /// Sets an account's professional plan by hand, until billing exists: Studio for designers and
    /// planners, Venue for a resort or hall.
    ///
    /// <para>Choosing None ends an active plan now rather than erasing it, because the end date is
    /// when a venue's events stopped being covered and their photo retention counts from it.</para>
    /// </summary>
    public async Task<AdminUserDto> SetSubscriptionAsync(
        Guid userId, SetSubscriptionRequest req, CancellationToken ct = default)
    {
        if (!Enum.TryParse<SubscriptionTier>(req.Tier?.Trim(), ignoreCase: true, out var tier) || !Enum.IsDefined(tier))
            throw new BusinessRuleException("Choose None, Studio or Venue.", "tier_unknown");

        var now = DateTimeOffset.UtcNow;
        if (tier != SubscriptionTier.None && req.EndsAt is { } requested && requested <= now)
            throw new BusinessRuleException("The end date has to be in the future.", "tier_end_in_past");

        var user = await users.Query(tracking: true)
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new NotFoundException("That account no longer exists.");

        var wasActive = PlanRules.IsActive(user.SubscriptionTier, user.SubscriptionEndsAt, now);
        // One end date serves both plans, and a venue's events count their lapse from it; switching
        // straight across would overwrite the date the venue's photos are being kept by.
        if (wasActive && tier != SubscriptionTier.None && user.SubscriptionTier != tier)
            throw new BusinessRuleException(
                $"End their {user.SubscriptionTier} plan first (choose None), then give them {tier}.", "tier_switch");
        if (tier == SubscriptionTier.None)
        {
            if (wasActive) user.SubscriptionEndsAt = now;
            user.SubscriptionTier = SubscriptionTier.None;
        }
        else
        {
            user.SubscriptionTier = tier;
            user.SubscriptionEndsAt = req.EndsAt;
        }

        await auditLogs.AddAsync(new AuditLog
        {
            Id = Guid.NewGuid(),
            Action = "admin.subscription.set",
            Actor = currentUser.UserId?.ToString() ?? "admin",
            DataJson = JsonSerializer.Serialize(new { user = user.Id, tier = tier.ToString(), endsAt = user.SubscriptionEndsAt }),
            CreatedAt = now,
        }, ct);

        await uow.SaveChangesAsync(ct);
        return Describe(user, await UnusedCreditsAsync(user.Id, ct));
    }

    /// <summary>
    /// Adds passes to a Studio account's stock (a positive count) or takes unused ones away (a
    /// negative one), until they can be bought online.
    /// </summary>
    public async Task<AdminUserDto> AdjustPassCreditsAsync(
        Guid userId, AdjustPassCreditsRequest req, CancellationToken ct = default)
    {
        if (!Enum.TryParse<EventPassKind>(req.Kind?.Trim(), ignoreCase: true, out var kind) || kind == EventPassKind.None)
            throw new BusinessRuleException("Choose Party or Wedding.", "pass_kind_unknown");
        if (req.Count is 0 or > 100 or < -100)
            throw new BusinessRuleException("Choose between 1 and 100 passes.", "pass_count_invalid");

        var user = await users.Query()
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new NotFoundException("That account no longer exists.");
        var now = DateTimeOffset.UtcNow;

        if (req.Count > 0)
        {
            for (var i = 0; i < req.Count; i++)
                await credits.AddAsync(new PassCredit { Id = Guid.NewGuid(), OwnerUserId = userId, Kind = kind, Price = 0m, CreatedAt = now }, ct);
        }
        else
        {
            var unused = await credits.Query(tracking: true)
                .Where(c => c.OwnerUserId == userId && c.Kind == kind && c.UsedOnCampaignId == null)
                .OrderByDescending(c => c.CreatedAt)
                .Take(-req.Count)
                .ToListAsync(ct);
            foreach (var c in unused) credits.Remove(c);
        }

        await auditLogs.AddAsync(new AuditLog
        {
            Id = Guid.NewGuid(),
            Action = "admin.pass_credits.adjust",
            Actor = currentUser.UserId?.ToString() ?? "admin",
            DataJson = JsonSerializer.Serialize(new { user = userId, kind = kind.ToString(), count = req.Count }),
            CreatedAt = now,
        }, ct);

        await uow.SaveChangesAsync(ct);
        return Describe(user, await UnusedCreditsAsync(userId, ct));
    }

    public async Task<IReadOnlyList<AdminUserEventDto>> UserEventsAsync(Guid userId, CancellationToken ct = default)
    {
        var rows = await campaigns.Query()
            .Where(c => c.CreatedByUserId == userId)
            .OrderByDescending(c => c.EventStartAt)
            .ToListAsync(ct);
        var list = new List<AdminUserEventDto>();
        foreach (var c in rows) list.Add(await DescribeEventAsync(c, ct));
        return list;
    }

    private async Task<AdminUserEventDto> DescribeEventAsync(Campaign c, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        var plan = await plans.ForCampaignAsync(c.Id, ct);
        return new AdminUserEventDto(
            c.Id, c.Title, c.EventStartAt, c.EventPass.ToString(), c.EventPassUntil,
            c.EventPass != EventPassKind.None && c.EventPassUntil is { } until && until > now,
            c.KeepPhotosUntil,
            plan.Kind.ToString(), plan.CoveredUntil, plan.Phase.ToString(),
            await allowances.ForCampaignAsync(c.Id, ct));
    }

    /// <summary>
    /// Adds emailed invitations to an event on top of what its pass includes (a negative number takes
    /// unused ones back), until they can be bought online.
    /// </summary>
    public async Task<AdminUserEventDto> AddSendingAsync(Guid campaignId, AddSendingRequest req, CancellationToken ct = default)
    {
        if (req.Invitations is 0 or > 10000 or < -10000)
            throw new BusinessRuleException("Choose between 1 and 10,000 invitations.", "sending_count_invalid");
        var campaign = await campaigns.GetByIdAsync(campaignId, ct)
                       ?? throw new NotFoundException("That event no longer exists.");
        campaign.PaidInviteCapacity = Math.Max(0, campaign.PaidInviteCapacity + req.Invitations);
        campaign.UpdatedAt = DateTimeOffset.UtcNow;

        await auditLogs.AddAsync(new AuditLog
        {
            Id = Guid.NewGuid(),
            Action = "admin.sending.add",
            Actor = currentUser.UserId?.ToString() ?? "admin",
            CampaignId = campaign.Id,
            DataJson = JsonSerializer.Serialize(new { added = req.Invitations, extra = campaign.PaidInviteCapacity }),
            CreatedAt = DateTimeOffset.UtcNow,
        }, ct);

        campaigns.Update(campaign);
        await uow.SaveChangesAsync(ct);
        return await DescribeEventAsync(campaign, ct);
    }

    /// <summary>
    /// Gives one event a Party or Wedding pass, or takes it away. A pass runs a year from the event
    /// day, or from today if the event has passed; giving the same pass again adds a year to it, and
    /// moving up from Party to Wedding keeps whichever end is later.
    /// </summary>
    public async Task<AdminUserEventDto> SetEventPassAsync(
        Guid campaignId, SetEventPassRequest req, CancellationToken ct = default)
    {
        if (!Enum.TryParse<EventPassKind>(req.Kind?.Trim(), ignoreCase: true, out var kind) || !Enum.IsDefined(kind))
            throw new BusinessRuleException("Choose None, Party or Wedding.", "pass_kind_unknown");

        var campaign = await campaigns.GetByIdAsync(campaignId, ct)
                       ?? throw new NotFoundException("That event no longer exists.");
        var now = DateTimeOffset.UtcNow;
        EventPasses.Apply(campaign, kind, now);

        await auditLogs.AddAsync(new AuditLog
        {
            Id = Guid.NewGuid(),
            Action = kind == EventPassKind.None ? "admin.event_pass.revoke" : "admin.event_pass.grant",
            Actor = currentUser.UserId?.ToString() ?? "admin",
            CampaignId = campaign.Id,
            DataJson = JsonSerializer.Serialize(new { kind = kind.ToString(), until = campaign.EventPassUntil }),
            CreatedAt = now,
        }, ct);

        campaigns.Update(campaign);
        await uow.SaveChangesAsync(ct);
        if (kind != EventPassKind.None) await buckets.RaiseWindowsToPlanAsync(campaign.Id, ct);
        return await DescribeEventAsync(campaign, ct);
    }

    /// <summary>
    /// "Keep your photos": a year more online for the event's albums, from whenever they would
    /// otherwise have started to lapse. <c>Years</c> 0 takes it away.
    /// </summary>
    public async Task<AdminUserEventDto> KeepPhotosAsync(
        Guid campaignId, KeepPhotosRequest req, CancellationToken ct = default)
    {
        if (req.Years is < 0 or > 10)
            throw new BusinessRuleException("Choose between 0 and 10 years.", "keep_years_invalid");
        var campaign = await campaigns.GetByIdAsync(campaignId, ct)
                       ?? throw new NotFoundException("That event no longer exists.");
        var now = DateTimeOffset.UtcNow;

        if (req.Years == 0) campaign.KeepPhotosUntil = null;
        else
        {
            // A year more from whenever the photos would otherwise start to lapse — every cover counts
            // (a pass, one already kept, a venue that has ended, what older events were promised).
            var cover = (await plans.ForCampaignAsync(campaign.Id, ct)).CoveredUntil ?? now;
            var from = cover > now ? cover : now;
            campaign.KeepPhotosUntil = from.AddMonths(PlanCatalog.KeepPhotosMonths * req.Years);
        }

        await auditLogs.AddAsync(new AuditLog
        {
            Id = Guid.NewGuid(),
            Action = "admin.keep_photos.set",
            Actor = currentUser.UserId?.ToString() ?? "admin",
            CampaignId = campaign.Id,
            DataJson = JsonSerializer.Serialize(new { until = campaign.KeepPhotosUntil }),
            CreatedAt = now,
        }, ct);

        campaigns.Update(campaign);
        await uow.SaveChangesAsync(ct);
        return await DescribeEventAsync(campaign, ct);
    }
}
