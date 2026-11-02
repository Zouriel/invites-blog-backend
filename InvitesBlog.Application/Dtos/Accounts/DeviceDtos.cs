namespace InvitesBlog.Application.Dtos.Accounts;

/// <summary>Registers this device for push, for the signed-in account. Platform: "android" or "ios".</summary>
public sealed record RegisterDeviceRequest(string Token, string Platform);

/// <summary>Stops push to this device for the signed-in account.</summary>
public sealed record RemoveDeviceRequest(string Token);
