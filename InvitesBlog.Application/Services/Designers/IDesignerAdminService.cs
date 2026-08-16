using InvitesBlog.Application.Common;
using InvitesBlog.Application.Dtos.Designers;
using InvitesBlog.Application.Filters.Designers;

namespace InvitesBlog.Application.Services.Designers;

/// <summary>Admin oversight of designers and what they've earned (§Phase 7).</summary>
public interface IDesignerAdminService
{
    Task<PagedResult<DesignerAdminDto>> ListAsync(DesignerFilter filter, CancellationToken ct = default);

    /// <summary>
    /// Suspends or reinstates a designer account. Suspension blocks new submissions and sign-ins;
    /// it deliberately leaves their already-published templates live, so no inviter's campaign breaks.
    /// </summary>
    Task<DesignerAdminDto> SetSuspendedAsync(Guid designerUserId, bool suspended, CancellationToken ct = default);

    /// <summary>
    /// What each designer has earned: commissions agreed for their bespoke work, plus the per-use fees
    /// accrued from campaigns started on their public templates. Reporting only — paying it out is a
    /// separate, deliberate act.
    /// </summary>
    Task<IReadOnlyList<DesignerEarningsDto>> EarningsAsync(CancellationToken ct = default);
}
