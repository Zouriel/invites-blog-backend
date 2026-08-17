using InvitesBlog.Application.Common;
using InvitesBlog.Application.Dtos.Inquiries;
using InvitesBlog.Application.Filters.Inquiries;

namespace InvitesBlog.Application.Services.Inquiries;

/// <summary>Custom-invitation inquiry pipeline: public submit → admin triage → issue dedicated template.</summary>
public interface IInquiryService
{
    Task<SubmitInquiryResponse> SubmitAsync(SubmitInquiryRequest req, CancellationToken ct = default);
    Task<PagedResult<InquiryListItemDto>> ListAsync(InquiryFilter filter, CancellationToken ct = default);
    Task<InquiryDetailDto> GetAsync(Guid id, CancellationToken ct = default);
    Task UpdateAsync(Guid id, UpdateInquiryRequest req, CancellationToken ct = default);
    Task<InquiryIssuedResponse> IssueTemplateAsync(Guid id, IssueTemplateData data, CancellationToken ct = default);

    /// <summary>Hands the request to a designer at an agreed price, or clears the assignment.</summary>
    Task<InquiryDetailDto> AssignCommissionAsync(Guid id, AssignCommissionRequest req, CancellationToken ct = default);

    /// <summary>
    /// What the signed-in designer should see: requests they're assigned, plus requests that asked
    /// for them by name and are still awaiting terms.
    /// </summary>
    Task<IReadOnlyList<DesignerCommissionDto>> ListCommissionsForDesignerAsync(CancellationToken ct = default);

    /// <summary>Designers a customer can ask for on the request form (public — names only).</summary>
    Task<IReadOnlyList<PublicDesignerDto>> ListPublicDesignersAsync(CancellationToken ct = default);
}
