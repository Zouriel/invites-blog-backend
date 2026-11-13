using InvitesBlog.Application.Plans;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Services.Billing;
using InvitesBlog.Application.Services.Campaigns;
using InvitesBlog.Application.Services.MediaBuckets;
using InvitesBlog.Application.Services.Payments;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>An extension is checked against the event's pass when it is paid, not only when checkout began.</summary>
public class PassExtensionFulfilTests
{
    private static readonly DateTimeOffset Until = DateTimeOffset.UtcNow.AddMonths(3);

    private static async Task<Campaign> FulfilAsync(EventPassKind passNow, PaymentKind paid)
    {
        var campaign = new Campaign { Id = Guid.NewGuid(), Title = "E", EventPass = passNow, EventPassUntil = Until, Status = CampaignStatus.Dispatched };
        var payment = new Payment
        {
            Id = Guid.NewGuid(), CampaignId = campaign.Id, UserId = Guid.NewGuid(), Kind = paid, Quantity = 1,
            Status = PaymentStatus.Paid, Provider = "Bml", Currency = "MVR", Description = "x", CreatedAt = DateTimeOffset.UtcNow,
        };
        var payments = Substitute.For<IPaymentRepository>();
        payments.Query(Arg.Any<bool>()).Returns(_ => new[] { payment }.AsAsyncQueryable());
        var campaigns = Substitute.For<ICampaignRepository>();
        campaigns.Query(Arg.Any<bool>()).Returns(_ => new[] { campaign }.AsAsyncQueryable());
        var users = Substitute.For<IRepository<AppUser>>();
        users.Query(Arg.Any<bool>()).Returns(_ => Array.Empty<AppUser>().AsAsyncQueryable());
        var service = new BillingService(
            Substitute.For<ICurrentUser>(), new ConfigurationBuilder().Build(), TestData.PriceBook(), TestData.FreePlans(),
            Substitute.For<ICampaignOwnershipService>(), campaigns, users, payments, Substitute.For<IPassOfferService>(),
            Substitute.For<IPaymentProvider>(), TestData.Allowance(), Substitute.For<IMediaBucketService>(),
            Substitute.For<IRepository<AuditLog>>(), Substitute.For<IUnitOfWork>());
        await service.FulfilAsync(payment.Id);
        return campaign;
    }

    [Fact]
    public async Task A_gold_extension_paid_after_the_event_moved_to_platinum_does_not_lengthen_platinum()
    {
        var campaign = await FulfilAsync(EventPassKind.Platinum, PaymentKind.GoldExtension);
        Assert.Equal(Until, campaign.EventPassUntil);
    }

    [Fact]
    public async Task The_matching_extension_lengthens_the_pass()
    {
        var campaign = await FulfilAsync(EventPassKind.Gold, PaymentKind.GoldExtension);
        Assert.True(campaign.EventPassUntil > Until);
    }
}
