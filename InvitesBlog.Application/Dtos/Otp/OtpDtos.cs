namespace InvitesBlog.Application.Dtos.Otp;

/// <summary>Request an OTP code by SMS or email (§10.7 / §11.1).</summary>
public sealed record SendOtpRequest(string Channel, string? Phone, string? Email, string? DefaultCountry);

/// <summary>Verify a previously requested OTP code.</summary>
public sealed record VerifyOtpRequest(Guid ChallengeId, string Code);

/// <summary>Returned after a code is sent: the challenge id and its lifetime (seconds).</summary>
public sealed record OtpChallengeResponse(Guid ChallengeId, int ExpiresInSeconds);

/// <summary>Returned after a successful verification: the invitee access + refresh tokens.</summary>
public sealed record OtpTokensResponse(string AccessToken, string RefreshToken);
