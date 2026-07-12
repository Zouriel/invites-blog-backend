using FluentValidation;
using InvitesBlog.Application.Dtos.Inquiries;

namespace InvitesBlog.Application.Validation.Inquiries;

public sealed class SubmitInquiryRequestValidator : AbstractValidator<SubmitInquiryRequest>
{
    public SubmitInquiryRequestValidator()
    {
        RuleFor(x => x.Name).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Email).NotEmpty().EmailAddress().MaximumLength(200);
        RuleFor(x => x.Occasion).NotEmpty().MaximumLength(120);
        RuleFor(x => x.Message).NotEmpty().MaximumLength(4000);
    }
}
