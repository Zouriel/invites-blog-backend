using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Accounts;
using InvitesBlog.Application.Dtos.Otp;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Exceptions.Accounts;
using InvitesBlog.Application.Phones;
using InvitesBlog.Application.Services.Accounts;
using InvitesBlog.Application.Services.Otp;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>
/// One sign-in, many roles. The dangerous part is linking a second identifier: it can absorb another
/// account, so it must be impossible without proving control of that identifier, and impossible to
/// silently overwrite an identifier the account already signs in with.
/// </summary>
public class AccountServiceTests
{
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly IRepository<AppUser> _users = Substitute.For<IRepository<AppUser>>();
    private readonly IRepository<Role> _roles = Substitute.For<IRepository<Role>>();
    private readonly IRepository<UserExternalLogin> _logins = Substitute.For<IRepository<UserExternalLogin>>();
    private readonly IRepository<Inviter> _inviters = Substitute.For<IRepository<Inviter>>();
    private readonly IRepository<Inquiry> _inquiries = Substitute.For<IRepository<Inquiry>>();
    private readonly ICampaignRepository _campaigns = Substitute.For<ICampaignRepository>();
    private readonly IGuestRepository _guests = Substitute.For<IGuestRepository>();
    private readonly ITemplateRepository _templates = Substitute.For<ITemplateRepository>();
    private readonly IOtpService _otp = Substitute.For<IOtpService>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IInviteeTokenIssuer _tokens = Substitute.For<IInviteeTokenIssuer>();
    private readonly IOtpSender _sms = Substitute.For<IOtpSender>();
    private readonly IConfiguration _config = Substitute.For<IConfiguration>();

    private readonly List<IExternalAuthProvider> _authProviders = [];
    private readonly Guid _customerRoleId = Guid.NewGuid();
    private readonly Guid _designerRoleId = Guid.NewGuid();

