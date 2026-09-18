using InvitesBlog.Application.Common;
using InvitesBlog.Application.Dtos.Inquiries;
using InvitesBlog.Application.Filters.Inquiries;

namespace InvitesBlog.Application.Services.Inquiries;

/// <summary>Requests for a made-to-order invitation: the public form, and the admin queue that reads them.</summary>
public interface IInquiryService
{
    Task<SubmitInquiryResponse> SubmitAsync(SubmitInquiryRequest req, CancellationToken ct = default);
    Task<PagedResult<InquiryListItemDto>> ListAsync(InquiryFilter filter, CancellationToken ct = default);
    Task<InquiryDetailDto> GetAsync(Guid id, CancellationToken ct = default);
    Task UpdateAsync(Guid id, UpdateInquiryRequest req, CancellationToken ct = default);
}
