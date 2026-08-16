using InvitesBlog.Application.Common;
using InvitesBlog.Application.Dtos.Designers;
using InvitesBlog.Application.Filters.Designers;

namespace InvitesBlog.Application.Services.Designers;

/// <summary>The designer's own side of the submission pipeline (§Phase 4).</summary>
public interface IDesignerTemplateService
{
    /// <summary>
    /// Runs the automatic scan and reports what it found WITHOUT creating anything — the submission
    /// form uses this so a designer sees their fields, slots and roles before committing.
    /// </summary>
    TemplateScanResultDto Scan(string html);

    /// <summary>
    /// Scans, then creates the submission in <c>Submitted</c> status. A template that fails the scan
    /// never reaches the review queue at all — no row is written.
    /// </summary>
    Task<DesignerTemplateDto> SubmitAsync(SubmitTemplateRequest request, CancellationToken ct = default);

    /// <summary>Replaces a rejected submission's content and puts it back in the queue.</summary>
    Task<DesignerTemplateDto> ResubmitAsync(Guid id, SubmitTemplateRequest request, CancellationToken ct = default);

    Task<IReadOnlyList<DesignerTemplateDto>> ListMineAsync(CancellationToken ct = default);
    Task<DesignerTemplateDto> GetMineAsync(Guid id, CancellationToken ct = default);

    /// <summary>The designer half of the two-party consent that releases a commission to the gallery.</summary>
    Task<DesignerTemplateDto> ConsentToPublishAsync(Guid id, CancellationToken ct = default);
}

/// <summary>The admin review queue (§Phase 4.2).</summary>
public interface ITemplateReviewService
{
    Task<PagedResult<TemplateSubmissionDto>> ListAsync(TemplateSubmissionFilter filter, CancellationToken ct = default);
    Task<TemplateSubmissionDto> GetAsync(Guid id, CancellationToken ct = default);

    /// <summary>
    /// Approve — promoting the submission into a real gallery template (or bumping the version of the
    /// one it edits) — or reject with a reason the designer will see.
    /// </summary>
    Task<TemplateSubmissionDto> ReviewAsync(Guid id, ReviewSubmissionRequest request, CancellationToken ct = default);
}
