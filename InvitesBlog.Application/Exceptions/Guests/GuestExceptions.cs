namespace InvitesBlog.Application.Exceptions.Guests;

public sealed class GuestNotFoundException(Guid id)
    : NotFoundException($"Guest '{id}' was not found.", "guest_not_found");

public sealed class GuestFileRejectedException(string reason)
    : BusinessRuleException(reason, "guest_file_rejected");

public sealed class GuestContactRequiredException()
    : BusinessRuleException("At least one of email or phone is required.", "guest_contact_required");

public sealed class ResendLimitExceededException()
    : InvalidStateException("Resend limit reached (max 3 per 24 hours).", "resend_limit");

public sealed class UploadNotFoundException(Guid id)
    : NotFoundException($"Upload '{id}' was not found.", "upload_not_found");
