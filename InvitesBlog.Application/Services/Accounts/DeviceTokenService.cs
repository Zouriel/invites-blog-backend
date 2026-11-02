using FluentValidation;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Accounts;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Services.Accounts;

public sealed class DeviceTokenService(
    ICurrentUser currentUser,
    IRepository<AppUser> users,
    IRepository<DeviceToken> devices,
    IUnitOfWork uow,
    IValidator<RegisterDeviceRequest> registerValidator,
    IValidator<RemoveDeviceRequest> removeValidator) : IDeviceTokenService
{
    public async Task RegisterAsync(RegisterDeviceRequest request, CancellationToken ct = default)
    {
        var userId = await SignedInAsync(ct);
        await registerValidator.ValidateAndThrowAsync(request, ct);

        var now = DateTimeOffset.UtcNow;
        var existing = await FindAsync(request.Token, ct);
        if (existing is not null)
        {
            Apply(existing, userId, request, now);
            await uow.SaveChangesAsync(ct);
            return;
        }

        var added = new DeviceToken
        {
            Id = Guid.NewGuid(), UserId = userId, Token = request.Token, Platform = request.Platform,
            Environment = EnvironmentOf(request),
            CreatedAt = now, UpdatedAt = now,
        };
        await devices.AddAsync(added, ct);
        try
        {
            await uow.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // An app can register twice at once (start-up and a token refresh), and the unique index
            // lets only one insert win. Removing an unsaved row just forgets it; then update the winner.
            devices.Remove(added);
            var winner = await FindAsync(request.Token, ct);
            if (winner is null) throw;
            Apply(winner, userId, request, now);
            await uow.SaveChangesAsync(ct);
        }
    }

    public async Task RemoveAsync(RemoveDeviceRequest request, CancellationToken ct = default)
    {
        var userId = await SignedInAsync(ct);
        await removeValidator.ValidateAndThrowAsync(request, ct);

        var mine = await devices.Query(tracking: true)
            .FirstOrDefaultAsync(d => d.Token == request.Token && d.UserId == userId, ct);
        if (mine is null) return;

        devices.Remove(mine);
        try
        {
            await uow.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // A simultaneous request removed it first, which is the outcome asked for.
        }
    }

    /// <summary>
    /// The permission gate on these routes also admits anonymous callers and campaign-token holders,
    /// so an account is required here. A session can also outlive its account after a merge.
    /// </summary>
    private async Task<Guid> SignedInAsync(CancellationToken ct)
    {
        var id = currentUser.UserId ?? throw new UnauthorizedException();
        return await users.AnyAsync(u => u.Id == id, ct) ? id : throw new UnauthorizedException();
    }

    private Task<DeviceToken?> FindAsync(string token, CancellationToken ct) =>
        devices.Query(tracking: true).FirstOrDefaultAsync(d => d.Token == token, ct);

    private static void Apply(DeviceToken device, Guid userId, RegisterDeviceRequest request, DateTimeOffset now)
    {
        device.UserId = userId;
        device.Platform = request.Platform;
        device.Environment = EnvironmentOf(request);
        device.UpdatedAt = now;
    }

    /// <summary>Only iOS tokens have an environment; production unless the app says sandbox.</summary>
    private static string? EnvironmentOf(RegisterDeviceRequest request) =>
        request.Platform == DeviceToken.Ios
            ? request.Environment == DeviceToken.Sandbox ? DeviceToken.Sandbox : DeviceToken.Production
            : null;
}
