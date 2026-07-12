namespace InvitesBlog.Application.Exceptions.Inquiries;

public sealed class InquiryNotFoundException(System.Guid id)
    : NotFoundException($"Inquiry '{id}' was not found.", "inquiry_not_found");
