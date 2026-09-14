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
/// Admin business logic (full-RBAC). Read-only inspection of users/roles/permissions plus paged
/// suppression + audit review, and password-verified admin login that issues a role-aware JWT.
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
    ICampaignRepository campaigns) : IAdminService
{
    private static readonly TimeSpan AdminSessionLifetime = TimeSpan.FromHours(8);

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

        var total = await query.CountAsync(ct);
        var page = await query
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .OrderBy(u => u.Email)
            .Skip(filter.Skip).Take(filter.PageSize)
            .ToListAsync(ct);

        var items = page.Select(Describe).ToList();

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
    /// <para>There is no billing behind <c>Subscriber</c> yet, so this IS the subscription — a list
    /// kept by hand. It refuses four things, and all four are about not being able to break the
    /// platform from a settings page:</para>
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
        if (req.Granted == (held is not null)) return Describe(user);

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
        return Describe(user);
    }

    private static AdminUserDto Describe(AppUser u) => new(
        u.Id, u.Email, u.DisplayName, u.IsActive,
        u.UserRoles.Select(ur => ur.Role?.Name).OfType<string>().OrderBy(n => n).ToList(),
        u.SubscriptionTier.ToString(),
        u.SubscriptionEndsAt,
        PlanRules.IsActive(u.SubscriptionTier, u.SubscriptionEndsAt, DateTimeOffset.UtcNow));

    /// <summary>
    /// Sets an account's subscription by hand, until billing exists.
    ///
    /// <para>Choosing None ends an active subscription now rather than erasing it, because the end
    /// date is when the account's events stopped being covered and their photo retention counts
    /// from it.</para>
    /// </summary>
    public async Task<AdminUserDto> SetSubscriptionAsync(
        Guid userId, SetSubscriptionRequest req, CancellationToken ct = default)
    {
        if (!Enum.TryParse<SubscriptionTier>(req.Tier?.Trim(), ignoreCase: true, out var tier))
            throw new BusinessRuleException("Choose None, Basic or Premium.", "tier_unknown");

        var now = DateTimeOffset.UtcNow;
        if (tier != SubscriptionTier.None && req.EndsAt is { } requested && requested <= now)
            throw new BusinessRuleException("The end date has to be in the future.", "tier_end_in_past");

        var user = await users.Query(tracking: true)
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Id == userId, ct)
            ?? throw new NotFoundException("That account no longer exists.");

        var wasActive = PlanRules.IsActive(user.SubscriptionTier, user.SubscriptionEndsAt, now);
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
        return Describe(user);
    }

    public async Task<IReadOnlyList<AdminUserEventDto>> UserEventsAsync(Guid userId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var rows = await campaigns.Query()
            .Where(c => c.CreatedByUserId == userId)
            .OrderByDescending(c => c.EventStartAt)
            .Select(c => new { c.Id, c.Title, c.EventStartAt, c.EventPassUntil })
            .ToListAsync(ct);
        return rows.Select(c => new AdminUserEventDto(
            c.Id, c.Title, c.EventStartAt, c.EventPassUntil, c.EventPassUntil is { } until && until > now)).ToList();
    }

    /// <summary>
    /// Grants a pass on one event, or takes it away. A pass runs six months from the event day, or
    /// from today if the event has passed; granting again on an event that still has one adds six
    /// more months to it.
    /// </summary>
    public async Task<AdminUserEventDto> SetEventPassAsync(
        Guid campaignId, SetEventPassRequest req, CancellationToken ct = default)
    {
        var campaign = await campaigns.GetByIdAsync(campaignId, ct)
                       ?? throw new NotFoundException("That event no longer exists.");
        var now = DateTimeOffset.UtcNow;

        if (req.Granted)
        {
            var from = campaign.EventPassUntil is { } until && until > now
                ? until
                : campaign.EventStartAt > now ? campaign.EventStartAt : now;
            campaign.EventPassUntil = from.AddMonths(PlanCatalog.EventPassMonths);
        }
        else
        {
            campaign.EventPassUntil = null;
        }

        await auditLogs.AddAsync(new AuditLog
        {
            Id = Guid.NewGuid(),
            Action = req.Granted ? "admin.event_pass.grant" : "admin.event_pass.revoke",
            Actor = currentUser.UserId?.ToString() ?? "admin",
            CampaignId = campaign.Id,
            DataJson = JsonSerializer.Serialize(new { until = campaign.EventPassUntil }),
            CreatedAt = now,
        }, ct);

        campaigns.Update(campaign);
        await uow.SaveChangesAsync(ct);
        return new AdminUserEventDto(campaign.Id, campaign.Title, campaign.EventStartAt, campaign.EventPassUntil,
            campaign.EventPassUntil is { } end && end > now);
    }
}