    public AccountServiceTests()
    {
        _logins.Query(Arg.Any<bool>()).Returns(Array.Empty<UserExternalLogin>().AsAsyncQueryable());
        _templates.Query(Arg.Any<bool>()).Returns(Array.Empty<Template>().AsAsyncQueryable());
        _inviters.Query(Arg.Any<bool>()).Returns(Array.Empty<Inviter>().AsAsyncQueryable());
        _inquiries.Query(Arg.Any<bool>()).Returns(Array.Empty<Inquiry>().AsAsyncQueryable());
        _campaigns.Query(Arg.Any<bool>()).Returns(Array.Empty<Campaign>().AsAsyncQueryable());
        _guests.Query(Arg.Any<bool>()).Returns(Array.Empty<Guest>().AsAsyncQueryable());
        // The service looks a role up by name; answer with whichever one the predicate accepts.
        _roles.FirstOrDefaultAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Role, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var predicate = ci.ArgAt<System.Linq.Expressions.Expression<Func<Role, bool>>>(0).Compile();
                var known = new[]
                {
                    new Role { Id = _customerRoleId, Name = Roles.Customer, Description = "" },
                    new Role { Id = _designerRoleId, Name = Roles.Designer, Description = "" },
                };
                return known.FirstOrDefault(predicate);
            });
        _tokens.IssueForRoles(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<TimeSpan>())
            .Returns("session-jwt");
        _sms.Channel.Returns("sms");
        _config["Sms:MsgOwl:ApiKey"].Returns("configured");
    }

    private readonly IRepository<EventPhoto> _photos = Substitute.For<IRepository<EventPhoto>>();

    private AccountService Sut()
    {
        // Sent counts each campaign's photos; without a queryable there is nothing to count.
        _photos.Query().Returns(Array.Empty<EventPhoto>().AsAsyncQueryable());
        return new(
            _currentUser, _users, _roles, _logins, _inviters, _inquiries, _campaigns, _guests,
            _templates, _photos, _authProviders, TestData.PassingValidator<RegisterDesignerRequest>(),
            [_sms], _otp, _uow, _tokens, new PhoneNormalizer(), _config);
    }

    private static AppUser User(
        string? email = null, string? phone = null, string? password = null,
        bool active = true, params string[] roleNames)
    {
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Email = email,
            PhoneE164 = phone,
            DisplayName = "Test",
            PasswordHash = password is null ? null : InvitesBlog.Application.Security.PasswordHasher.Hash(password),
            IsActive = active,
        };
        foreach (var name in roleNames)
        {
            var role = new Role { Id = Guid.NewGuid(), Name = name, Description = "" };
            user.UserRoles.Add(new UserRole { UserId = user.Id, User = user, RoleId = role.Id, Role = role });
        }
        return user;
    }

    private void Existing(params AppUser[] users) =>
        _users.Query(Arg.Any<bool>()).Returns(users.AsAsyncQueryable());

    /// <summary>
    /// Like <see cref="Existing"/>, but resolves role navigations on each read the way the database
    /// does. A newly granted role is stored as an id only (attaching the entity would make EF try to
    /// INSERT the role), and the service re-reads the account to pick the name back up — so without
    /// this, a role added mid-test would be invisible to anything that reads role NAMES.
    /// </summary>
    private void ExistingWithRoleLookup(AppUser user) =>
        _users.Query(Arg.Any<bool>()).Returns(_ =>
        {
            foreach (var ur in user.UserRoles.Where(ur => ur.Role is null))
            {
                var name = ur.RoleId == _designerRoleId ? Roles.Designer
                    : ur.RoleId == _customerRoleId ? Roles.Customer
                    : "Unknown";
                ur.Role = new Role { Id = ur.RoleId, Name = name, Description = "" };
            }
            return new[] { user }.AsAsyncQueryable();
        });

    // ----- Sign in -------------------------------------------------------------------------------

    [Fact]
    public async Task A_token_carries_every_role_the_account_holds()
    {
        // One person is often several things at once; a single-role token would make them choose.
        Existing(User("admin@test.com", password: "correct-horse", roleNames: [Roles.Admin, Roles.Designer, Roles.Customer]));

        var result = await Sut().LoginWithPasswordAsync(new PasswordLoginRequest("admin@test.com", "correct-horse"));

        Assert.Equal(
            new[] { Roles.Admin, Roles.Customer, Roles.Designer },
            result.Account.Roles.Order().ToArray());
        _tokens.Received().IssueForRoles(
            Arg.Is<IReadOnlyCollection<string>>(r => r.Contains(Roles.Admin) && r.Contains(Roles.Designer)),
            Arg.Any<IReadOnlyDictionary<string, string>>(), Arg.Any<TimeSpan>());
    }

    [Fact]
    public async Task A_wrong_password_and_an_unknown_account_fail_identically()
    {
        Existing(User("someone@test.com", password: "correct-horse", roleNames: Roles.Customer));
        var wrong = await Record.ExceptionAsync(
            () => Sut().LoginWithPasswordAsync(new PasswordLoginRequest("someone@test.com", "nope")));

        Existing();
        var unknown = await Record.ExceptionAsync(
            () => Sut().LoginWithPasswordAsync(new PasswordLoginRequest("nobody@test.com", "nope")));

        Assert.IsType<SignInFailedException>(wrong);
        Assert.IsType<SignInFailedException>(unknown);
        Assert.Equal(((SignInFailedException)wrong!).Message, ((SignInFailedException)unknown!).Message);
    }

    [Fact]
    public async Task A_suspended_account_cannot_sign_in()
    {
        Existing(User("x@test.com", password: "correct-horse", active: false, roleNames: Roles.Customer));

        await Assert.ThrowsAsync<AccountSuspendedException>(
            () => Sut().LoginWithPasswordAsync(new PasswordLoginRequest("x@test.com", "correct-horse")));
    }

    [Fact]
    public async Task Verifying_a_phone_code_creates_a_customer_account_on_first_use()
    {
        Existing();
        _otp.VerifyContactAsync(Arg.Any<VerifyOtpRequest>(), Arg.Any<CancellationToken>())
            .Returns(new VerifiedContact("phone", "+9607771234"));
        AppUser? created = null;
        await _users.AddAsync(Arg.Do<AppUser>(u => created = u), Arg.Any<CancellationToken>());

        await Sut().VerifyCodeAsync(new VerifyCodeRequest(Guid.NewGuid(), "123456"));

        Assert.Equal("+9607771234", created!.PhoneE164);
        Assert.Null(created.Email);
        Assert.Equal(_customerRoleId, Assert.Single(created.UserRoles).RoleId);
    }

    [Fact]
    public async Task A_new_account_references_its_role_by_id_only()
    {
        // The role lookup is no-tracking, so attaching the instance as a navigation makes EF treat
        // the role as a NEW row and try to insert it — which fails on the primary key for every
        // first-time sign-in. Referencing it by id keeps the role a lookup, not a write.
        Existing();
        _otp.VerifyContactAsync(Arg.Any<VerifyOtpRequest>(), Arg.Any<CancellationToken>())
            .Returns(new VerifiedContact("phone", "+9607771234"));
        AppUser? created = null;
        await _users.AddAsync(Arg.Do<AppUser>(u => created = u), Arg.Any<CancellationToken>());

        await Sut().VerifyCodeAsync(new VerifyCodeRequest(Guid.NewGuid(), "123456"));

        var link = Assert.Single(created!.UserRoles);
        Assert.Equal(_customerRoleId, link.RoleId);
        Assert.Null(link.Role);
    }

    // ----- Linking + merging ---------------------------------------------------------------------

    [Fact]
    public async Task Linking_a_phone_that_has_its_own_account_merges_the_two()
    {
        var designer = User("nadia@test.com", password: "pw", roleNames: Roles.Designer);
        var customer = User(phone: "+9607771234", roleNames: Roles.Customer);
        Existing(designer, customer);
        _currentUser.UserId.Returns(designer.Id);
        _otp.VerifyContactAsync(Arg.Any<VerifyOtpRequest>(), Arg.Any<CancellationToken>())
            .Returns(new VerifiedContact("phone", "+9607771234"));

        var result = await Sut().VerifyLinkAsync(new VerifyCodeRequest(Guid.NewGuid(), "123456"));

        Assert.True(result.Merged);
        Assert.False(string.IsNullOrWhiteSpace(result.Token));     // re-issued: the merge granted a role
        Assert.Equal(designer.Id, result.Account.Id);              // the account they're signed into survives
        Assert.Equal("+9607771234", result.Account.PhoneE164);     // reachable by either identifier now
        Assert.Equal("nadia@test.com", result.Account.Email);
        _users.Received().Remove(customer);                        // the absorbed account is gone
    }

    [Fact]
    public async Task Linking_never_happens_without_a_verified_code()
    {
        // The whole safety of the merge rests on this: the identifier must be PROVEN, so the OTP
        // service is the only way in and its failure must propagate rather than be swallowed.
        var me = User("nadia@test.com", roleNames: Roles.Designer);
        Existing(me);
        _currentUser.UserId.Returns(me.Id);
        _otp.VerifyContactAsync(Arg.Any<VerifyOtpRequest>(), Arg.Any<CancellationToken>())
            .Returns<VerifiedContact>(_ => throw new BusinessRuleException("bad code", "otp_invalid_code"));

        await Assert.ThrowsAsync<BusinessRuleException>(
            () => Sut().VerifyLinkAsync(new VerifyCodeRequest(Guid.NewGuid(), "000000")));

        _users.DidNotReceive().Remove(Arg.Any<AppUser>());
    }

    [Fact]
    public async Task Linking_a_second_email_over_an_existing_one_is_refused()
    {
        // An account holds one email; silently replacing it would change how they sign in and could
        // strand whichever account owned the other address.
        var me = User("first@test.com", password: "pw", roleNames: Roles.Designer);
        Existing(me);
        _currentUser.UserId.Returns(me.Id);
        _otp.VerifyContactAsync(Arg.Any<VerifyOtpRequest>(), Arg.Any<CancellationToken>())
            .Returns(new VerifiedContact("email", "second@test.com"));

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Sut().VerifyLinkAsync(new VerifyCodeRequest(Guid.NewGuid(), "123456")));

        Assert.Equal("identifier_already_set", ex.ErrorCode);
        Assert.Equal("first@test.com", me.Email);
    }

    [Fact]
    public async Task Relinking_an_identifier_the_account_already_holds_is_a_no_op()
    {
        var me = User("nadia@test.com", phone: "+9607771234", roleNames: Roles.Designer);
        Existing(me);
        _currentUser.UserId.Returns(me.Id);
        _otp.VerifyContactAsync(Arg.Any<VerifyOtpRequest>(), Arg.Any<CancellationToken>())
            .Returns(new VerifiedContact("phone", "+9607771234"));

        var result = await Sut().VerifyLinkAsync(new VerifyCodeRequest(Guid.NewGuid(), "123456"));

        Assert.False(result.Merged);
        _users.DidNotReceive().Remove(Arg.Any<AppUser>());
    }

    // ----- What the sign-in page can offer --------------------------------------------------------

    [Fact]
    public void Sms_is_reported_unavailable_when_no_provider_key_is_configured()
    {
        _config["Sms:MsgOwl:ApiKey"].Returns((string?)null);

        Assert.False(Sut().Options().SmsAvailable);
    }

    [Fact]
    public void Sms_is_reported_available_once_the_provider_is_configured() =>
        Assert.True(Sut().Options().SmsAvailable);

    [Fact]
    public async Task Asking_for_a_phone_code_with_no_sms_provider_says_so_instead_of_failing_obscurely()
    {
        _config["Sms:MsgOwl:ApiKey"].Returns((string?)null);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(
            () => Sut().RequestCodeAsync(new RequestCodeRequest("7771234", "MV")));

        Assert.Equal("sms_not_configured", ex.ErrorCode);
    }
    // ----- Designer sign-up ---------------------------------------------------------------------

    [Fact]
    public async Task Registering_creates_a_designer_who_is_also_a_customer()
    {
        _users.Query(Arg.Any<bool>()).Returns(Array.Empty<AppUser>().AsAsyncQueryable());
        AppUser? added = null;
        await _users.AddAsync(Arg.Do<AppUser>(u => added = u), Arg.Any<CancellationToken>());

        await Sut().RegisterDesignerAsync(new RegisterDesignerRequest("  New@Test.com ", "a-long-password", "New"));

        Assert.Equal("new@test.com", added!.Email);
        Assert.Equal(2, added.UserRoles.Count);   // Designer to publish, Customer to receive
        Assert.Contains(added.UserRoles, ur => ur.RoleId == _designerRoleId);
        Assert.Contains(added.UserRoles, ur => ur.RoleId == _customerRoleId);
        // Roles are referenced by id only — attaching the no-tracking instance would try to INSERT it.
        Assert.All(added.UserRoles, ur => Assert.Null(ur.Role));
    }

    // ----- Becoming a creator -------------------------------------------------------------------

    /// <summary>
    /// Signing up a second time with an address you already use cannot work — it's taken, and Google
    /// sign-in just returns the customer you already were. Asking from the account is the way through.
    /// </summary>
    [Fact]
    public async Task A_customer_can_opt_into_publishing_templates()
    {
        var me = User("nadia@test.com", roleNames: Roles.Customer);
        ExistingWithRoleLookup(me);
        _currentUser.UserId.Returns(me.Id);

        var result = await Sut().BecomeDesignerAsync();

        Assert.Contains(me.UserRoles, ur => ur.RoleId == _designerRoleId);
        // Kept, not replaced: they still receive invitations.
        Assert.Contains(me.UserRoles, ur => ur.Role?.Name == Roles.Customer);
        Assert.Equal("session-jwt", result.Token);
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>The role rides in the token, so it means nothing until a new one is issued.</summary>
    [Fact]
    public async Task Becoming_a_creator_reissues_the_token_with_the_new_role()
    {
        var me = User("nadia@test.com", roleNames: Roles.Customer);
        ExistingWithRoleLookup(me);
        _currentUser.UserId.Returns(me.Id);

        await Sut().BecomeDesignerAsync();

        _tokens.Received(1).IssueForRoles(
            Arg.Is<IReadOnlyCollection<string>>(r => r.Contains(Roles.Designer)),
            Arg.Any<IReadOnlyDictionary<string, string>>(),
            Arg.Any<TimeSpan>());
    }

    [Fact]
    public async Task Asking_twice_says_so_rather_than_adding_the_role_again()
    {
        var me = User("nadia@test.com", roleNames: Roles.Designer, password: "pw");
        Existing(me);
        _currentUser.UserId.Returns(me.Id);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Sut().BecomeDesignerAsync());

        Assert.Equal("already_a_designer", ex.ErrorCode);
    }

    [Fact]
    public async Task Registering_never_takes_over_an_account_that_already_has_a_password()
    {
        _users.Query(Arg.Any<bool>()).Returns(new[] { User(email: "taken@test.com", password: "theirs") }.AsAsyncQueryable());

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            Sut().RegisterDesignerAsync(new RegisterDesignerRequest("taken@test.com", "a-long-password", "Me")));
        Assert.Equal("email_taken", ex.ErrorCode);
        await _users.DidNotReceive().AddAsync(Arg.Any<AppUser>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Someone who has only ever received invitations already has a passwordless account. Signing up
    /// to design should upgrade THAT account, not strand their history behind a second one.
    /// </summary>
    [Fact]
    public async Task Registering_upgrades_the_passwordless_account_that_email_already_has()
    {
        var existing = User(email: "customer@test.com", roleNames: Roles.Customer);
        _users.Query(Arg.Any<bool>()).Returns(new[] { existing }.AsAsyncQueryable());

        await Sut().RegisterDesignerAsync(new RegisterDesignerRequest("customer@test.com", "a-long-password", "Me"));

        Assert.NotNull(existing.PasswordHash);
        Assert.Contains(existing.UserRoles, ur => ur.RoleId == _designerRoleId);
        await _users.DidNotReceive().AddAsync(Arg.Any<AppUser>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Registering_refuses_a_suspended_account()
    {
        _users.Query(Arg.Any<bool>()).Returns(new[] { User(email: "gone@test.com", active: false) }.AsAsyncQueryable());

        await Assert.ThrowsAsync<AccountSuspendedException>(() =>
            Sut().RegisterDesignerAsync(new RegisterDesignerRequest("gone@test.com", "a-long-password", "Gone")));
    }

    // ----- OAuth --------------------------------------------------------------------------------

    [Fact]
    public async Task OAuth_links_the_verified_email_to_the_existing_account_rather_than_creating_a_second()
    {
        var existing = User(email: "designer@test.com", roleNames: Roles.Designer);
        _users.Query(Arg.Any<bool>()).Returns(new[] { existing }.AsAsyncQueryable());
        _authProviders.Add(Provider("google", identity:
            new ExternalIdentity("google", "google-sub-1", "designer@test.com", "Test Designer")));

        UserExternalLogin? link = null;
        await _logins.AddAsync(Arg.Do<UserExternalLogin>(l => link = l), Arg.Any<CancellationToken>());

        var result = await Sut().OAuthAsync("google", new OAuthLoginRequest("id-token"));

        Assert.Equal(existing.Id, result.Account.Id);
        Assert.Equal(existing.Id, link!.UserId);
        Assert.Equal("google-sub-1", link.ExternalSubjectId);
        await _users.DidNotReceive().AddAsync(Arg.Any<AppUser>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// The subject id is the linking key, not the email — a provider that let someone change their
    /// address must not hand them a different account.
    /// </summary>
    [Fact]
    public async Task OAuth_follows_an_existing_link_even_when_the_email_has_changed()
    {
        var linked = User(email: "old@test.com", roleNames: Roles.Designer);
        _users.Query(Arg.Any<bool>()).Returns(new[] { linked }.AsAsyncQueryable());
        _logins.Query(Arg.Any<bool>()).Returns(new[]
        {
            new UserExternalLogin
            {
                Id = Guid.NewGuid(), UserId = linked.Id, Provider = "google",
                ExternalSubjectId = "google-sub-1", Email = "old@test.com",
            },
        }.AsAsyncQueryable());
        _authProviders.Add(Provider("google", identity:
            new ExternalIdentity("google", "google-sub-1", "new@test.com", "Renamed")));

        var result = await Sut().OAuthAsync("google", new OAuthLoginRequest("id-token"));

        Assert.Equal(linked.Id, result.Account.Id);
        await _logins.DidNotReceive().AddAsync(Arg.Any<UserExternalLogin>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task OAuth_creates_a_customer_on_first_arrival()
    {
        _users.Query(Arg.Any<bool>()).Returns(Array.Empty<AppUser>().AsAsyncQueryable());
        _authProviders.Add(Provider("google", identity:
            new ExternalIdentity("google", "google-sub-9", "brand@new.com", "Brand New")));
        AppUser? added = null;
        await _users.AddAsync(Arg.Do<AppUser>(u => added = u), Arg.Any<CancellationToken>());

        await Sut().OAuthAsync("google", new OAuthLoginRequest("id-token"));

        Assert.Equal("brand@new.com", added!.Email);
        // Arriving via a provider grants nothing beyond what any first sign-in grants.
        Assert.Equal(_customerRoleId, Assert.Single(added.UserRoles).RoleId);
    }

    [Fact]
    public async Task OAuth_with_an_unknown_provider_throws()
    {
        await Assert.ThrowsAsync<NotFoundException>(() =>
            Sut().OAuthAsync("facebook", new OAuthLoginRequest("id-token")));
    }

    [Fact]
    public async Task OAuth_with_an_unconfigured_provider_says_so_instead_of_failing_obscurely()
    {
        _authProviders.Add(Provider("google", configured: false));

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() =>
            Sut().OAuthAsync("google", new OAuthLoginRequest("id-token")));
        Assert.Equal("oauth_not_configured", ex.ErrorCode);
    }

    [Fact]
    public void Options_offers_only_providers_that_have_credentials()
    {
        _authProviders.Add(Provider("google"));
        _authProviders.Add(Provider("microsoft", configured: false));

        var options = Sut().Options();

        var only = Assert.Single(options.OAuthProviders);
        Assert.Equal("google", only.Provider);
        Assert.Equal("google-client-id", only.ClientId);
    }

    private static IExternalAuthProvider Provider(
        string name, bool configured = true, ExternalIdentity? identity = null)
    {
        var provider = Substitute.For<IExternalAuthProvider>();
        provider.Provider.Returns(name);
        provider.IsConfigured.Returns(configured);
        if (configured)
            provider.Descriptor().Returns(new ExternalAuthDescriptor(name, $"{name}-client-id", $"https://{name}/auth"));
        if (identity is not null)
            provider.VerifyAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(identity);
        return provider;
    }
}
