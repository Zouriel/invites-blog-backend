using InvitesBlog.Application.Dtos.Otp;

namespace InvitesBlog.Application.Services.Otp;

/// <summary>OTP request/verify for invitee inbox login (§10.7).</summary>
public interface IOtpService
{
    Task<OtpChallengeResponse> RequestAsync(SendOtpRequest req, CancellationToken ct = default);
    Task<OtpTokensResponse> VerifyAsync(VerifyOtpRequest req, CancellationToken ct = default);
}
