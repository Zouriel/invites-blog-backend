using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Plans;
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

/// <summary>Venue is a monthly subscription, bought and renewed like a month of Premium.</summary>
public class VenueSubscriptionTests
{
    private readonly AppUser _user = new() { Id = Guid.NewGuid(), Email = "resort@test.com" };
    private readonly List<Payment> _payments = [];

    private BillingService Service(bool paymentsOn = false)
    {
        var me = Substitute.For<ICurrentUser>();
        me.UserId.Returns(_user.Id);
        var users = Substitute.For<IRepository<AppUser>>();
        users.Query(Arg.Any<bool>()).Returns(_ => new[] { _user }.AsAsyncQueryable());
        var payments = Substitute.For<IPaymentRepository>();
        payments.Query(Arg.Any<bool>()).Returns(_ => _payments.AsAsyncQueryable());
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Payments:Enabled"] = paymentsOn ? "true" : "false",
        }).Build();
        return new BillingService(
            me, config, TestData.PriceBook(), TestData.FreePlans(), Substitute.For<ICampaignOwnershipService>(),
            Substitute.For<ICampaignRepository>(), users, payments, Substitute.For<IPassOfferService>(),
            Substitute.For<IPaymentProvider>(), TestData.Allowance(), Substitute.For<IMediaBucketService>(),
            Substitute.For<IRepository<AuditLog>>(), Substitute.For<IUnitOfWork>());
    }

    private Payment Paid()
    {
        var p = new Payment
        {
            Id = Guid.NewGuid(), UserId = _user.Id, Kind = PaymentKind.VenueMonthly, Quantity = 1,
            Amount = PlanCatalog.VenueMonthly, Currency = "MVR", Status = PaymentStatus.Paid, CreatedAt = DateTimeOffset.UtcNow,
        };
        _payments.Add(p);
        return p;
    }

    [Fact]
    public void Venue_is_sold_by_the_month_at_the_price_book_price()
    {
        var venue = PlanCatalog.Describe(Prices.Defaults with { VenueMonthly = 2500m }).Plans.Single(p => p.Kind == "Venue");

        Assert.Equal(2500m, venue.Price);
        Assert.Equal("per month", venue.Billing);
        Assert.Equal(PaymentKind.VenueMonthly, BillingService.ParseItem("venue-monthly"));
        Assert.NotEmpty((Prices.Defaults with { VenueMonthly = 0 }).Problems());
    }

    [Fact]
    public async Task A_paid_month_makes_the_account_a_venue_for_a_month()
    {
        await Service().FulfilAsync(Paid().Id);

        Assert.Equal(SubscriptionTier.Venue, _user.SubscriptionTier);
        Assert.InRange(_user.SubscriptionEndsAt!.Value, DateTimeOffset.UtcNow.AddMonths(1).AddMinutes(-1), DateTimeOffset.UtcNow.AddMonths(1).AddMinutes(1));
    }

    [Fact]
    public async Task Another_month_follows_the_one_running()
    {
        var ends = DateTimeOffset.UtcNow.AddDays(10);
        _user.SubscriptionTier = SubscriptionTier.Venue;
        _user.SubscriptionEndsAt = ends;

        await Service().FulfilAsync(Paid().Id);

        Assert.Equal(ends.AddMonths(1), _user.SubscriptionEndsAt);
    }

    [Fact]
    public async Task A_venue_given_with_no_end_stays_open_ended()
    {
        _user.SubscriptionTier = SubscriptionTier.Venue;
        _user.SubscriptionEndsAt = null;

        await Service().FulfilAsync(Paid().Id);

        Assert.Null(_user.SubscriptionEndsAt);
    }

    [Fact]
    public async Task A_running_premium_is_not_swapped_for_a_venue_at_checkout()
    {
        _user.SubscriptionTier = SubscriptionTier.Premium;
        _user.SubscriptionEndsAt = DateTimeOffset.UtcNow.AddDays(20);

        var ex = await Assert.ThrowsAsync<BusinessRuleException>(() => Service().CheckoutAsync(new CheckoutRequest("venue-monthly", null, 1, null)));
        Assert.Equal("billing_premium_account", ex.ErrorCode);
    }

    [Fact]
    public async Task While_payments_are_off_the_checkout_points_at_the_venue_enquiry()
    {
        var result = await Service().CheckoutAsync(new CheckoutRequest("venue-monthly", null, 1, null));

        Assert.False(result.Available);
        Assert.Equal("venue", result.InquireTopic);
    }
}
