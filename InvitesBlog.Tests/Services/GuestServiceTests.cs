using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using FluentValidation;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Guests;
using InvitesBlog.Application.Exceptions.Campaigns;
using InvitesBlog.Application.Exceptions.Guests;
using InvitesBlog.Application.Guests;
using InvitesBlog.Application.Phones;
using InvitesBlog.Application.Security;
using InvitesBlog.Application.Services.Guests;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

public class GuestServiceTests
{
    private readonly ICurrentUser _currentUser = Substitute.For<ICurrentUser>();
    private readonly ICampaignRepository _campaigns = Substitute.For<ICampaignRepository>();
    private readonly IGuestRepository _guests = Substitute.For<IGuestRepository>();
    private readonly IInviteRepository _invites = Substitute.For<IInviteRepository>();
    private readonly ISuppressionRepository _suppression = Substitute.For<ISuppressionRepository>();
    private readonly IRepository<UploadedGuestFile> _uploads = Substitute.For<IRepository<UploadedGuestFile>>();
    private readonly IRepository<DeliveryAttempt> _attempts = Substitute.For<IRepository<DeliveryAttempt>>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private IValidator<ConfirmUploadRequest> _confirmV = TestData.PassingValidator<ConfirmUploadRequest>();

    private GuestService Sut() => new(
        _currentUser, _campaigns, _guests, _invites, _suppression, _uploads, _attempts, _uow,
        new GuestUploadParser(new PhoneNormalizer()), new PhoneNormalizer(), _confirmV);

