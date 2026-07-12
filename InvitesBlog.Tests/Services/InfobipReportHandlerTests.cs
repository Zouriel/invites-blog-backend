using InvitesBlog.Application.Abstractions;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using InvitesBlog.Infrastructure.Delivery;
using InvitesBlog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>
/// Infobip Viber delivery-report handling: DELIVERED marks the attempt delivered; an undeliverable
/// report flips it to Failed and auto-falls-back to email exactly once (idempotent on duplicates).
/// </summary>
public class InfobipReportHandlerTests
{
    private static InfobipReportHandler Sut(AppDbContext db, params IInviteDeliveryProvider[] providers)
    {
        var dispatch = new DispatchService(db, providers, Substitute.For<IConfiguration>(),
            Substitute.For<ILogger<DispatchService>>());
        return new InfobipReportHandler(db, dispatch, Substitute.For<ILogger<InfobipReportHandler>>());
    }

    private static string Report(string messageId, string groupName, string? error = null)
    {
        var errorJson = error is null ? "" : $", \"error\": {{ \"name\": \"{error}\" }}";
        return $"{{ \"results\": [ {{ \"messageId\": \"{messageId}\", " +
               $"\"status\": {{ \"groupName\": \"{groupName}\" }}{errorJson} }} ] }}";
    }

    [Fact]
    public async Task Delivered_report_marks_attempt_delivered()
    {
        using var db = DeliveryTestHarness.NewDb();
        var invite = new Invite { Id = Guid.NewGuid(), CampaignId = Guid.NewGuid(), GuestId = Guid.NewGuid(), TokenHash = "h" };
        db.Invites.Add(invite);
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id = Guid.NewGuid(), InviteId = invite.Id, Channel = "viber",
            RecipientAddress = "9601234567", Status = DeliveryStatus.Sent,
            ProviderMessageId = "m1", AttemptedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        await Sut(db).HandleReportAsync(Report("m1", "DELIVERED"));

        Assert.Equal(DeliveryStatus.Delivered,
            (await db.DeliveryAttempts.SingleAsync(a => a.ProviderMessageId == "m1")).Status);
    }

    [Fact]
    public async Task Undeliverable_report_fails_attempt_and_falls_back_to_email_once()
    {
        using var db = DeliveryTestHarness.NewDb();
        var campaign = DeliveryTestHarness.SeedCampaign(db);
        var guest = DeliveryTestHarness.SeedGuest(db, campaign.Id, email: "a@test.com", phone: "+9601234567");
        var invite = new Invite
        {
            Id = Guid.NewGuid(), CampaignId = campaign.Id, GuestId = guest.Id, Status = InviteStatus.Sent, TokenHash = "h"
        };
        db.Invites.Add(invite);
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id = Guid.NewGuid(), InviteId = invite.Id, Channel = "viber",
            RecipientAddress = "9601234567", Status = DeliveryStatus.Sent,
            ProviderMessageId = "m2", AttemptedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();

        var email = new FakeProvider("email", succeeds: true);
        var handler = Sut(db, email);

        await handler.HandleReportAsync(Report("m2", "UNDELIVERABLE", "EC_ABSENT_SUBSCRIBER"));

        // Viber attempt failed; email fallback fired and succeeded.
        Assert.Equal(DeliveryStatus.Failed,
            (await db.DeliveryAttempts.SingleAsync(a => a.ProviderMessageId == "m2")).Status);
        Assert.Single(email.Calls);
        Assert.Equal(1, await db.DeliveryAttempts.CountAsync(a => a.Channel == "email" && a.Status == DeliveryStatus.Sent));

        // Duplicate report → no second email (idempotent).
        await handler.HandleReportAsync(Report("m2", "UNDELIVERABLE", "EC_ABSENT_SUBSCRIBER"));
        Assert.Single(email.Calls);
        Assert.Equal(1, await db.DeliveryAttempts.CountAsync(a => a.Channel == "email"));
    }

    [Fact]
    public async Task Unknown_message_id_is_ignored()
    {
        using var db = DeliveryTestHarness.NewDb();
        await Sut(db).HandleReportAsync(Report("does-not-exist", "DELIVERED"));
        Assert.Equal(0, await db.DeliveryAttempts.CountAsync());
    }
}
