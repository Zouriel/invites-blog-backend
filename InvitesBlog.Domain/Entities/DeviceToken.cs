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

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