    private Campaign Own(CampaignStatus status = CampaignStatus.Draft, int paidCapacity = 100)
    {
        var c = TestData.Campaign(status: status, paidCapacity: paidCapacity);
        _currentUser.CampaignId.Returns(c.Id);
        _campaigns.GetByIdAsync(c.Id, Arg.Any<CancellationToken>()).Returns(c);
        // default: empty guest tables for dedupe queries
        _guests.Query().Returns(Array.Empty<Guest>().AsAsyncQueryable());
        _suppression.ListHashesAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<string>());
        return c;
    }

    private static MemoryStream ValidXlsx()
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Guests");
        ws.Cell(1, 1).Value = "name"; ws.Cell(1, 2).Value = "email"; ws.Cell(1, 3).Value = "phone";
        ws.Cell(2, 1).Value = "Alice"; ws.Cell(2, 2).Value = "alice@test.com"; ws.Cell(2, 3).Value = "+9607777777";
        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        return ms;
    }

    // ----- Upload -----

    [Fact]
    public async Task Upload_mismatched_campaign_throws_AccessDenied()
    {
        _currentUser.CampaignId.Returns(Guid.NewGuid());
        using var s = new MemoryStream(new byte[] { 1, 2, 3 });
        await Assert.ThrowsAsync<CampaignAccessDeniedException>(
            () => Sut().UploadAsync(Guid.NewGuid(), s, "g.xlsx", "MV"));
    }

    [Fact]
    public async Task Upload_rejected_file_throws()
    {
        var c = Own();
        using var garbage = new MemoryStream(Encoding.UTF8.GetBytes("this is not an excel file"));
        await Assert.ThrowsAsync<GuestFileRejectedException>(
            () => Sut().UploadAsync(c.Id, garbage, "g.xlsx", "MV"));
    }

    [Fact]
    public async Task Upload_success_persists_parse_result()
    {
        var c = Own();
        using var xlsx = ValidXlsx();

        var summary = await Sut().UploadAsync(c.Id, xlsx, "guests.xlsx", "MV");

        Assert.NotEqual(Guid.Empty, summary.UploadId);
        Assert.Equal(1, summary.ValidRows);
        await _uploads.Received(1).AddAsync(Arg.Any<UploadedGuestFile>(), Arg.Any<CancellationToken>());
    }

    // ----- ExportErrorsCsv -----

    [Fact]
    public async Task ExportErrors_unknown_upload_throws()
    {
        var c = Own();
        _uploads.FirstOrDefaultAsync(Arg.Any<System.Linq.Expressions.Expression<Func<UploadedGuestFile, bool>>>(), Arg.Any<CancellationToken>())
            .Returns((UploadedGuestFile?)null);
        await Assert.ThrowsAsync<UploadNotFoundException>(() => Sut().ExportErrorsCsvAsync(c.Id, Guid.NewGuid()));
    }

    [Fact]
    public async Task ExportErrors_success_returns_csv()
    {
        var c = Own();
        var parsed = new List<ParsedGuest> { new("a@test.com", "+9607777777", "+9607777777", "Alice", "guest", "female", "{}") };
        var upload = new UploadedGuestFile { Id = Guid.NewGuid(), CampaignId = c.Id, ResultJson = JsonSerializer.Serialize(parsed) };
        _uploads.FirstOrDefaultAsync(Arg.Any<System.Linq.Expressions.Expression<Func<UploadedGuestFile, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(upload);

        var bytes = await Sut().ExportErrorsCsvAsync(c.Id, upload.Id);

        var csv = Encoding.UTF8.GetString(bytes);
        Assert.Contains("email,phone,name,role,gender", csv);
        Assert.Contains("a@test.com", csv);
    }

    // ----- ConfirmUpload -----

    [Fact]
    public async Task Confirm_mismatched_campaign_throws_AccessDenied()
    {
        _currentUser.CampaignId.Returns(Guid.NewGuid());
        await Assert.ThrowsAsync<CampaignAccessDeniedException>(
            () => Sut().ConfirmUploadAsync(Guid.NewGuid(), new ConfirmUploadRequest(Guid.NewGuid())));
    }

    [Fact]
    public async Task Confirm_unknown_upload_throws()
    {
        var c = Own();
        _uploads.Query(true).Returns(Array.Empty<UploadedGuestFile>().AsAsyncQueryable());
        await Assert.ThrowsAsync<UploadNotFoundException>(
            () => Sut().ConfirmUploadAsync(c.Id, new ConfirmUploadRequest(Guid.NewGuid())));
    }

    [Fact]
    public async Task Confirm_materializes_guests_and_respects_suppression()
    {
        var c = Own();
        var parsed = new List<ParsedGuest>
        {
            new("ok@test.com", null, null, "Ok Guest", "guest", "female", "{}"),
            new("blocked@test.com", null, null, "Blocked", "guest", "male", "{}")
        };
        var upload = new UploadedGuestFile { Id = Guid.NewGuid(), CampaignId = c.Id, ResultJson = JsonSerializer.Serialize(parsed) };
        _uploads.Query(true).Returns(new[] { upload }.AsAsyncQueryable());
        _suppression.ListHashesAsync(Arg.Any<CancellationToken>())
            .Returns(new[] { TokenService.HashContact("blocked@test.com") });

        var res = await Sut().ConfirmUploadAsync(c.Id, new ConfirmUploadRequest(upload.Id));

        Assert.Equal(1, res.Added);       // only the non-suppressed guest
        Assert.Equal(1, res.Suppressed);
        await _guests.Received(1).AddAsync(Arg.Any<Guest>(), Arg.Any<CancellationToken>());
        Assert.True(upload.Confirmed);
    }

    // ----- AddGuest -----

    [Fact]
    public async Task AddGuest_mismatched_campaign_throws_AccessDenied()
    {
        _currentUser.CampaignId.Returns(Guid.NewGuid());
        await Assert.ThrowsAsync<CampaignAccessDeniedException>(
            () => Sut().AddGuestAsync(Guid.NewGuid(), new AddGuestRequest("a@test.com", null, "A", null, null, null)));
    }

    [Fact]
    public async Task AddGuest_no_contact_throws()
    {
        var c = Own();
        await Assert.ThrowsAsync<GuestContactRequiredException>(
            () => Sut().AddGuestAsync(c.Id, new AddGuestRequest(null, null, "No Contact", null, null, null)));
    }

    [Fact]
    public async Task AddGuest_success_materializes_and_reports_capacity()
    {
        var c = Own(paidCapacity: 100);
        _guests.CountByCampaignAsync(c.Id, Arg.Any<CancellationToken>()).Returns(1);

        var outcome = await Sut().AddGuestAsync(c.Id, new AddGuestRequest("new@test.com", null, "New", "guest", "female", null));

        Assert.Equal(1, outcome.Response.Added);
        Assert.Equal(1, outcome.Response.GuestCount);
        Assert.False(outcome.Response.NeedsTopUp);
        Assert.Null(outcome.DispatchGuestId); // Draft campaign → not dispatched immediately
        await _guests.Received(1).AddAsync(Arg.Any<Guest>(), Arg.Any<CancellationToken>());
    }

    // ----- UpdateGuest -----

    [Fact]
    public async Task UpdateGuest_mismatched_campaign_throws_AccessDenied()
    {
        _currentUser.CampaignId.Returns(Guid.NewGuid());
        await Assert.ThrowsAsync<CampaignAccessDeniedException>(
            () => Sut().UpdateGuestAsync(Guid.NewGuid(), Guid.NewGuid(), new UpdateGuestRequest(null, null, "X", null, null, null)));
    }

    [Fact]
    public async Task UpdateGuest_unknown_guest_throws()
    {
        var c = Own();
        _guests.Query(true).Returns(Array.Empty<Guest>().AsAsyncQueryable());
        await Assert.ThrowsAsync<GuestNotFoundException>(
            () => Sut().UpdateGuestAsync(c.Id, Guid.NewGuid(), new UpdateGuestRequest(null, null, "X", null, null, null)));
    }

    [Fact]
    public async Task UpdateGuest_success_updates_fields()
    {
        var c = Own();
        var guest = TestData.Guest(c.Id);
        _guests.Query(true).Returns(new[] { guest }.AsAsyncQueryable());

        await Sut().UpdateGuestAsync(c.Id, guest.Id, new UpdateGuestRequest("changed@test.com", null, "Changed", "vip", "male", null));

        Assert.Equal("changed@test.com", guest.Email);
        Assert.Equal("Changed", guest.Name);
        Assert.Equal("vip", guest.Role);
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ----- PrepareResend -----

    [Fact]
    public async Task Resend_mismatched_campaign_throws_AccessDenied()
    {
        _currentUser.CampaignId.Returns(Guid.NewGuid());
        await Assert.ThrowsAsync<CampaignAccessDeniedException>(
            () => Sut().PrepareResendAsync(Guid.NewGuid(), Guid.NewGuid()));
    }

    [Fact]
    public async Task Resend_unknown_guest_throws()
    {
        var c = Own();
        _guests.AnyAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Guest, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(false);
        await Assert.ThrowsAsync<GuestNotFoundException>(() => Sut().PrepareResendAsync(c.Id, Guid.NewGuid()));
    }

    [Fact]
    public async Task Resend_over_limit_throws()
    {
        var c = Own();
        var guestId = Guid.NewGuid();
        var invite = TestData.Invite(c.Id, guestId);
        _guests.AnyAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Guest, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _invites.Query().Returns(new[] { invite }.AsAsyncQueryable());
        var recent = Enumerable.Range(0, 3).Select(_ => new DeliveryAttempt
        {
            Id = Guid.NewGuid(), InviteId = invite.Id, Channel = "sms", RecipientAddress = "x",
            IsOtp = false, AttemptedAt = DateTimeOffset.UtcNow
        }).ToArray();
        _attempts.Query().Returns(recent.AsAsyncQueryable());

        await Assert.ThrowsAsync<ResendLimitExceededException>(() => Sut().PrepareResendAsync(c.Id, guestId));
    }

    [Fact]
    public async Task Resend_under_limit_succeeds()
    {
        var c = Own();
        var guestId = Guid.NewGuid();
        var invite = TestData.Invite(c.Id, guestId);
        _guests.AnyAsync(Arg.Any<System.Linq.Expressions.Expression<Func<Guest, bool>>>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _invites.Query().Returns(new[] { invite }.AsAsyncQueryable());
        _attempts.Query().Returns(Array.Empty<DeliveryAttempt>().AsAsyncQueryable());

        await Sut().PrepareResendAsync(c.Id, guestId); // should not throw
    }
}
