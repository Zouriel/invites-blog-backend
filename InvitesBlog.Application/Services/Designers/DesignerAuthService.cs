using System.Security.Claims;
using FluentValidation;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Designers;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Exceptions.Designers;
using InvitesBlog.Application.Security;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Services.Designers;

/// <summary>
/// Designer sign-up / sign-in. Deliberately mirrors <c>AdminService.LoginAsync</c>: same
/// <see cref="PasswordHasher"/>, same <see cref="IInviteeTokenIssuer.IssueForRole"/> JWT, same generic
/// RBAC tables — the only new thing is the <see cref="Roles.Designer"/> role and OAuth linking.
/// </summary>
public sealed class DesignerAuthService(
    ICurrentUser currentUser,
    IRepository<AppUser> users,
    IRepository<Role> roles,
    IRepository<UserExternalLogin> externalLogins,
    IEnumerable<IExternalAuthProvider> authProviders,
    IUnitOfWork uow,
    IInviteeTokenIssuer tokenIssuer,
    IValidator<DesignerRegisterRequest> registerValidator) : IDesignerAuthService
{
    private static readonly TimeSpan SessionLifetime = TimeSpan.FromDays(14);

    public async Task<DesignerAuthResultDto> RegisterAsync(DesignerRegisterRequest request, CancellationToken ct = default)
    {
        await registerValidator.ValidateAndThrowAsync(request, ct);

        var email = Normalize(request.Email);
        if (await users.AnyAsync(u => u.Email == email, ct))
            throw new DesignerEmailTakenException();

        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Email = email,
            DisplayName = string.IsNullOrWhiteSpace(request.DisplayName) ? email.Split('@')[0] : request.DisplayName.Trim(),
            PasswordHash = PasswordHasher.Hash(request.Password),
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await AssignDesignerRoleAsync(user, ct);
        await users.AddAsync(user, ct);
        await uow.SaveChangesAsync(ct);

        return Issue(user, [], [Roles.Designer]);
    }

    public async Task<DesignerAuthResultDto> LoginAsync(DesignerLoginRequest request, CancellationToken ct = default)
    {
        var email = Normalize(request.Email);
        var user = await LoadAsync(u => u.Email == email, ct);

        // Uniform failure: never reveal whether the account exists.
        if (user is null ||
            string.IsNullOrEmpty(user.PasswordHash) ||
            !PasswordHasher.Verify(request.Password ?? string.Empty, user.PasswordHash))
            throw new DesignerLoginFailedException();

        return await AuthorizeAndIssueAsync(user, ct);
    }

    public async Task<DesignerAuthResultDto> OAuthAsync(
        string provider, DesignerOAuthRequest request, CancellationToken ct = default)
    {
        var impl = authProviders.FirstOrDefault(p =>
                       string.Equals(p.Provider, provider, StringComparison.OrdinalIgnoreCase))
                   ?? throw new NotFoundException($"Unknown sign-in provider '{provider}'.", "oauth_unknown_provider");

        var identity = await impl.VerifyAsync(request.IdToken, ct);

        // The provider's subject id is the linking key — an existing link wins outright.
        var link = await externalLogins.Query(tracking: true)
            .FirstOrDefaultAsync(l => l.Provider == identity.Provider && l.ExternalSubjectId == identity.SubjectId, ct);

        AppUser user;
        if (link is not null)
        {
            user = await LoadAsync(u => u.Id == link.UserId, ct)
                   ?? throw new DesignerLoginFailedException();
        }
        else
        {
            // No link yet: attach to the account owning this VERIFIED email, else create one. This is
            // what stops a designer who signed up with a password from getting a second account.
            user = await LoadAsync(u => u.Email == identity.Email, ct) ?? await CreateFromExternalAsync(identity, ct);

            await externalLogins.AddAsync(new UserExternalLogin
            {
                Id = Guid.NewGuid(),
                UserId = user.Id,
                Provider = identity.Provider,
                ExternalSubjectId = identity.SubjectId,
                Email = identity.Email,
                CreatedAt = DateTimeOffset.UtcNow
            }, ct);
            await uow.SaveChangesAsync(ct);
        }

        return await AuthorizeAndIssueAsync(user, ct);
    }

    public async Task<DesignerDto> MeAsync(CancellationToken ct = default)
    {
        var id = currentUser.UserId ?? throw new UnauthorizedException();
        var user = await LoadAsync(u => u.Id == id, ct) ?? throw new UnauthorizedException();
        return ToDto(user, await LinkedProvidersAsync(user.Id, ct));
    }

    public IReadOnlyList<ExternalAuthDescriptor> ConfiguredProviders() =>
        authProviders.Where(p => p.IsConfigured).Select(p => p.Descriptor())
            .OrderBy(d => d.Provider).ToList();

    /// <summary>
    /// Shared tail of every sign-in path: the account must be active and hold the Designer role
    /// before a token is issued. Admins hold every permission, so they're designers too.
    /// </summary>
    private async Task<DesignerAuthResultDto> AuthorizeAndIssueAsync(AppUser user, CancellationToken ct)
    {
        if (!user.IsActive) throw new DesignerSuspendedException();

        var roleNames = user.UserRoles.Select(ur => ur.Role.Name).ToList();
        if (!roleNames.Contains(Roles.Designer) && !roleNames.Contains(Roles.Admin))
            throw new DesignerLoginFailedException();

        return Issue(user, await LinkedProvidersAsync(user.Id, ct), roleNames);
    }

    private async Task<AppUser> CreateFromExternalAsync(ExternalIdentity identity, CancellationToken ct)
    {
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Email = identity.Email,
            DisplayName = identity.DisplayName,
            PasswordHash = null,          // OAuth-only account until they set a password
            IsActive = true,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await AssignDesignerRoleAsync(user, ct);
        await users.AddAsync(user, ct);
        await uow.SaveChangesAsync(ct);

        // Re-read so UserRoles.Role is populated for the role check that follows.
        return await LoadAsync(u => u.Id == user.Id, ct)!
               ?? throw new UnauthorizedException();
    }

    private async Task AssignDesignerRoleAsync(AppUser user, CancellationToken ct)
    {
        var role = await roles.FirstOrDefaultAsync(r => r.Name == Roles.Designer, ct)
                   ?? throw new BusinessRuleException(
                       "The Designer role hasn't been seeded on this server yet.", "designer_role_missing");
        user.UserRoles.Add(new UserRole { RoleId = role.Id });
    }

    private Task<AppUser?> LoadAsync(
        System.Linq.Expressions.Expression<Func<AppUser, bool>> predicate, CancellationToken ct) =>
        users.Query(tracking: true)
            .Include(u => u.UserRoles).ThenInclude(ur => ur.Role)
            .FirstOrDefaultAsync(predicate, ct);

    private async Task<IReadOnlyList<string>> LinkedProvidersAsync(Guid userId, CancellationToken ct) =>
        await externalLogins.Query().Where(l => l.UserId == userId)
            .Select(l => l.Provider).OrderBy(p => p).ToListAsync(ct);

    private DesignerAuthResultDto Issue(AppUser user, IReadOnlyList<string> providers, IReadOnlyList<string> roleNames)
    {
        var claims = new Dictionary<string, string>
        {
            [ClaimTypes.NameIdentifier] = user.Id.ToString(),
            ["email"] = user.Email
        };
        // Admins sign in here too; issuing under their own role keeps their wider permission set.
        var role = roleNames.Contains(Roles.Admin) ? Roles.Admin : Roles.Designer;
        var token = tokenIssuer.IssueForRole(role, claims, SessionLifetime);

        return new DesignerAuthResultDto(token, DateTimeOffset.UtcNow.Add(SessionLifetime), ToDto(user, providers));
    }

    private static DesignerDto ToDto(AppUser user, IReadOnlyList<string> providers) =>
        new(user.Id, user.Email, user.DisplayName, user.IsActive, providers);

    private static string Normalize(string? email) => (email ?? string.Empty).Trim().ToLowerInvariant();
}
