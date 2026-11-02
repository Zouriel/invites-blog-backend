using InvitesBlog.Application.Dtos.Accounts;

namespace InvitesBlog.Application.Services.Accounts;

/// <summary>The signed-in account's devices that can receive push notifications.</summary>
public interface IDeviceTokenService
{
    /// <summary>Adds the device, or moves it to this account if another account had it.</summary>
    Task RegisterAsync(RegisterDeviceRequest request, CancellationToken ct = default);

    /// <summary>Removes the device if it belongs to this account. Doing so twice is not an error.</summary>
    Task RemoveAsync(RemoveDeviceRequest request, CancellationToken ct = default);
}
