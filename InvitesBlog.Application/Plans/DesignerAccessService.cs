using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Plans;

/// <summary>
/// The template designer comes with Studio. There is no designer sign-up and no "become a creator":
/// an account holds the Designer role exactly while its Studio plan is in force — given by an admin
/// by hand, or bought — and admins always keep it.
///
/// <para>The role stays the thing the rest of the app checks (permissions, guards); this keeps it in
/// step with the plan. Called when a plan is set or bought, and swept regularly so a Studio plan that
/// simply runs out loses the designer too. A token already issued keeps its roles until it is
/// refreshed.</para>
/// </summary>
public interface IDesignerAccessService
{
    /// <summary>Grants or removes the Designer role to match this account's plan. True when it changed.</summary>
    Task<bool> SyncAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Every account that has the role or the plan, brought into line. Returns how many changed.</summary>
    Task<int> SweepAsync(CancellationToken ct = default);
}

public sealed class DesignerAccessService(
    IRepository<AppUser> users,
    IRepository<Role> roles,
    IUnitOfWork uow) : IDesignerAccessService
{
    public async Task<bool> SyncAsync(Guid userId, CancellationToken ct = default)
    {
        // Tracked, so it is the same instance EF gives every user's loaded roles: a second copy of the
        // row makes the save throw the moment any account in the batch already holds the role.
        var designer = await roles.Query(tracking: true).FirstOrDefaultAsync(r => r.Name == Roles.Designer, ct);
        if (designer is null) return false;
        var user = await users.Query(tracking: true)
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return false;
        var changed = Apply(user, designer, DateTimeOffset.UtcNow);
        if (changed) await uow.SaveChangesAsync(ct);
        return changed;
    }

    public async Task<int> SweepAsync(CancellationToken ct = default)
    {
        // Tracked, so it is the same instance EF gives every user's loaded roles: a second copy of the
        // row makes the save throw the moment any account in the batch already holds the role.
        var designer = await roles.Query(tracking: true).FirstOrDefaultAsync(r => r.Name == Roles.Designer, ct);
        if (designer is null) return 0;
        var list = await users.Query(tracking: true)
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .Where(u => u.SubscriptionTier == SubscriptionTier.Studio || u.UserRoles.Any(ur => ur.RoleId == designer.Id))
            .ToListAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var changed = list.Count(u => Apply(u, designer, now));
        if (changed > 0) await uow.SaveChangesAsync(ct);
        return changed;
    }

    /// <summary>Whether this account should design: an active Studio plan, or an admin.</summary>
    public static bool ShouldDesign(AppUser user, DateTimeOffset now) =>
        (user.SubscriptionTier == SubscriptionTier.Studio && PlanRules.IsActive(user.SubscriptionTier, user.SubscriptionEndsAt, now))
        || user.UserRoles.Any(ur => ur.Role?.Name == Roles.Admin);

    private static bool Apply(AppUser user, Role designer, DateTimeOffset now)
    {
        var held = user.UserRoles.FirstOrDefault(ur => ur.RoleId == designer.Id);
        var should = ShouldDesign(user, now);
        if (should && held is null)
        {
            user.UserRoles.Add(new UserRole { UserId = user.Id, RoleId = designer.Id, Role = designer });
            return true;
        }
        if (!should && held is not null)
        {
            user.UserRoles.Remove(held);
            return true;
        }
        return false;
    }
}
