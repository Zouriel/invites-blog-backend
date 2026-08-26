using InvitesBlog.Domain.Enums;

namespace InvitesBlog.Domain.Entities;

/// <summary>An OTP challenge for invitee inbox login (§8.2 OtpChallenge, §11.1).</summary>
public sealed class OtpChallenge
{
    public Guid Id { get; set; }
    public OtpChannel Channel { get; set; }            // Sms | Email
    public string? PhoneE164 { get; set; }
    public string? Email { get; set; }
    public string CodeHash { get; set; } = default!;
    public int Attempts { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? VerifiedAt { get; set; }

    /// <summary>
    /// Which send budget this code was drawn from. Reauth and sign-in are counted separately so that
    /// one cannot lock a guest out of the other — see <see cref="OtpPurpose"/>.
    /// </summary>
    public OtpPurpose Purpose { get; set; }
}
