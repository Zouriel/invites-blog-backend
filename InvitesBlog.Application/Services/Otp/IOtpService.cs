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
}
