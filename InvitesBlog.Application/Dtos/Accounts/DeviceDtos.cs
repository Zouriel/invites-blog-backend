namespace InvitesBlog.Application.Dtos.Accounts;

/// <summary>
/// Registers this device for push, for the signed-in account. Platform: "android" or "ios". Environment,
/// iOS only: "production" (TestFlight, App Store; the default) or "sandbox" (a build run from Xcode).
/// </summary>
public sealed record RegisterDeviceRequest(string Token, string Platform, string? Environment = null);

/// <summary>Stops push to this device for the signed-in account.</summary>
public sealed record RemoveDeviceRequest(string Token);
