using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Common;
using InvitesBlog.Application.Dtos.Designers;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Filters.Designers;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Services.Designers;

/// <summary>
/// Admin oversight of designers (§Phase 7). Earnings are DERIVED, never a stored balance: commissions
/// come from the inquiries they were assigned, usage fees from the fee each campaign froze at creation.
/// That means the report can't drift from what was actually charged.
/// </summary>
public sealed class DesignerAdminService(
    IRepository<AppUser> users,
    IRepository<UserExternalLogin> externalLogins,
    IRepository<CustomTemplate> submissions,
    IRepository<Inquiry> inquiries,
    ITemplateRepository templates,
    ICampaignRepository campaigns,
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
            query = query.Where(u => u.Email.ToLower().Contains(term) || u.DisplayName.ToLower().Contains(term));
        }

        var total = await query.CountAsync(ct);
        var page = await query.OrderBy(u => u.Email).Skip(filter.Skip).Take(filter.PageSize).ToListAsync(ct);
        var ids = page.Select(u => u.Id).ToList();

        var published = await templates.Query()
            .Where(t => t.DesignerUserId != null && ids.Contains(t.DesignerUserId!.Value) && t.IsActive)
            .GroupBy(t => t.DesignerUserId!.Value)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, ct);

        var pending = await submissions.Query()
            .Where(s => ids.Contains(s.DesignerUserId)
                        && (s.Status == CustomTemplateStatus.Submitted || s.Status == CustomTemplateStatus.InReview))
            .GroupBy(s => s.DesignerUserId)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, ct);

        var providers = (await externalLogins.Query().Where(l => ids.Contains(l.UserId)).ToListAsync(ct))
            .GroupBy(l => l.UserId)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<string>)g.Select(l => l.Provider).OrderBy(p => p).ToList());

        var items = page.Select(u => new DesignerAdminDto(
            u.Id, u.Email, u.DisplayName, u.IsActive,
            providers.GetValueOrDefault(u.Id, []),
            published.GetValueOrDefault(u.Id),
            pending.GetValueOrDefault(u.Id),
            u.CreatedAt)).ToList();

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
               ?? new DesignerAdminDto(user.Id, user.Email, user.DisplayName, user.IsActive, [], 0, 0, user.CreatedAt);
    }

    public async Task<IReadOnlyList<DesignerEarningsDto>> EarningsAsync(CancellationToken ct = default)
    {
        var designers = await users.Query()
            .Where(u => u.UserRoles.Any(ur => ur.Role.Name == Roles.Designer))
            .OrderBy(u => u.Email)
            .ToListAsync(ct);
        if (designers.Count == 0) return [];

        var ids = designers.Select(u => u.Id).ToList();

        // Commissions: what an admin agreed to pay for bespoke work, once the template was actually issued.
        var commissions = await inquiries.Query()
            .Where(i => i.AssignedDesignerUserId != null
                        && ids.Contains(i.AssignedDesignerUserId!.Value)
                        && i.TemplateIssued
                        && i.CommissionPrice != null)
            .Select(i => new { DesignerId = i.AssignedDesignerUserId!.Value, Price = i.CommissionPrice!.Value })
            .ToListAsync(ct);

        var designerTemplates = await templates.Query()
            .Where(t => t.DesignerUserId != null && ids.Contains(t.DesignerUserId!.Value))
            .Select(t => new { t.Id, t.Name, t.Slug, t.UsagePrice, DesignerId = t.DesignerUserId!.Value })
            .ToListAsync(ct);
        var templateIds = designerTemplates.Select(t => t.Id).ToList();

        // Usage fees come from what each campaign FROZE, not from the template's current price — the
        // report has to match what the inviter was actually charged.
        var charged = await campaigns.Query()
            .Where(c => templateIds.Contains(c.TemplateId) && c.DesignerFee > 0m)
            .GroupBy(c => c.TemplateId)
            .Select(g => new { TemplateId = g.Key, Total = g.Sum(c => c.DesignerFee), Count = g.Count() })
            .ToDictionaryAsync(x => x.TemplateId, ct);

        return designers.Select(u =>
        {
            var mine = designerTemplates.Where(t => t.DesignerId == u.Id).ToList();
            var byTemplate = mine.Select(t =>
            {
                charged.TryGetValue(t.Id, out var c);
                return new DesignerTemplateEarningsDto(
                    t.Id, t.Name, t.Slug, t.UsagePrice, c?.Count ?? 0, c?.Total ?? 0m);
            }).OrderByDescending(t => t.Total).ToList();

            var commissionRows = commissions.Where(c => c.DesignerId == u.Id).ToList();
            var commissionTotal = commissionRows.Sum(c => c.Price);
            var usageTotal = byTemplate.Sum(t => t.Total);

            return new DesignerEarningsDto(
                u.Id, u.Email, u.DisplayName,
                commissionTotal, commissionRows.Count,
                usageTotal, byTemplate.Sum(t => t.Campaigns),
                commissionTotal + usageTotal,
                byTemplate);
        }).ToList();
    }
}
