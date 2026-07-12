namespace InvitesBlog.Application.Dtos.Otp;

/// <summary>Request an OTP code by SMS or email (§10.7 / §11.1).</summary>
public sealed record SendOtpRequest(string Channel, string? Phone, string? Email, string? DefaultCountry);

/// <summary>Verify a previously requested OTP code.</summary>
public sealed record VerifyOtpRequest(Guid ChallengeId, string Code);

/// <summary>Returned after a code is sent: the challenge id and its lifetime (seconds).</summary>
public sealed record OtpChallengeResponse(Guid ChallengeId, int ExpiresInSeconds);

/// <summary>Returned after a successful verification: the invitee access + refresh tokens.</summary>
public sealed record OtpTokensResponse(string AccessToken, string RefreshToken);

/// <summary>Request an OTP for the shared campaign link — a code is sent ONLY if the email is on the
/// campaign's guest list, so an uninvited address never triggers an email.</summary>
public sealed record CampaignOtpRequest(string Email);

/// <summary>
/// Result of a guest-list-gated OTP request. A challenge is created (and a code emailed) only when
/// <c>Invited</c> is true; otherwise the caller shows a "not invited" / "cancelled" message and no email
/// is wasted. Campaign existence is never leaked — an unknown campaign reads as simply "not invited".
/// </summary>
public sealed record CampaignOtpResponse(bool Invited, bool Cancelled, Guid? ChallengeId, int ExpiresInSeconds);
