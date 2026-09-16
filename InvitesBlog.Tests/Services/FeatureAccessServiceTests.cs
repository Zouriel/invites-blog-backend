using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Services.Features;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Infrastructure.Persistence;
using InvitesBlog.Infrastructure.Repositories;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>
/// Test features are reachable only by the emails an admin lists (and by admins), until the feature is
/// released for everyone.
/// </summary>
public class FeatureAccessServiceTests
{
    private readonly AppDbContext _db = new(new DbContextOptionsBuilder<AppDbContext>()
        .UseInMemoryDatabase($"features-{Guid.NewGuid()}").Options);
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();

    private FeatureAccessService Access() => new(
        _currentUser, new BaseRepository<AppUser>(_db), new BaseRepository<FeatureTester>(_db), new BaseRepository<FeatureRelease>(_db));

    private TesterAdminService Admin() => new(
        _currentUser, new BaseRepository<AppUser>(_db), new BaseRepository<FeatureTester>(_db),
        new BaseRepository<FeatureRelease>(_db), new UnitOfWork(_db));

    private async Task<Guid> SignInAsync(string email)
    {
        var id = Guid.NewGuid();
        _db.Users.Add(new AppUser { Id = id, Email = email, DisplayName = email, IsActive = true });
        await _db.SaveChangesAsync();
        _currentUser.UserId.Returns(id);
        return id;
    }

    [Fact]
    public async Task A_signed_in_customer_who_isnt_listed_doesnt_get_the_designer()
    {
        await SignInAsync("someone@example.com");

        Assert.False(await Access().HasAsync(Features.TemplateDesigner));
    }

    [Fact]
    public async Task A_listed_email_gets_the_designer_whatever_case_the_admin_typed()
    {
        await Admin().AddAsync(new("  Tester@Example.COM ", [Features.TemplateDesigner], null));
        await SignInAsync("tester@example.com");

        Assert.True(await Access().HasAsync(Features.TemplateDesigner));
    }

    [Fact]
    public async Task Admins_always_have_every_feature()
    {
        await SignInAsync("admin@example.com");
        _currentUser.HasPermission(Permissions.Admin.Access).Returns(true);

        Assert.True(await Access().HasAsync(Features.TemplateDesigner));
    }

    [Fact]
    public async Task Removing_a_tester_takes_the_feature_away()
    {
        var added = await Admin().AddAsync(new("tester@example.com", [Features.TemplateDesigner], null));
        await Admin().RemoveAsync(added.Id);
        await SignInAsync("tester@example.com");

        Assert.False(await Access().HasAsync(Features.TemplateDesigner));
    }

    [Fact]
    public async Task A_released_feature_is_for_everyone_even_signed_out()
    {
        await Admin().SetReleasedAsync(Features.TemplateDesigner, true);
        _currentUser.UserId.Returns((Guid?)null);

        Assert.True(await Access().HasAsync(Features.TemplateDesigner));
    }

    [Fact]
    public async Task Adding_an_email_twice_merges_instead_of_duplicating()
    {
        await Admin().AddAsync(new("tester@example.com", [Features.TemplateDesigner], "first"));
        var again = await Admin().AddAsync(new("TESTER@example.com", [Features.TemplateDesigner], "second"));

        Assert.Equal(1, await _db.Set<FeatureTester>().CountAsync());
        Assert.Equal("second", again.Note);
    }

    [Fact]
    public async Task Unknown_features_and_bad_emails_are_refused()
    {
        var noFeature = await Assert.ThrowsAsync<BusinessRuleException>(() => Admin().AddAsync(new("tester@example.com", ["time-travel"], null)));
        Assert.Equal("features_required", noFeature.ErrorCode);
        var badEmail = await Assert.ThrowsAsync<BusinessRuleException>(() => Admin().AddAsync(new("not-an-email", [Features.TemplateDesigner], null)));
        Assert.Equal("email_invalid", badEmail.ErrorCode);
    }

    [Fact]
    public async Task Renaming_a_tester_onto_an_existing_email_is_refused()
    {
        await Admin().AddAsync(new("one@example.com", [Features.TemplateDesigner], null));
        var two = await Admin().AddAsync(new("two@example.com", [Features.TemplateDesigner], null));

        await Assert.ThrowsAsync<AlreadyExistsException>(() => Admin().UpdateAsync(two.Id, new("one@example.com", [Features.TemplateDesigner], null)));
    }
}
