using InvitesBlog.Application.Dtos.Otp;

namespace InvitesBlog.Application.Services.Otp;

/// <summary>OTP request/verify for invitee inbox login (§10.7).</summary>
public interface IOtpService
{
    Task<OtpChallengeResponse> RequestAsync(SendOtpRequest req, CancellationToken ct = default);
    Task<OtpTokensResponse> VerifyAsync(VerifyOtpRequest req, CancellationToken ct = default);

    /// <summary>Guest-list-gated OTP for the shared campaign link: only sends a code if the email is on
    /// the campaign's guest list (no blind sends to uninvited addresses).</summary>
    Task<CampaignOtpResponse> RequestForCampaignAsync(Guid campaignId, string email, CancellationToken ct = default);

    /// <summary>
    /// Consumes a challenge and returns the contact it proved, WITHOUT issuing any token. Account
    /// sign-in needs the verified identifier so it can find or create the account behind it; sharing
    /// this with <see cref="VerifyAsync"/> keeps the expiry/attempt rules in exactly one place.
    /// </summary>
    Task<VerifiedContact> VerifyContactAsync(VerifyOtpRequest req, CancellationToken ct = default);
}

/// <summary>An identifier a challenge has just proved the caller controls.</summary>
/// <param name="ContactType">"phone" or "email".</param>
public sealed record VerifiedContact(string ContactType, string Contact);
