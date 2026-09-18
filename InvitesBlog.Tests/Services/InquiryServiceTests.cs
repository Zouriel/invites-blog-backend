using FluentValidation;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Inquiries;
using InvitesBlog.Application.Filters.Inquiries;
using InvitesBlog.Application.Services.Inquiries;
using InvitesBlog.Domain.Entities;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

public class InquiryServiceTests
{
    private readonly IRepository<Inquiry> _inquiries = Substitute.For<IRepository<Inquiry>>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();
    private IValidator<SubmitInquiryRequest> _submitV = TestData.PassingValidator<SubmitInquiryRequest>();

    private InquiryService Sut() => new(_inquiries, _uow, _submitV);

    private static Inquiry Inquiry(bool attended = false) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Aisha",
        Email = "aisha@test.com",
        Occasion = "Wedding",
        Message = "We'd love a red-curtain theme.",
        HasAttended = attended,
        CreatedAt = DateTimeOffset.UtcNow
    };

    [Fact]
    public async Task Submit_validation_failure_throws()
    {
        _submitV = TestData.FailingValidator<SubmitInquiryRequest>();
        await Assert.ThrowsAsync<ValidationException>(
            () => Sut().SubmitAsync(new SubmitInquiryRequest("", "", "", "")));
    }

    [Fact]
    public async Task Submit_creates_inquiry_and_lowercases_email()
    {
        Inquiry? captured = null;
        await _inquiries.AddAsync(Arg.Do<Inquiry>(i => captured = i), Arg.Any<CancellationToken>());

        var res = await Sut().SubmitAsync(new SubmitInquiryRequest("Omar", "Omar@Test.com", "Engagement", "hi"));

        Assert.NotEqual(Guid.Empty, res.Id);
        Assert.NotNull(captured);
        Assert.Equal("omar@test.com", captured!.Email); // lowercased
        Assert.False(captured.HasAttended);
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task List_orders_unattended_first_then_oldest()
    {
        var oldAttended = Inquiry(attended: true); oldAttended.CreatedAt = DateTimeOffset.UtcNow.AddDays(-5);
        var newUnattended = Inquiry(); newUnattended.CreatedAt = DateTimeOffset.UtcNow.AddDays(-1);
        var oldUnattended = Inquiry(); oldUnattended.CreatedAt = DateTimeOffset.UtcNow.AddDays(-3);
        _inquiries.Query().Returns(new[] { oldAttended, newUnattended, oldUnattended }.AsAsyncQueryable());

        var page = await Sut().ListAsync(new InquiryFilter());

        // Unattended first (oldest→newest among them), then attended.
        Assert.Equal(3, page.TotalCount);
        Assert.Equal(new[] { oldUnattended.Id, newUnattended.Id, oldAttended.Id }, page.Items.Select(i => i.Id).ToArray());
    }

    [Fact]
    public async Task List_status_attended_filters_to_the_ones_already_met()
    {
        var newOne = Inquiry();
        var attended = Inquiry(attended: true);
        _inquiries.Query().Returns(new[] { newOne, attended }.AsAsyncQueryable());

        var page = await Sut().ListAsync(new InquiryFilter { Status = "attended" });

        Assert.Equal(1, page.TotalCount);
        Assert.Equal(attended.Id, page.Items[0].Id);
    }

    [Fact]
    public async Task List_status_unattended_filters_to_new()
    {
        var newOne = Inquiry();
        var attended = Inquiry(attended: true);
        _inquiries.Query().Returns(new[] { newOne, attended }.AsAsyncQueryable());

        var page = await Sut().ListAsync(new InquiryFilter { Status = "unattended" });

        Assert.Equal(1, page.TotalCount);
        Assert.Equal(newOne.Id, page.Items[0].Id);
    }

    [Fact]
    public async Task List_search_matches_name_email_or_occasion()
    {
        var a = Inquiry(); a.Name = "Aisha"; a.Email = "aisha@test.com"; a.Occasion = "Wedding";
        var b = Inquiry(); b.Name = "Bilal"; b.Email = "bilal@test.com"; b.Occasion = "Birthday";
        _inquiries.Query().Returns(new[] { a, b }.AsAsyncQueryable());

        var page = await Sut().ListAsync(new InquiryFilter { Search = "wedding" });

        Assert.Equal(1, page.TotalCount);
        Assert.Equal(a.Id, page.Items[0].Id);
    }

    [Fact]
    public async Task Update_marking_attended_stamps_attended_at()
    {
        var i = Inquiry();
        _inquiries.GetByIdAsync(i.Id, Arg.Any<CancellationToken>()).Returns(i);

        await Sut().UpdateAsync(i.Id, new UpdateInquiryRequest("Blush & gold", "pinterest.com/x", null, HasAttended: true));

        Assert.True(i.HasAttended);
        Assert.NotNull(i.AttendedAt);
        Assert.Equal("Blush & gold", i.Colors);
        Assert.Equal("pinterest.com/x", i.References);
        Assert.Null(i.Notes);
    }
}
