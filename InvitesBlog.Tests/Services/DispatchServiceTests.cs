using InvitesBlog.Application.Abstractions;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using InvitesBlog.Infrastructure.Delivery;
using InvitesBlog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>
/// When a dedicated template is spent. It used to be marked the moment someone started a campaign,
/// which meant a draft abandoned before anything was sent burned a single-use template for good —
/// leaving the person it was made for unable to start again. It is now spent only once invitations
/// actually reach guests.
/// </summary>
public class DispatchServiceTests
{
    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase($"dispatch-{Guid.NewGuid()}")
            .Options);

    private static IConfiguration Config() =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Urls:InviteeBase"] = "https://me.invites.blog"
        }).Build();

    /// <summary>An email provider whose sends either all succeed or all fail.</summary>
    private static IInviteDeliveryProvider EmailProvider(bool succeeds)
    {
        var p = Substitute.For<IInviteDeliveryProvider>();
        p.Channel.Returns("email");
        p.SendAsync(Arg.Any<InviteDeliveryMessage>(), Arg.Any<CancellationToken>())
            .Returns(succeeds ? DeliveryResult.Ok("msg-1") : DeliveryResult.Fail("nope"));
        return p;
    }

    private static async Task<(AppDbContext Db, Guid CampaignId, Guid TemplateId)> SeedAsync(
        bool guestHasEmail = true)
    {
        var db = NewDb();
        var template = new Template
        {
            Id = Guid.NewGuid(), Name = "Gilded Hour", Slug = "gilded-hour", Version = "1.0.0",
            Category = "Birthday", Description = "d", PreviewImageUrl = "p", SceneJson = "{}",
            ManifestJson = "{}", PackageUrl = "/assets/x/", IsActive = true,
            Visibility = TemplateVisibility.Dedicated, IsUsed = false,
        };
        var campaign = new Campaign
        {
            Id = Guid.NewGuid(), TemplateId = template.Id, TemplateVersion = "1.0.0",
            AccessTokenHash = "h", Title = "T", Slug = "t", Status = CampaignStatus.Paid,
            DeliverySettingsJson = "{\"channels\":[\"email\"]}",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var guest = new Guest
        {
            Id = Guid.NewGuid(), CampaignId = campaign.Id, Name = "Ahmed",
            Email = guestHasEmail ? "ahmed@example.com" : null,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Templates.Add(template);
        db.Campaigns.Add(campaign);
        db.Guests.Add(guest);
        await db.SaveChangesAsync();
        return (db, campaign.Id, template.Id);
    }

    private static DispatchService Sut(AppDbContext db, IInviteDeliveryProvider provider) =>
        new(db, new[] { provider }, Config(), NullLogger<DispatchService>.Instance);

    [Fact]
    public async Task A_delivered_invitation_spends_the_dedicated_template()
    {
        var (db, campaignId, templateId) = await SeedAsync();

        await Sut(db, EmailProvider(succeeds: true)).DispatchCampaignAsync(campaignId);

        var template = await db.Templates.FirstAsync(t => t.Id == templateId);
        Assert.True(template.IsUsed);
    }

    [Fact]
    public async Task A_failed_dispatch_leaves_the_template_unspent()
    {
        // Nothing reached anyone, so the template must still be startable.
        var (db, campaignId, templateId) = await SeedAsync();

        await Sut(db, EmailProvider(succeeds: false)).DispatchCampaignAsync(campaignId);

        var template = await db.Templates.FirstAsync(t => t.Id == templateId);
        Assert.False(template.IsUsed);
    }

    [Fact]
    public async Task A_guest_with_no_reachable_contact_does_not_spend_the_template()
    {
        // The guest is skipped rather than failed, but still nothing was sent.
        var (db, campaignId, templateId) = await SeedAsync(guestHasEmail: false);

        await Sut(db, EmailProvider(succeeds: true)).DispatchCampaignAsync(campaignId);

        var template = await db.Templates.FirstAsync(t => t.Id == templateId);
        Assert.False(template.IsUsed);
    }

    [Fact]
    public async Task A_public_template_is_never_marked_used()
    {
        // Only dedicated templates are single-use; a public one stays freely selectable forever.
        var (db, campaignId, templateId) = await SeedAsync();
        var template = await db.Templates.FirstAsync(t => t.Id == templateId);
        template.Visibility = TemplateVisibility.Public;
        await db.SaveChangesAsync();

        await Sut(db, EmailProvider(succeeds: true)).DispatchCampaignAsync(campaignId);

        var after = await db.Templates.FirstAsync(t => t.Id == templateId);
        Assert.False(after.IsUsed);
    }
}
