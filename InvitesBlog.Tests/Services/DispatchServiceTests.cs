using InvitesBlog.Application.Abstractions;
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
/// Channel-policy tests for <see cref="DispatchService"/> (§product rule): default Viber → email,
/// silent skip when a channel lacks contact info, and the explicit "not sent — no contact" outcome.
/// </summary>
public class DispatchServiceTests
{
    private static DispatchService Sut(AppDbContext db, params IInviteDeliveryProvider[] providers) =>
        new(db, providers, Substitute.For<IConfiguration>(), Substitute.For<ILogger<DispatchService>>());

    private static async Task<List<DeliveryAttemptRow>> AttemptsAsync(AppDbContext db, Guid campaignId)
    {
        var inviteIds = await db.Invites.Where(i => i.CampaignId == campaignId).Select(i => i.Id).ToListAsync();
        return await db.DeliveryAttempts
            .Where(a => inviteIds.Contains(a.InviteId))
            .Select(a => new DeliveryAttemptRow(a.Channel, a.Status))
            .ToListAsync();
    }

    private record DeliveryAttemptRow(string Channel, DeliveryStatus Status);

    [Fact]
    public async Task Default_settings_resolve_to_viber_then_email()
    {
        using var db = DeliveryTestHarness.NewDb();
        var c = DeliveryTestHarness.SeedCampaign(db, settingsJson: "{}");   // legacy empty JSON → defaults
        DeliveryTestHarness.SeedGuest(db, c.Id, email: "a@test.com", phone: "+9601234567");
        await db.SaveChangesAsync();

        var viber = new FakeProvider("viber", succeeds: true);
        var email = new FakeProvider("email", succeeds: true);
        await Sut(db, viber, email).DispatchCampaignAsync(c.Id);

        Assert.Single(viber.Calls);        // Viber tried first
        Assert.Empty(email.Calls);         // succeeded → email never attempted
        var attempts = await AttemptsAsync(db, c.Id);
        Assert.Equal(new[] { "viber" }, attempts.Select(a => a.Channel));
        Assert.Equal(InviteStatus.Sent, (await db.Invites.SingleAsync()).Status);
    }

    [Fact]
    public async Task Phone_only_guest_uses_viber_and_no_email_attempt()
    {
        using var db = DeliveryTestHarness.NewDb();
        var c = DeliveryTestHarness.SeedCampaign(db);
        DeliveryTestHarness.SeedGuest(db, c.Id, email: null, phone: "+9601234567");
        await db.SaveChangesAsync();

        var viber = new FakeProvider("viber", succeeds: true);
        var email = new FakeProvider("email", succeeds: true);
        await Sut(db, viber, email).DispatchCampaignAsync(c.Id);

        Assert.Single(viber.Calls);
        Assert.Empty(email.Calls);
    }

    [Fact]
    public async Task Email_only_guest_skips_viber_silently_and_emails()
    {
        using var db = DeliveryTestHarness.NewDb();
        var c = DeliveryTestHarness.SeedCampaign(db);
        DeliveryTestHarness.SeedGuest(db, c.Id, email: "a@test.com", phone: null);
        await db.SaveChangesAsync();

        var viber = new FakeProvider("viber", succeeds: true);
        var email = new FakeProvider("email", succeeds: true);
        await Sut(db, viber, email).DispatchCampaignAsync(c.Id);

        Assert.Empty(viber.Calls);          // no phone → viber skipped, no attempt recorded
        Assert.Single(email.Calls);
        var attempts = await AttemptsAsync(db, c.Id);
        Assert.Equal(new[] { "email" }, attempts.Select(a => a.Channel));
        Assert.Equal(InviteStatus.Sent, (await db.Invites.SingleAsync()).Status);
    }

    [Fact]
    public async Task Viber_failure_falls_back_to_email_with_two_attempts()
    {
        using var db = DeliveryTestHarness.NewDb();
        var c = DeliveryTestHarness.SeedCampaign(db);
        DeliveryTestHarness.SeedGuest(db, c.Id, email: "a@test.com", phone: "+9601234567");
        await db.SaveChangesAsync();

        var viber = new FakeProvider("viber", succeeds: false);
        var email = new FakeProvider("email", succeeds: true);
        await Sut(db, viber, email).DispatchCampaignAsync(c.Id);

        Assert.Single(viber.Calls);
        Assert.Single(email.Calls);
        var attempts = await AttemptsAsync(db, c.Id);
        Assert.Equal(new[] { "viber", "email" }, attempts.Select(a => a.Channel));
        Assert.Equal(DeliveryStatus.Failed, attempts[0].Status);
        Assert.Equal(DeliveryStatus.Sent, attempts[1].Status);
        Assert.Equal(InviteStatus.Sent, (await db.Invites.SingleAsync()).Status);
    }

    [Fact]
    public async Task Guest_with_no_contact_is_not_sent_and_campaign_still_dispatched()
    {
        using var db = DeliveryTestHarness.NewDb();
        var c = DeliveryTestHarness.SeedCampaign(db);
        DeliveryTestHarness.SeedGuest(db, c.Id, email: "reachable@test.com", phone: null, name: "Reachable");
        DeliveryTestHarness.SeedGuest(db, c.Id, email: null, phone: null, name: "Unreachable");
        await db.SaveChangesAsync();

        var viber = new FakeProvider("viber", succeeds: true);
        var email = new FakeProvider("email", succeeds: true);
        await Sut(db, viber, email).DispatchCampaignAsync(c.Id);

        var notSent = await db.Invites.CountAsync(i => i.Status == InviteStatus.NotSent);
        Assert.Equal(1, notSent);

        var skipped = await db.DeliveryAttempts
            .Where(a => a.Status == DeliveryStatus.Skipped)
            .ToListAsync();
        Assert.Single(skipped);
        Assert.Equal("none", skipped[0].Channel);
        Assert.Contains("no phone", skipped[0].ErrorMessage);

        // One reachable guest delivered, one not-sent (no contact) → campaign is Dispatched, not partial.
        Assert.Equal(CampaignStatus.Dispatched, (await db.Campaigns.SingleAsync()).Status);
    }
}
