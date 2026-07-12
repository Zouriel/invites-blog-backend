using FluentValidation;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Campaigns;
using InvitesBlog.Application.Exceptions.Campaigns;
using InvitesBlog.Application.Phones;
using InvitesBlog.Application.Services.Campaigns;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

public class CampaignServiceTests
{
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly ICampaignRepository _campaigns = Substitute.For<ICampaignRepository>();
    private readonly IInviterRepository _inviters = Substitute.For<IInviterRepository>();
    private readonly IGuestRepository _guests = Substitute.For<IGuestRepository>();
    private readonly IInviteRepository _invites = Substitute.For<IInviteRepository>();
    private readonly IPaymentRepository _payments = Substitute.For<IPaymentRepository>();
    private readonly ITemplateRepository _templates = Substitute.For<ITemplateRepository>();
    private readonly IRepository<RsvpResponse> _rsvp = Substitute.For<IRepository<RsvpResponse>>();
    private readonly IRepository<DeliveryAttempt> _attempts = Substitute.For<IRepository<DeliveryAttempt>>();
    private readonly IRepository<CampaignAsset> _assets = Substitute.For<IRepository<CampaignAsset>>();
    private readonly IRepository<UploadedGuestFile> _uploads = Substitute.For<IRepository<UploadedGuestFile>>();
    private readonly IRepository<AuditLog> _auditLogs = Substitute.For<IRepository<AuditLog>>();
    private readonly IRepository<Refund> _refunds = Substitute.For<IRepository<Refund>>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private readonly IEmailSender _email = Substitute.For<IEmailSender>();
    private readonly IStorageService _storage = Substitute.For<IStorageService>();
    private readonly IPaymentProvider _provider = Substitute.For<IPaymentProvider>();
    private readonly IConfiguration _config = Substitute.For<IConfiguration>();

    private IValidator<CreateCampaignRequest> _createV = TestData.PassingValidator<CreateCampaignRequest>();
    private IValidator<UpdateContentRequest> _contentV = TestData.PassingValidator<UpdateContentRequest>();
    private IValidator<UpdateVenueRequest> _venueV = TestData.PassingValidator<UpdateVenueRequest>();
    private IValidator<UpdateInviterRequest> _inviterV = TestData.PassingValidator<UpdateInviterRequest>();
    private IValidator<UpdateDeliverySettingsRequest> _deliveryV = TestData.PassingValidator<UpdateDeliverySettingsRequest>();

    private CampaignService Sut() => new(
        _currentUser, _campaigns, _inviters, _guests, _invites, _payments, _templates,
        _rsvp, _attempts, _assets, _uploads, _auditLogs, _refunds, _uow, _email, _storage, _provider,
        new PhoneNormalizer(), _config, _createV, _contentV, _venueV, _inviterV, _deliveryV);

    private void Own(Campaign c)
    {
        _currentUser.CampaignId.Returns(c.Id);
        _campaigns.GetByIdAsync(c.Id, Arg.Any<CancellationToken>()).Returns(c);
    }

    // ----- Create -----

    [Fact]
    public async Task Create_validation_failure_throws()
    {
        _createV = TestData.FailingValidator<CreateCampaignRequest>();
        await Assert.ThrowsAsync<ValidationException>(
            () => Sut().CreateAsync(new CreateCampaignRequest(Guid.NewGuid(), "")));
    }

