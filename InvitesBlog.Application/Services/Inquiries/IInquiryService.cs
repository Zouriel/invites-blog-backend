using InvitesBlog.Application.Dtos.Inquiries;

namespace InvitesBlog.Application.Services.Inquiries;

/// <summary>Custom-invitation inquiry pipeline: public submit → admin triage → issue dedicated template.</summary>
public interface IInquiryService
{
    Task<SubmitInquiryResponse> SubmitAsync(SubmitInquiryRequest req, CancellationToken ct = default);
    Task<IReadOnlyList<InquiryListItemDto>> ListAsync(CancellationToken ct = default);
    Task<InquiryDetailDto> GetAsync(Guid id, CancellationToken ct = default);
    Task UpdateAsync(Guid id, UpdateInquiryRequest req, CancellationToken ct = default);
    Task<InquiryIssuedResponse> IssueTemplateAsync(Guid id, IssueTemplateData data, CancellationToken ct = default);
}
