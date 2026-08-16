using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Designers;
using InvitesBlog.Application.Exceptions.Designers;
using InvitesBlog.Application.Services.Designers;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>
/// Designer sign-in mirrors admin sign-in (uniform failures, role-gated) and adds OAuth linking: the
/// provider's subject id is the identity, and a verified email must attach to the EXISTING account
/// rather than silently creating a second one.
/// </summary>
public class DesignerAuthServiceTests
{
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IRepository<AppUser> _users = Substitute.For<IRepository<AppUser>>();
    private readonly IRepository<Role> _roles = Substitute.For<IRepository<Role>>();
    private readonly IRepository<UserExternalLogin> _logins = Substitute.For<IRepository<UserExternalLogin>>();
    private readonly IExternalAuthProvider _google = Substitute.For<IExternalAuthProvider>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IInviteeTokenIssuer _tokens = Substitute.For<IInviteeTokenIssuer>();

    public DesignerAuthServiceTests()
    {
        _google.Provider.Returns("google");
        _google.IsConfigured.Returns(true);
        _logins.Query(Arg.Any<bool>()).Returns(Array.Empty<UserExternalLogin>().AsAsyncQueryable());
        _roles.FirstOrDefaultAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Role, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(new Role { Id = Guid.NewGuid(), Name = Roles.Designer, Description = "" });
        _tokens.IssueForRole(Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<TimeSpan>())
            .Returns("designer-jwt");
    }

    private DesignerAuthService Sut() => new(
        _currentUser, _users, _roles, _logins, [_google], _uow, _tokens,
        TestData.PassingValidator<DesignerRegisterRequest>());

