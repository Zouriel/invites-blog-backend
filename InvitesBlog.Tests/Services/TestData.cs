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
    /// <summary>A plan for tests: active and free-sized unless told otherwise.</summary>
    public static InvitesBlog.Application.Plans.EventPlan Plan(
        long eventBytes = 10 * InvitesBlog.Application.Plans.PlanCatalog.Gb, int maxBuckets = 1, int maxWindowDays = 1,
        InvitesBlog.Application.Plans.MediaPhase phase = InvitesBlog.Application.Plans.MediaPhase.Active,
        InvitesBlog.Application.Plans.PlanKind kind = InvitesBlog.Application.Plans.PlanKind.Free,
        long? accountBytes = null, Guid? owner = null, bool privateAlbums = false, Guid? venueId = null) =>
        new(kind, eventBytes, accountBytes, maxBuckets, maxWindowDays, 0, privateAlbums,
            kind == InvitesBlog.Application.Plans.PlanKind.Free, null, phase, owner, venueId);

    /// <summary>An event that may email <paramref name="left"/> more guests (plenty, by default).</summary>
    public static InvitesBlog.Application.Plans.ISendingAllowanceService Allowance(int left = 100_000, int used = 0)
    {
        var s = Substitute.For<InvitesBlog.Application.Plans.ISendingAllowanceService>();
        s.ForCampaignAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new InvitesBlog.Application.Plans.SendingAllowanceDto(left + used, 0, used));
        return s;
    }

    /// <summary>A price book holding <paramref name="prices"/>, or the defaults in code.</summary>
    public static InvitesBlog.Application.Plans.IPriceBook PriceBook(InvitesBlog.Application.Plans.Prices? prices = null)
    {
        var book = Substitute.For<InvitesBlog.Application.Plans.IPriceBook>();
        book.CurrentAsync(Arg.Any<CancellationToken>()).Returns(prices ?? InvitesBlog.Application.Plans.Prices.Defaults);
        return book;
    }

    /// <summary>A plan service that answers every event with <see cref="Plan"/>.</summary>
    public static InvitesBlog.Application.Plans.IPlanService FreePlans()
    {
        var plans = Substitute.For<InvitesBlog.Application.Plans.IPlanService>();
        plans.ForCampaignAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(Plan());
        return plans;
    }

    /// <summary>A table with nothing in it, for services that look something up there that a test doesn't use.</summary>
    public static InvitesBlog.Application.Abstractions.Persistence.IRepository<T> Empty<T>() where T : class
    {
        var repo = Substitute.For<InvitesBlog.Application.Abstractions.Persistence.IRepository<T>>();
        repo.Query(Arg.Any<bool>()).Returns(Array.Empty<T>().AsAsyncQueryable());
        return repo;
    }

    /// <summary>A celebrants table with nobody in it, for services that look people up there.</summary>
    public static InvitesBlog.Application.Abstractions.Persistence.IRepository<CampaignCelebrant> NoCelebrants()
    {
        var repo = Substitute.For<InvitesBlog.Application.Abstractions.Persistence.IRepository<CampaignCelebrant>>();
        repo.Query(Arg.Any<bool>()).Returns(Array.Empty<CampaignCelebrant>().AsAsyncQueryable());
        return repo;
    }

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
        DesignerName = "Studio Test",
        SceneJson = "{}",
        ManifestJson = "{}",
        PackageUrl = "https://cdn.test/pkg.zip",
        IsActive = active,
        CreatedAt = DateTimeOffset.UtcNow
    };

    public static Campaign Campaign(
        Guid? id = null, Guid? templateId = null, CampaignStatus status = CampaignStatus.Draft,
        int paidCapacity = 0) => new()
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
        Currency = "MVR",
        Status = status,
        Provider = "Fake",
        ProviderSessionId = sessionId,
        CreatedAt = DateTimeOffset.UtcNow
    };

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
