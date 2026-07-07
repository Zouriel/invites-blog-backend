using FluentValidation;
using InvitesBlog.Application.Dtos.Guests;

namespace InvitesBlog.Application.Validation.Guests;

/// <summary>Validates a confirm-upload request (§15.3). Auto-registered by the DI scanner.</summary>
public sealed class ConfirmUploadRequestValidator : AbstractValidator<ConfirmUploadRequest>
{
    public ConfirmUploadRequestValidator()
    {
        RuleFor(x => x.UploadId).NotEmpty();
    }
}