    [Fact]
    public async Task Create_unknown_template_throws()
    {
        _templates.GetActiveByIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Template?)null);
        await Assert.ThrowsAsync<UnknownTemplateException>(
            () => Sut().CreateAsync(new CreateCampaignRequest(Guid.NewGuid(), "My Event")));
    }

    [Fact]
    public async Task Create_success_returns_token_and_persists_draft()
    {
        var template = TestData.Template();
        _templates.GetActiveByIdAsync(template.Id, Arg.Any<CancellationToken>()).Returns(template);

        var res = await Sut().CreateAsync(new CreateCampaignRequest(template.Id, "My Event"));

        Assert.Equal(CampaignStatus.Draft.ToString(), res.Status);
        Assert.False(string.IsNullOrEmpty(res.AccessToken));
        await _campaigns.Received(1).AddAsync(Arg.Any<Campaign>(), Arg.Any<CancellationToken>());
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ----- Ownership enforcement (LoadOwnedAsync) -----

    [Fact]
    public async Task UpdateContent_mismatched_campaign_throws_AccessDenied()
    {
        _currentUser.CampaignId.Returns(Guid.NewGuid());
        await Assert.ThrowsAsync<CampaignAccessDeniedException>(
            () => Sut().UpdateContentAsync(Guid.NewGuid(), new UpdateContentRequest(null, null, null, null, null, null, null)));
    }

    [Fact]
    public async Task UpdateContent_missing_campaign_throws_NotFound()
    {
        var id = Guid.NewGuid();
        _currentUser.CampaignId.Returns(id);
        _campaigns.GetByIdAsync(id, Arg.Any<CancellationToken>()).Returns((Campaign?)null);
        await Assert.ThrowsAsync<CampaignNotFoundException>(
            () => Sut().UpdateContentAsync(id, new UpdateContentRequest(null, null, null, null, null, null, null)));
    }

    [Fact]
    public async Task UpdateContent_success_applies_fields()
    {
        var c = TestData.Campaign();
        Own(c);
        var req = new UpdateContentRequest("{\"a\":1}", null, null, true, null, null, "birthday");

        await Sut().UpdateContentAsync(c.Id, req);

        Assert.Equal("{\"a\":1}", c.CustomContentJson);
        Assert.True(c.IsSensitive);
        Assert.Equal("birthday", c.EventType);
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task UpdateVenue_success_writes_venue_block()
    {
        var c = TestData.Campaign();
        Own(c);
        var req = new UpdateVenueRequest("hall", "Grand Ballroom", "1 St", null, "City", null, null, null, null);

        await Sut().UpdateVenueAsync(c.Id, req);

        Assert.Contains("Grand Ballroom", c.CustomContentJson);
        Assert.Equal("hall", c.EventType);
    }

    [Fact]
    public async Task UpdateInviter_new_inviter_created_and_email_sent()
    {
        var c = TestData.Campaign();
        Own(c);
        _inviters.GetByEmailAsync("host@test.com", Arg.Any<CancellationToken>()).Returns((Inviter?)null);
        var req = new UpdateInviterRequest("Host", "+9607777777", "Host@Test.com", "Org", null, null, "MV");

        await Sut().UpdateInviterAsync(c.Id, req, "access-tok");

        await _inviters.Received(1).AddAsync(Arg.Any<Inviter>(), Arg.Any<CancellationToken>());
        await _email.Received(1).SendAsync(Arg.Is<EmailMessage>(m => m.To == "host@test.com"), Arg.Any<CancellationToken>());
        Assert.NotNull(c.InviterId);
    }

    [Fact]
    public async Task UpdateInviter_existing_inviter_updated()
    {
        var c = TestData.Campaign();
        Own(c);
        var existing = new Inviter { Id = Guid.NewGuid(), Email = "host@test.com", Name = "Old", PhoneE164 = "+9601111111" };
        _inviters.GetByEmailAsync("host@test.com", Arg.Any<CancellationToken>()).Returns(existing);
        var req = new UpdateInviterRequest("New Name", "+9607777777", "host@test.com", null, null, null, "MV");

        await Sut().UpdateInviterAsync(c.Id, req, "access-tok");

        _inviters.Received(1).Update(existing);
        Assert.Equal("New Name", existing.Name);
        Assert.Equal(existing.Id, c.InviterId);
    }

    [Fact]
    public async Task UpdateDelivery_mismatched_campaign_throws_AccessDenied()
    {
        _currentUser.CampaignId.Returns(Guid.NewGuid());
        await Assert.ThrowsAsync<CampaignAccessDeniedException>(
            () => Sut().UpdateDeliverySettingsAsync(Guid.NewGuid(), new UpdateDeliverySettingsRequest("{}")));
    }

    // ----- Summary / pricing -----

    [Fact]
    public async Task GetSummary_mismatched_campaign_throws_AccessDenied()
    {
        _currentUser.CampaignId.Returns(Guid.NewGuid());
        await Assert.ThrowsAsync<CampaignAccessDeniedException>(() => Sut().GetSummaryAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task GetSummary_success_returns_dto_with_guestcount_and_price()
    {
        var template = TestData.Template();
        var c = TestData.Campaign(templateId: template.Id);
        Own(c);
        _guests.CountByCampaignAsync(c.Id, Arg.Any<CancellationToken>()).Returns(12);
        _templates.GetByIdAsync(template.Id, Arg.Any<CancellationToken>()).Returns(template);

        var dto = await Sut().GetSummaryAsync(c.Id);

        Assert.Equal(c.Title, dto.Title);
        Assert.Equal(12, dto.GuestCount);
        Assert.NotNull(dto.Price);
        Assert.Equal(template.Name, dto.Template!.Name);
    }

    [Fact]
    public async Task GetPricing_uses_supplied_count()
    {
        var c = TestData.Campaign();
        Own(c);
        var price = await Sut().GetPricingAsync(c.Id, inviteCount: 70);
        Assert.NotNull(price);
    }

    // ----- Resend link -----

    [Fact]
    public async Task ResendLink_unknown_email_sends_nothing()
    {
        _inviters.GetByEmailAsync("ghost@test.com", Arg.Any<CancellationToken>()).Returns((Inviter?)null);
        await Sut().ResendLinkAsync(new ResendLinkRequest("Ghost@Test.com"));
        await _email.DidNotReceive().SendAsync(Arg.Any<EmailMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ResendLink_known_email_regenerates_and_emails_links()
    {
        var inviter = new Inviter { Id = Guid.NewGuid(), Email = "host@test.com", Name = "Host", PhoneE164 = "+9607777777" };
        var owned = TestData.Campaign();
        owned.InviterId = inviter.Id;
        _inviters.GetByEmailAsync("host@test.com", Arg.Any<CancellationToken>()).Returns(inviter);
        _campaigns.Query(true).Returns(new[] { owned }.AsAsyncQueryable());

        await Sut().ResendLinkAsync(new ResendLinkRequest("host@test.com"));

        Assert.False(string.IsNullOrEmpty(owned.DashboardTokenHash));
        await _email.Received(1).SendAsync(Arg.Is<EmailMessage>(m => m.To == "host@test.com"), Arg.Any<CancellationToken>());
    }

    // ----- Dashboard -----

    [Fact]
    public async Task Dashboard_missing_token_throws()
    {
        await Assert.ThrowsAsync<InvalidDashboardTokenException>(() => Sut().GetDashboardAsync(Guid.NewGuid(), null));
    }

    [Fact]
    public async Task Dashboard_bad_token_throws()
    {
        _campaigns.GetByDashboardTokenHashAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((Campaign?)null);
        await Assert.ThrowsAsync<InvalidDashboardTokenException>(() => Sut().GetDashboardAsync(Guid.NewGuid(), "bad"));
    }

    [Fact]
    public async Task Dashboard_success_builds_report()
    {
        var c = TestData.Campaign();
        var guest = TestData.Guest(c.Id);
        var invite = TestData.Invite(c.Id, guest.Id, status: InviteStatus.Sent, rsvp: RsvpStatus.Going);
        _campaigns.GetByDashboardTokenHashAsync(c.Id, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(c);
        _guests.ListByCampaignAsync(c.Id, true, Arg.Any<CancellationToken>()).Returns(new[] { guest });
        _invites.ListByCampaignAsync(c.Id, Arg.Any<CancellationToken>()).Returns(new[] { invite });
        _attempts.Query().Returns(Array.Empty<DeliveryAttempt>().AsAsyncQueryable());

        var res = await Sut().GetDashboardAsync(c.Id, "good");

        Assert.Equal(1, res.Report.Total);
        Assert.Equal(1, res.Report.Sent);
        Assert.Equal(1, res.Report.Rsvp.Going);
        Assert.Single(res.Guests);
    }

    // ----- Cancel -----

    [Fact]
    public async Task Cancel_mismatched_campaign_throws_AccessDenied()
    {
        _currentUser.CampaignId.Returns(Guid.NewGuid());
        await Assert.ThrowsAsync<CampaignAccessDeniedException>(() => Sut().CancelAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Cancel_post_dispatch_sets_cancelled_with_notice()
    {
        var c = TestData.Campaign(status: CampaignStatus.Dispatched);
        Own(c);

        var res = await Sut().CancelAsync(c.Id);

        Assert.True(res.Cancelled);
        Assert.False(res.Refunded);
        Assert.NotNull(res.Note);
        Assert.Equal(CampaignStatus.Cancelled, c.Status);
    }

    [Fact]
    public async Task Cancel_pre_dispatch_no_payments_cancels_without_refund()
    {
        var c = TestData.Campaign(status: CampaignStatus.Draft);
        Own(c);
        _payments.Query(true).Returns(Array.Empty<Payment>().AsAsyncQueryable());

        var res = await Sut().CancelAsync(c.Id);

        Assert.True(res.Cancelled);
        Assert.False(res.Refunded);
        Assert.Equal(CampaignStatus.Cancelled, c.Status);
    }

    [Fact]
    public async Task Cancel_pre_dispatch_with_paid_payment_refunds()
    {
        var c = TestData.Campaign(status: CampaignStatus.Draft);
        Own(c);
        var paid = TestData.Payment(c.Id, status: PaymentStatus.Paid);
        paid.ProviderPaymentId = "pay_1";
        _payments.Query(true).Returns(new[] { paid }.AsAsyncQueryable());
        _provider.RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>())
            .Returns(new RefundResult(true, "ref_1", null));

        var res = await Sut().CancelAsync(c.Id);

        Assert.True(res.Cancelled);
        Assert.True(res.Refunded);
        await _refunds.Received(1).AddAsync(Arg.Any<Refund>(), Arg.Any<CancellationToken>());
        await _provider.Received(1).RefundAsync(Arg.Any<RefundRequest>(), Arg.Any<CancellationToken>());
        Assert.Equal(PaymentStatus.Refunded, paid.Status);
    }

    // ----- Delete -----

    [Fact]
    public async Task Delete_mismatched_campaign_throws_AccessDenied()
    {
        _currentUser.CampaignId.Returns(Guid.NewGuid());
        await Assert.ThrowsAsync<CampaignAccessDeniedException>(() => Sut().DeleteAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task Delete_success_removes_campaign_and_audits()
    {
        var c = TestData.Campaign();
        Own(c);
        var guest = TestData.Guest(c.Id);
        var invite = TestData.Invite(c.Id, guest.Id);
        _guests.ListByCampaignAsync(c.Id, true, Arg.Any<CancellationToken>()).Returns(new[] { guest });
        _invites.ListByCampaignAsync(c.Id, Arg.Any<CancellationToken>()).Returns(new[] { invite });
        _attempts.ListAsync(Arg.Any<System.Linq.Expressions.Expression<Func<DeliveryAttempt, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<DeliveryAttempt>());
        _rsvp.ListAsync(Arg.Any<System.Linq.Expressions.Expression<Func<RsvpResponse, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<RsvpResponse>());
        _uploads.ListAsync(Arg.Any<System.Linq.Expressions.Expression<Func<UploadedGuestFile, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<UploadedGuestFile>());
        _assets.ListAsync(Arg.Any<System.Linq.Expressions.Expression<Func<CampaignAsset, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<CampaignAsset>());

        var res = await Sut().DeleteAsync(c.Id);

        Assert.True(res.Deleted);
        _campaigns.Received(1).Remove(c);
        await _auditLogs.Received(1).AddAsync(Arg.Any<AuditLog>(), Arg.Any<CancellationToken>());
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }
}
