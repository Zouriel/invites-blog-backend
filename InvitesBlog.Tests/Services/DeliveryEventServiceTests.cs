using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Services.Delivery;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

public class DeliveryEventServiceTests
{
    private readonly IRepository<DeliveryAttempt> _attempts = Substitute.For<IRepository<DeliveryAttempt>>();
    private readonly ISuppressionRepository _suppression = Substitute.For<ISuppressionRepository>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();

    private DeliveryEventService Sut() =>
        new(_attempts, _suppression, _uow, Substitute.For<ILogger<DeliveryEventService>>());

    private DeliveryAttempt Attempt(string messageId, DeliveryStatus status = DeliveryStatus.Sent) => new()
    {
        Id = Guid.NewGuid(), InviteId = Guid.NewGuid(), Channel = "email",
        RecipientAddress = "g@test.com", Status = status, ProviderMessageId = messageId,
        AttemptedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task Delivered_marks_attempt_delivered()
    {
        var a = Attempt("msg-1");
        _attempts.Query(true).Returns(new[] { a }.AsAsyncQueryable());
        await Sut().ProcessAsync("email.delivered", "msg-1", Array.Empty<string>());
        Assert.Equal(DeliveryStatus.Delivered, a.Status);
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Bounced_marks_attempt_failed()
    {
        var a = Attempt("msg-2");
        _attempts.Query(true).Returns(new[] { a }.AsAsyncQueryable());
        await Sut().ProcessAsync("email.bounced", "msg-2", Array.Empty<string>());
        Assert.Equal(DeliveryStatus.Failed, a.Status);
    }

    [Fact]
    public async Task Complained_marks_failed_and_suppresses_contact()
    {
        var a = Attempt("msg-3");
        _attempts.Query(true).Returns(new[] { a }.AsAsyncQueryable());
        _suppression.ExistsByHashAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        await Sut().ProcessAsync("email.complained", "msg-3", new[] { "g@test.com" });

        Assert.Equal(DeliveryStatus.Failed, a.Status);
        await _suppression.Received(1).AddAsync(Arg.Is<SuppressionEntry>(s => s.ContactType == "email"), Arg.Any<CancellationToken>());
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Unknown_event_is_a_noop()
    {
        await Sut().ProcessAsync("email.opened", "msg-4", Array.Empty<string>());
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Idempotent_when_already_in_target_status()
    {
        var a = Attempt("msg-5", DeliveryStatus.Delivered);
        _attempts.Query(true).Returns(new[] { a }.AsAsyncQueryable());
        await Sut().ProcessAsync("email.delivered", "msg-5", Array.Empty<string>());
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
