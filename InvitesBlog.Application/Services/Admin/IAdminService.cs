using InvitesBlog.Application.Common;
using InvitesBlog.Application.Dtos.Admin;
using InvitesBlog.Application.Filters.Admin;

namespace InvitesBlog.Application.Services.Admin;

/// <summary>Admin surface: user/role/permission inspection and role grants, suppression + audit review.</summary>
public interface IAdminService
{
    Task<PagedResult<AdminUserDto>> ListUsersAsync(AdminUserFilter filter, CancellationToken ct = default);
    Task<IReadOnlyList<AdminRoleDto>> ListRolesAsync(CancellationToken ct = default);
    Task<IReadOnlyList<AdminPermissionDto>> ListPermissionsAsync(CancellationToken ct = default);
    Task<PagedResult<SuppressionEntryDto>> ListSuppressionAsync(SuppressionFilter filter, CancellationToken ct = default);
    Task<PagedResult<AuditLogDto>> ListAuditAsync(AuditLogFilter filter, CancellationToken ct = default);

    /// <summary>
    /// Gives one account a role, or takes it away, and returns the account as it now stands.
    ///
    /// <para>The only WRITE on this surface. Everything else here inspects; this is what makes a
    /// designer a designer, so it is also the one call that has to refuse things — see the
    /// implementation for which, and why each refusal exists.</para>
    /// </summary>
    Task<AdminUserDto> SetUserRoleAsync(
        Guid userId, SetUserRoleRequest req, CancellationToken ct = default);

    /// <summary>Sets an account's professional plan (None, Studio or Venue) and optional end date.</summary>
    Task<AdminUserDto> SetSubscriptionAsync(Guid userId, SetSubscriptionRequest req, CancellationToken ct = default);

    /// <summary>The events an account organised, with their passes.</summary>
    Task<IReadOnlyList<AdminUserEventDto>> UserEventsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>Gives an event a Party or Wedding pass, or takes it away.</summary>
    Task<AdminUserEventDto> SetEventPassAsync(Guid campaignId, SetEventPassRequest req, CancellationToken ct = default);

    /// <summary>"Keep your photos": adds years to how long an event's albums stay online, or takes them away.</summary>
    Task<AdminUserEventDto> KeepPhotosAsync(Guid campaignId, KeepPhotosRequest req, CancellationToken ct = default);

    /// <summary>Adds emailed invitations to an event on top of what its pass includes.</summary>
    Task<AdminUserEventDto> AddSendingAsync(Guid campaignId, AddSendingRequest req, CancellationToken ct = default);

    /// <summary>Adds passes to a Studio account's stock, or takes unused ones away.</summary>
    Task<AdminUserDto> AdjustPassCreditsAsync(Guid userId, AdjustPassCreditsRequest req, CancellationToken ct = default);
}
