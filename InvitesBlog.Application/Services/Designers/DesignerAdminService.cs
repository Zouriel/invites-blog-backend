using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Common;
using InvitesBlog.Application.Dtos.Designers;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Filters.Designers;
using InvitesBlog.Application.Plans;
using InvitesBlog.Domain.Enums;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Services.Designers;

/// <summary>Admin oversight of designer accounts: who they are, what they've published, suspension.</summary>
public sealed class DesignerAdminService(
    IRepository<AppUser> users,
    IRepository<UserExternalLogin> externalLogins,
    ITemplateRepository templates,
    IRepository<PassCredit> credits,
    IUnitOfWork uow) : IDesignerAdminService
{
    public async Task<PagedResult<DesignerAdminDto>> ListAsync(
        DesignerFilter filter, CancellationToken ct = default)
    {
        var query = users.Query().Where(u => u.UserRoles.Any(ur => ur.Role.Name == Roles.Designer));

        if (filter.IsActive is { } active) query = query.Where(u => u.IsActive == active);
        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.ToLower();
            query = query.Where(u =>
                (u.Email != null && u.Email.ToLower().Contains(term)) ||
                u.DisplayName.ToLower().Contains(term));
        }

        var total = await query.CountAsync(ct);
        var page = await query.OrderBy(u => u.Email).Skip(filter.Skip).Take(filter.PageSize).ToListAsync(ct);
        var ids = page.Select(u => u.Id).ToList();

        var published = await templates.Query()
            .Where(t => t.DesignerUserId != null && ids.Contains(t.DesignerUserId!.Value) && t.IsActive)
            .GroupBy(t => t.DesignerUserId!.Value)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, ct);

        var providers = (await externalLogins.Query().Where(l => ids.Contains(l.UserId)).ToListAsync(ct))
            .GroupBy(l => l.UserId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(l => l.Provider).OrderBy(p => p).ToList());

        // Their clients: templates published for one person.
        var forClients = await templates.Query()
            .Where(t => t.DesignerUserId != null && ids.Contains(t.DesignerUserId!.Value) && t.AssignedEmail != null)
            .GroupBy(t => t.DesignerUserId!.Value)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, ct);
        var held = (await credits.Query()
                .Where(c => ids.Contains(c.OwnerUserId) && c.UsedOnCampaignId == null)
                .Select(c => new { c.OwnerUserId, c.Kind })
                .ToListAsync(ct))
            .GroupBy(c => c.OwnerUserId)
            .ToDictionary(g => g.Key, g => (Party: g.Count(c => c.Kind == EventPassKind.Party), Wedding: g.Count(c => c.Kind == EventPassKind.Wedding)));
        var now = DateTimeOffset.UtcNow;

        var items = page.Select(u => new DesignerAdminDto(
            u.Id, u.Email, u.DisplayName, u.IsActive,
            providers.GetValueOrDefault(u.Id, []),
            published.GetValueOrDefault(u.Id),
            u.CreatedAt,
            u.SubscriptionTier == SubscriptionTier.Studio && PlanRules.IsActive(u.SubscriptionTier, u.SubscriptionEndsAt, now),
            u.SubscriptionTier == SubscriptionTier.Studio ? u.SubscriptionEndsAt : null,
            held.GetValueOrDefault(u.Id).Party + held.GetValueOrDefault(u.Id).Wedding,
            forClients.GetValueOrDefault(u.Id),
            held.GetValueOrDefault(u.Id).Party,
            held.GetValueOrDefault(u.Id).Wedding)).ToList();

        return PagedResult<DesignerAdminDto>.Create(items, total, filter);
    }

    public async Task<DesignerAdminDto> SetSuspendedAsync(
        Guid designerUserId, bool suspended, CancellationToken ct = default)
    {
        var user = await users.Query(tracking: true)
                       .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
                       .FirstOrDefaultAsync(u => u.Id == designerUserId, ct)
                   ?? throw new NotFoundException("That designer doesn't exist.", "designer_not_found");

        if (!user.UserRoles.Any(ur => ur.Role.Name == Roles.Designer))
            throw new BusinessRuleException("That account isn't a designer.", "not_a_designer");

        user.IsActive = !suspended;
        users.Update(user);
        await uow.SaveChangesAsync(ct);

        // Re-read through the list path so the counts come back consistent with the list screen.
        var page = await ListAsync(new DesignerFilter { Search = user.Email, PageSize = 1 }, ct);
        return page.Items.FirstOrDefault()
               ?? new DesignerAdminDto(user.Id, user.Email, user.DisplayName, user.IsActive, [], 0, user.CreatedAt);
    }
}
