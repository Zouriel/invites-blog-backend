using FluentValidation;
using FluentValidation.Results;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using NSubstitute;

namespace InvitesBlog.Tests.Services;

/// <summary>
/// Reusable entity factories + validator/config stubs for the service unit tests. Keeps each test
/// focused on the scenario under test rather than repeating construction boilerplate.
/// </summary>
internal static class TestData
{
    public static Template Template(Guid? id = null, bool active = true) => new()
    {
        Id = id ?? Guid.NewGuid(),
        Name = "Golden Bloom",
        Slug = "golden-bloom",
        Version = "1.0.0",
        Category = "wedding",
        Description = "An elegant wedding template.",
        PreviewImageUrl = "https://cdn.test/preview.png",
        PreviewAnimationUrl = null,
        IsPremium = false,
        DesignerName = "Studio Test",
        SceneJson = "{}",
        ManifestJson = "{}",
        PackageUrl = "https://cdn.test/pkg.zip",
        IsActive = active,
        CreatedAt = DateTimeOffset.UtcNow
    };

    public static Campaign Campaign(
        Guid? id = null, Guid? templateId = null, CampaignStatus status = CampaignStatus.Draft,
        int paidCapacity = 0, bool hasDesignerDiscount = false) => new()
    {
        Id = id ?? Guid.NewGuid(),
        TemplateId = templateId ?? Guid.NewGuid(),
        TemplateVersion = "1.0.0",
        AccessTokenHash = "access-hash",
        Title = "Aisha & Omar",
        Slug = "aisha-omar-abc123",
        Status = status,
        EventType = "wedding",
        EventStartAt = DateTimeOffset.UtcNow.AddDays(30),
        PaidInviteCapacity = paidCapacity,
        HasDesignerDiscount = hasDesignerDiscount,
        CreatedAt = DateTimeOffset.UtcNow,
        UpdatedAt = DateTimeOffset.UtcNow
    };

    public static Guest Guest(Guid campaignId, Guid? id = null, string? email = "guest@test.com", string? phone = "+9607777777") => new()
    {
        Id = id ?? Guid.NewGuid(),
        CampaignId = campaignId,
        Email = email,
        PhoneE164 = phone,
        PhoneRaw = phone,
        Name = "Test Guest",
        Role = "guest",
        Gender = "unspecified",
        CreatedAt = DateTimeOffset.UtcNow
    };

    public static Invite Invite(
        Guid campaignId, Guid guestId, Guid? id = null, string tokenHash = "token-hash",
        bool requiresOtp = false, InviteStatus status = InviteStatus.Sent,
        RsvpStatus rsvp = RsvpStatus.NoResponse) => new()
    {
        Id = id ?? Guid.NewGuid(),
        CampaignId = campaignId,
        GuestId = guestId,
        TokenHash = tokenHash,
        RequiresOtp = requiresOtp,
        Status = status,
        RsvpStatus = rsvp,
        CreatedAt = DateTimeOffset.UtcNow
    };

    public static Payment Payment(
        Guid campaignId, PaymentKind kind = PaymentKind.Initial, PaymentStatus status = PaymentStatus.Paid,
        int inviteCount = 50, decimal amount = 10m, string? sessionId = "sess_1") => new()
    {
        Id = Guid.NewGuid(),
        CampaignId = campaignId,
        Kind = kind,
        InviteCount = inviteCount,
        Amount = amount,
        Currency = "USD",
        Status = status,
        Provider = "Fake",
        ProviderSessionId = sessionId,
        CreatedAt = DateTimeOffset.UtcNow
    };

    public static AppUser AdminUser(string email = "admin@test.com", string password = "correct-horse", bool isActive = true, bool isAdmin = true)
    {
        var role = new Role { Id = Guid.NewGuid(), Name = isAdmin ? InvitesBlog.Domain.Authorization.Roles.Admin : "Inviter", Description = "" };
        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            Email = email,
            DisplayName = "Test Admin",
            PasswordHash = InvitesBlog.Application.Security.PasswordHasher.Hash(password),
            IsActive = isActive
        };
        user.UserRoles.Add(new UserRole { UserId = user.Id, User = user, RoleId = role.Id, Role = role });
        return user;
    }

    // A validator stub that always passes (no failures).
    public static IValidator<T> PassingValidator<T>()
    {
        var v = Substitute.For<IValidator<T>>();
        v.ValidateAsync(Arg.Any<IValidationContext>(), Arg.Any<CancellationToken>()).Returns(new ValidationResult());
        v.ValidateAsync(Arg.Any<T>(), Arg.Any<CancellationToken>()).Returns(new ValidationResult());
        return v;
    }

    // A validator stub that throws ValidationException, mirroring what ValidateAndThrowAsync does on a
    // real validator when a rule fails (the throw lives in the validator body, which a plain
    // returns-a-failing-result substitute would bypass — so we throw directly).
    public static IValidator<T> FailingValidator<T>(string property = "Field", string message = "is required")
    {
        var v = Substitute.For<IValidator<T>>();
        var failures = new[] { new ValidationFailure(property, message) };
        v.ValidateAsync(Arg.Any<IValidationContext>(), Arg.Any<CancellationToken>())
            .Returns<Task<ValidationResult>>(_ => throw new ValidationException(failures));
        v.ValidateAsync(Arg.Any<T>(), Arg.Any<CancellationToken>())
            .Returns<Task<ValidationResult>>(_ => throw new ValidationException(failures));
        return v;
    }
}
