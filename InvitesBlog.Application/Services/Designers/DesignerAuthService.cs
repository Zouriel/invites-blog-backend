using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Designers;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Services.Designers;

/// <summary>
/// Reads back the signed-in designer, including which providers their account is linked to.
/// Signing up and signing in live in <c>AccountService</c>, which issues one token for every role
/// the account holds.
/// </summary>
public sealed class DesignerAuthService(
    ICurrentUser currentUser,
    IRepository<AppUser> users,
    IRepository<UserExternalLogin> externalLogins) : IDesignerAuthService
{
    public async Task<DesignerDto> MeAsync(CancellationToken ct = default)
    {
        var id = currentUser.UserId ?? throw new UnauthorizedException();
        var user = await LoadAsync(u => u.Id == id, ct) ?? throw new UnauthorizedException();
        return ToDto(user, await LinkedProvidersAsync(user.Id, ct));
    }

    private Task<AppUser?> LoadAsync(
        System.Linq.Expressions.Expression<Func<AppUser, bool>> predicate, CancellationToken ct) =>
        users.Query(tracking: true)
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(predicate, ct);

    private async Task<IReadOnlyList<string>> LinkedProvidersAsync(Guid userId, CancellationToken ct) =>
        await externalLogins.Query().Where(l => l.UserId == userId)
            .Select(l => l.Provider).OrderBy(p => p).ToListAsync(ct);

    private static DesignerDto ToDto(AppUser user, IReadOnlyList<string> providers) =>
        new(user.Id, user.Email, user.DisplayName, user.IsActive, providers);

}