    private static AppUser Designer(
        string email = "designer@test.com", string? password = "correct-horse",
        bool isActive = true, string role = Roles.Designer)
    {
        var roleEntity = new Role { Id = Guid.NewGuid(), Name = role, Description = "" };
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Email = email,
            DisplayName = "Test Designer",
            PasswordHash = password is null ? null : InvitesBlog.Application.Security.PasswordHasher.Hash(password),
            IsActive = isActive
        };
        user.UserRoles.Add(new UserRole { UserId = user.Id, User = user, RoleId = roleEntity.Id, Role = roleEntity });
        return user;
    }

    [Fact]
    public async Task Login_wrong_password_throws()
    {
        _users.Query(Arg.Any<bool>()).Returns(new[] { Designer() }.AsAsyncQueryable());

        await Assert.ThrowsAsync<DesignerLoginFailedException>(
            () => Sut().LoginAsync(new DesignerLoginRequest("designer@test.com", "wrong")));
    }

    [Fact]
    public async Task Login_unknown_account_throws_the_same_failure_as_a_wrong_password()
    {
        _users.Query(Arg.Any<bool>()).Returns(Array.Empty<AppUser>().AsAsyncQueryable());

        await Assert.ThrowsAsync<DesignerLoginFailedException>(
            () => Sut().LoginAsync(new DesignerLoginRequest("nobody@test.com", "any")));
    }

    [Fact]
    public async Task Login_suspended_account_is_told_it_is_suspended()
    {
        _users.Query(Arg.Any<bool>()).Returns(new[] { Designer(isActive: false) }.AsAsyncQueryable());

        await Assert.ThrowsAsync<DesignerSuspendedException>(
            () => Sut().LoginAsync(new DesignerLoginRequest("designer@test.com", "correct-horse")));
    }

    [Fact]
    public async Task Login_without_the_designer_role_throws()
    {
        _users.Query(Arg.Any<bool>()).Returns(new[] { Designer(role: "Inviter") }.AsAsyncQueryable());

        await Assert.ThrowsAsync<DesignerLoginFailedException>(
            () => Sut().LoginAsync(new DesignerLoginRequest("designer@test.com", "correct-horse")));
    }

    [Fact]
    public async Task Login_success_issues_a_designer_jwt()
    {
        _users.Query(Arg.Any<bool>()).Returns(new[] { Designer() }.AsAsyncQueryable());

        var res = await Sut().LoginAsync(new DesignerLoginRequest("Designer@Test.com", "correct-horse"));

        Assert.Equal("designer-jwt", res.Token);
        Assert.Equal("designer@test.com", res.Designer.Email);
        _tokens.Received().IssueForRole(Roles.Designer, Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<TimeSpan>());
    }

    [Fact]
    public async Task An_admin_signing_in_here_keeps_their_admin_role()
    {
        _users.Query(Arg.Any<bool>()).Returns(new[] { Designer(role: Roles.Admin) }.AsAsyncQueryable());

        await Sut().LoginAsync(new DesignerLoginRequest("designer@test.com", "correct-horse"));

        _tokens.Received().IssueForRole(Roles.Admin, Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<TimeSpan>());
    }

    [Fact]
    public async Task Register_rejects_an_email_that_already_has_an_account()
    {
        _users.AnyAsync(Arg.Any<System.Linq.Expressions.Expression<Func<AppUser, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(true);

        await Assert.ThrowsAsync<DesignerEmailTakenException>(
            () => Sut().RegisterAsync(new DesignerRegisterRequest("taken@test.com", "a-long-password", "Taken")));
    }

    [Fact]
    public async Task Register_lowercases_the_email_and_assigns_the_designer_role()
    {
        AppUser? added = null;
        await _users.AddAsync(Arg.Do<AppUser>(u => added = u), Arg.Any<CancellationToken>());

        var res = await Sut().RegisterAsync(new DesignerRegisterRequest("  New@Test.com ", "a-long-password", "New"));

        Assert.Equal("new@test.com", added!.Email);
        Assert.Single(added.UserRoles);
        Assert.Equal("new@test.com", res.Designer.Email);
    }

    [Fact]
    public async Task OAuth_links_a_verified_email_to_the_existing_account_instead_of_creating_a_second_one()
    {
        var existing = Designer(email: "designer@test.com");
        _users.Query(Arg.Any<bool>()).Returns(new[] { existing }.AsAsyncQueryable());
        _google.VerifyAsync("id-token", Arg.Any<CancellationToken>())
            .Returns(new ExternalIdentity("google", "google-sub-1", "designer@test.com", "Test Designer"));

        UserExternalLogin? link = null;
        await _logins.AddAsync(Arg.Do<UserExternalLogin>(l => link = l), Arg.Any<CancellationToken>());

        var res = await Sut().OAuthAsync("google", new DesignerOAuthRequest("id-token"));

        Assert.Equal(existing.Id, res.Designer.Id);
        Assert.Equal(existing.Id, link!.UserId);
        Assert.Equal("google-sub-1", link.ExternalSubjectId);
        // No new account was created.
        await _users.DidNotReceive().AddAsync(Arg.Any<AppUser>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OAuth_with_an_unknown_provider_throws()
    {
        await Assert.ThrowsAsync<InvitesBlog.Application.Exceptions.NotFoundException>(
            () => Sut().OAuthAsync("facebook", new DesignerOAuthRequest("id-token")));
    }

    [Fact]
    public async Task Configured_providers_lists_only_the_ones_with_credentials()
    {
        var unconfigured = Substitute.For<IExternalAuthProvider>();
        unconfigured.Provider.Returns("microsoft");
        unconfigured.IsConfigured.Returns(false);
        _google.Descriptor().Returns(new ExternalAuthDescriptor("google", "client-id", "https://auth"));

        var sut = new DesignerAuthService(
            _currentUser, _users, _roles, _logins, [_google, unconfigured], _uow, _tokens,
            TestData.PassingValidator<DesignerRegisterRequest>());

        Assert.Equal(new[] { "google" }, sut.ConfiguredProviders().Select(d => d.Provider));
        // An unconfigured provider is never asked for a descriptor — it would throw.
        unconfigured.DidNotReceive().Descriptor();
    }
}
