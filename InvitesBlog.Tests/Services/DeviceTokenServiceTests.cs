using System.Reflection;
using FluentValidation;
using InvitesBlog.Api.Authorization;
using InvitesBlog.Api.Controllers;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Accounts;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Services.Accounts;
using InvitesBlog.Application.Validation.Accounts;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Infrastructure.Persistence;
using InvitesBlog.Infrastructure.Repositories;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>
/// Which devices an account gets push on: a token has one row, follows whoever signed in last, and
/// only its owner can remove it.
/// </summary>
public class DeviceTokenServiceTests
{
    private const string Token = "fcm-token-1";

    private readonly string _dbName = $"devices-{Guid.NewGuid()}";
    private readonly AppDbContext _db;
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly Guid _alice = Guid.NewGuid();
    private readonly Guid _bob = Guid.NewGuid();

    public DeviceTokenServiceTests()
    {
        _db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(_dbName).Options);
        _db.Users.Add(new AppUser { Id = _alice, Email = "alice@example.com", DisplayName = "Alice", IsActive = true });
        _db.Users.Add(new AppUser { Id = _bob, Email = "bob@example.com", DisplayName = "Bob", IsActive = true });
        _db.SaveChanges();
        _currentUser.UserId.Returns(_alice);
    }

    private DeviceTokenService Sut(IUnitOfWork? uow = null) => new(
        _currentUser, new BaseRepository<AppUser>(_db), new BaseRepository<DeviceToken>(_db), uow ?? new UnitOfWork(_db),
        new RegisterDeviceRequestValidator(), new RemoveDeviceRequestValidator());

    private async Task<DeviceToken> SavedAsync(string token = Token) =>
        await _db.DeviceTokens.AsNoTracking().SingleAsync(d => d.Token == token);

    [Fact]
    public async Task Registering_saves_the_device_for_the_signed_in_account()
    {
        await Sut().RegisterAsync(new RegisterDeviceRequest(Token, "android"));

        var saved = await SavedAsync();
        Assert.Equal(_alice, saved.UserId);
        Assert.Equal("android", saved.Platform);
        Assert.NotEqual(default, saved.CreatedAt);
        Assert.Equal(saved.CreatedAt, saved.UpdatedAt);
    }

    [Fact]
    public async Task Registering_again_updates_the_one_row()
    {
        await Sut().RegisterAsync(new RegisterDeviceRequest(Token, "android"));
        var first = await SavedAsync();
        await Task.Delay(5);

        await Sut().RegisterAsync(new RegisterDeviceRequest(Token, "ios"));

        var again = await SavedAsync();
        Assert.Equal(first.Id, again.Id);
        Assert.Equal("ios", again.Platform);
        Assert.Equal(first.CreatedAt, again.CreatedAt);
        Assert.True(again.UpdatedAt > first.UpdatedAt);
    }

    [Fact]
    public async Task A_token_registered_by_someone_else_moves_to_them()
    {
        await Sut().RegisterAsync(new RegisterDeviceRequest(Token, "android"));

        _currentUser.UserId.Returns(_bob);
        await Sut().RegisterAsync(new RegisterDeviceRequest(Token, "android"));

        Assert.Equal(_bob, (await SavedAsync()).UserId);
        Assert.Equal(1, await _db.DeviceTokens.CountAsync());
    }

    /// <summary>Two registrations at once: the unique index refuses the loser, which then updates the winner's row.</summary>
    [Fact]
    public async Task Losing_a_race_to_insert_updates_the_winning_row()
    {
        var winner = new DeviceToken
        {
            Id = Guid.NewGuid(), UserId = _bob, Token = Token, Platform = "ios",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var uow = Substitute.For<IUnitOfWork>();
        var saves = 0;
        uow.SaveChangesAsync(Arg.Any<CancellationToken>()).Returns(async _ =>
        {
            if (++saves > 1) return await _db.SaveChangesAsync();

            // The winner commits from its own connection while this request's insert is refused.
            await using var other = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(_dbName).Options);
            other.DeviceTokens.Add(winner);
            await other.SaveChangesAsync();
            throw new DbUpdateException("duplicate key");
        });

        await Sut(uow).RegisterAsync(new RegisterDeviceRequest(Token, "android"));

        var saved = await SavedAsync();
        Assert.Equal(winner.Id, saved.Id);
        Assert.Equal(_alice, saved.UserId);
        Assert.Equal("android", saved.Platform);
    }

    [Fact]
    public async Task Removing_your_own_device_deletes_it_and_doing_it_again_is_fine()
    {
        await Sut().RegisterAsync(new RegisterDeviceRequest(Token, "android"));

        await Sut().RemoveAsync(new RemoveDeviceRequest(Token));
        Assert.False(await _db.DeviceTokens.AnyAsync());

        await Sut().RemoveAsync(new RemoveDeviceRequest(Token));
        Assert.False(await _db.DeviceTokens.AnyAsync());
    }

    [Fact]
    public async Task You_cannot_remove_someone_else_s_device()
    {
        await Sut().RegisterAsync(new RegisterDeviceRequest(Token, "android"));

        _currentUser.UserId.Returns(_bob);
        await Sut().RemoveAsync(new RemoveDeviceRequest(Token));

        Assert.Equal(_alice, (await SavedAsync()).UserId);
    }

    /// <summary>Anonymous callers and campaign-token holders pass the route's permission gate and have no account.</summary>
    [Fact]
    public async Task Without_an_account_both_are_refused()
    {
        _currentUser.UserId.Returns((Guid?)null);

        await Assert.ThrowsAsync<UnauthorizedException>(() => Sut().RegisterAsync(new RegisterDeviceRequest(Token, "android")));
        await Assert.ThrowsAsync<UnauthorizedException>(() => Sut().RemoveAsync(new RemoveDeviceRequest(Token)));
        Assert.False(await _db.DeviceTokens.AnyAsync());
    }

    /// <summary>A session can outlive its account after a merge; without this the foreign key gives a 500.</summary>
    [Fact]
    public async Task A_session_whose_account_is_gone_is_refused()
    {
        _currentUser.UserId.Returns(Guid.NewGuid());

        await Assert.ThrowsAsync<UnauthorizedException>(() => Sut().RegisterAsync(new RegisterDeviceRequest(Token, "android")));
    }

    [Theory]
    [InlineData("", "android")]
    [InlineData("   ", "android")]
    [InlineData(Token, "")]
    [InlineData(Token, "windows")]
    [InlineData(Token, "Android")]
    public async Task A_bad_registration_is_refused(string token, string platform)
    {
        await Assert.ThrowsAsync<ValidationException>(() => Sut().RegisterAsync(new RegisterDeviceRequest(token, platform)));
        Assert.False(await _db.DeviceTokens.AnyAsync());
    }

    [Fact]
    public async Task A_token_too_long_for_the_column_is_refused_rather_than_failing_at_the_database()
    {
        await Sut().RegisterAsync(new RegisterDeviceRequest(new string('a', DeviceToken.MaxTokenLength), "ios"));

        await Assert.ThrowsAsync<ValidationException>(() =>
            Sut().RegisterAsync(new RegisterDeviceRequest(new string('a', DeviceToken.MaxTokenLength + 1), "ios")));
    }

    [Fact]
    public async Task Removing_needs_a_token() =>
        await Assert.ThrowsAsync<ValidationException>(() => Sut().RemoveAsync(new RemoveDeviceRequest("")));

    [Theory]
    [InlineData(nameof(DevicesController.Register))]
    [InlineData(nameof(DevicesController.Remove))]
    public void Both_routes_use_the_signed_in_gate_and_a_rate_limit(string action)
    {
        var method = typeof(DevicesController).GetMethod(action)!;

        Assert.Equal(Permissions.Templates.Read, method.GetCustomAttribute<HasPermissionAttribute>()!.Permission);
        Assert.Equal("openlink", method.GetCustomAttribute<EnableRateLimitingAttribute>()!.PolicyName);
    }
}
