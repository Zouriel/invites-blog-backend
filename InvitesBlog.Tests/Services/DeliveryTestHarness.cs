using InvitesBlog.Application.Abstractions;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace InvitesBlog.Tests.Services;

/// <summary>Shared helpers for the Viber/email delivery tests: an in-memory DbContext and fake providers.</summary>
internal static class DeliveryTestHarness
{
    public static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .ConfigureWarnings(w => w.Ignore(InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    public static Campaign SeedCampaign(AppDbContext db, string settingsJson = "{}", Guid? inviterId = null)
    {
        var c = new Campaign
        {
            Id = Guid.NewGuid(),
            Title = "Test",
            Slug = "test-" + Guid.NewGuid().ToString("N")[..8],
            TemplateVersion = "1.0.0",
            AccessTokenHash = "hash",
            DeliverySettingsJson = settingsJson,
            InviterId = inviterId,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Campaigns.Add(c);
        return c;
    }

    public static Guest SeedGuest(AppDbContext db, Guid campaignId, string? email, string? phone, string name = "Amira")
    {
        var g = new Guest
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            Name = name,
            Email = email,
            PhoneE164 = phone,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Guests.Add(g);
        return g;
    }
}

/// <summary>A delivery provider test double that records calls and returns a preset result.</summary>
internal sealed class FakeProvider(string channel, bool succeeds) : IInviteDeliveryProvider
{
    public string Channel => channel;
    public List<InviteDeliveryMessage> Calls { get; } = new();

    public Task<DeliveryResult> SendAsync(InviteDeliveryMessage m, CancellationToken ct)
    {
        Calls.Add(m);
        return Task.FromResult(succeeds
            ? DeliveryResult.Ok($"{channel}-{Calls.Count}")
            : DeliveryResult.Fail("boom"));
    }
}
