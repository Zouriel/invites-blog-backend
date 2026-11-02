namespace InvitesBlog.Domain.Entities;

/// <summary>
/// A phone or tablet that can receive push notifications for an account. The token belongs to the app
/// install, not the person, so it has one row and follows whoever last signed in on that device.
/// </summary>
public sealed class DeviceToken
{
    /// <summary>Longest token accepted. Generous for FCM, and short enough for a Postgres unique index.</summary>
    public const int MaxTokenLength = 1024;

    public const string Android = "android";
    public const string Ios = "ios";

    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public string Token { get; set; } = default!;

    /// <summary><see cref="Android"/> or <see cref="Ios"/>.</summary>
    public string Platform { get; set; } = default!;

    /// <summary>
    /// For iOS: which Apple push service issued the token, <see cref="Production"/> (TestFlight and the
    /// App Store) or <see cref="Sandbox"/> (a build run from Xcode). Null means production.
    /// </summary>
    public string? Environment { get; set; }

    public const string Production = "production";
    public const string Sandbox = "sandbox";

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
