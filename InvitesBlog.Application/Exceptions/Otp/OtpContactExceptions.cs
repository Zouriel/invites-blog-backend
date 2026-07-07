namespace InvitesBlog.Application.Exceptions.Otp;

/// <summary>The supplied phone number could not be normalized to a usable E.164 number (§4.4.4).</summary>
public sealed class OtpInvalidPhoneException()
    : BusinessRuleException("The phone number is invalid.", "otp_invalid_phone");
