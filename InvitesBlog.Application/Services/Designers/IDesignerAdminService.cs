using InvitesBlog.Application.Common;
using InvitesBlog.Application.Dtos.Designers;
using InvitesBlog.Application.Filters.Designers;

namespace InvitesBlog.Application.Services.Designers;

/// <summary>Admin oversight of designer accounts.</summary>
public interface IDesignerAdminService
{
    Task<PagedResult<DesignerAdminDto>> ListAsync(DesignerFilter filter, CancellationToken ct = default);

    /// <summary>
    /// Suspends or reinstates a designer account. Suspension blocks sign-ins;
    /// it deliberately leaves their already-published templates live, so no inviter's campaign breaks.
    /// </summary>
    Task<DesignerAdminDto> SetSuspendedAsync(Guid designerUserId, bool suspended, CancellationToken ct = default);
}
