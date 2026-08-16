using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Designers;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Exceptions.Designers;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Services.Designers;

/// <summary>
/// A designer's submissions. The automatic scan (§4.1) runs synchronously here, BEFORE a row exists —
/// a template that fails it never reaches the review queue, so the queue only ever holds submissions
/// that are already known to be safe and well-formed.
/// </summary>
public sealed class DesignerTemplateService(
    ICurrentUser currentUser,
    IRepository<CustomTemplate> submissions,
    IRepository<Inquiry> inquiries,
    IRepository<AppUser> users,
    ITemplateRepository templates,
    ITemplatePackager packager,
    IStorageService storage,
    IUnitOfWork uow) : IDesignerTemplateService
{
    /// <summary>Statuses a designer may still replace the content of.</summary>
    private static readonly CustomTemplateStatus[] Revisable =
        [CustomTemplateStatus.Draft, CustomTemplateStatus.Rejected, CustomTemplateStatus.Submitted];

    public TemplateScanResultDto Scan(string html)
    {
        var bytes = System.Text.Encoding.UTF8.GetByteCount(html ?? string.Empty);

        try
        {
            packager.Scan(html ?? string.Empty);
        }
        catch (AppException ex)
        {
            return new TemplateScanResultDto(
                false, ex.ErrorCode, ex.Message, bytes, packager.RecommendedBytes, packager.MaxBytes,
                bytes > packager.RecommendedBytes, [], [], [], []);
        }

        TemplateStructure structure;
        try
        {
            structure = packager.Describe("preview", html!);
        }
        catch (AppException ex)
        {
            // Authoring mistakes (a select with no options) surface here rather than as a scan failure.
            return new TemplateScanResultDto(
                false, ex.ErrorCode, ex.Message, bytes, packager.RecommendedBytes, packager.MaxBytes,
                bytes > packager.RecommendedBytes, [], [], [], []);
        }

        return new TemplateScanResultDto(
            true, null, null, bytes, packager.RecommendedBytes, packager.MaxBytes,
            bytes > packager.RecommendedBytes,
            structure.Fields, structure.ImageSlots, structure.Roles, structure.ThemeKeys);
    }

    public async Task<DesignerTemplateDto> SubmitAsync(SubmitTemplateRequest request, CancellationToken ct = default)
    {
        var designerId = await ActiveDesignerIdAsync(ct);
        Validate(request);

        var commission = await ResolveCommissionAsync(request.CommissionInquiryId, designerId, ct);

        var now = DateTimeOffset.UtcNow;
        var entity = new CustomTemplate
        {
            Id = Guid.NewGuid(),
            DesignerUserId = designerId,
            Name = request.Name.Trim(),
            Description = (request.Description ?? string.Empty).Trim(),
            Category = request.Category.Trim(),
            // System-generated, never author-chosen — the id keeps it unique without a lookup.
            Slug = Slugify(request.Name),
            Status = CustomTemplateStatus.Submitted,
            PublishedTemplateId = request.PublishedTemplateId,
            RequestedByEmail = commission?.Email,
            CommissionPrice = commission?.CommissionPrice,
            UsagePrice = commission?.UsagePrice ?? request.UsagePrice,
            CreatedAt = now,
            UpdatedAt = now
        };

        await ApplyContentAsync(entity, request, ct);
        await submissions.AddAsync(entity, ct);
        await uow.SaveChangesAsync(ct);

        return ToDto(entity);
    }

    public async Task<DesignerTemplateDto> ResubmitAsync(
        Guid id, SubmitTemplateRequest request, CancellationToken ct = default)
    {
        await ActiveDesignerIdAsync(ct);
        var entity = await LoadMineAsync(id, ct);
        Validate(request);

        if (!Revisable.Contains(entity.Status))
            throw new InvalidStateException(
                $"This submission is {entity.Status} and can no longer be edited — submit a new revision instead.",
                "submission_not_revisable");

        entity.Name = request.Name.Trim();
        entity.Description = (request.Description ?? string.Empty).Trim();
        entity.Category = request.Category.Trim();
        entity.Status = CustomTemplateStatus.Submitted;
        entity.RejectionReason = null;
        entity.UpdatedAt = DateTimeOffset.UtcNow;

        await ApplyContentAsync(entity, request, ct);
        submissions.Update(entity);
        await uow.SaveChangesAsync(ct);

        return ToDto(entity);
    }

    public async Task<IReadOnlyList<DesignerTemplateDto>> ListMineAsync(CancellationToken ct = default)
    {
        var designerId = DesignerId();
        var list = await submissions.Query()
            .Where(t => t.DesignerUserId == designerId)
            .OrderByDescending(t => t.UpdatedAt)
            .ToListAsync(ct);

        return await WithReleaseStateAsync(list, ct);
    }

    public async Task<DesignerTemplateDto> GetMineAsync(Guid id, CancellationToken ct = default) =>
        (await WithReleaseStateAsync([await LoadMineAsync(id, ct)], ct))[0];

    /// <summary>
    /// Overlays the live release state from the published <c>Template</c>, which is where consent
    /// actually lives — the submission row's own flags are only what the designer set at submit time
    /// and go stale the moment either party consents afterwards.
    /// </summary>
    private async Task<IReadOnlyList<DesignerTemplateDto>> WithReleaseStateAsync(
        IReadOnlyList<CustomTemplate> list, CancellationToken ct)
    {
        var publishedIds = list.Select(t => t.PublishedTemplateId).OfType<Guid>().Distinct().ToList();
        if (publishedIds.Count == 0) return list.Select(t => ToDto(t)).ToList();

        var published = await templates.Query()
            .Where(t => publishedIds.Contains(t.Id))
            .ToDictionaryAsync(t => t.Id, ct);

        return list.Select(t =>
        {
            if (t.PublishedTemplateId is { } id && published.TryGetValue(id, out var live))
                return ToDto(t, live);
            return ToDto(t);
        }).ToList();
    }

    public async Task<DesignerTemplateDto> ConsentToPublishAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await LoadMineAsync(id, ct);
        entity.DesignerConsentToPublish = true;
        entity.UpdatedAt = DateTimeOffset.UtcNow;
        submissions.Update(entity);
        await uow.SaveChangesAsync(ct);
        return ToDto(entity);
    }

    /// <summary>
    /// Resolves the commission a submission answers, from the inquiry rather than from the request
    /// body: only an inquiry actually assigned to THIS designer can set a requester email or a price.
    /// </summary>
    private async Task<Inquiry?> ResolveCommissionAsync(Guid? inquiryId, Guid designerId, CancellationToken ct)
    {
        if (inquiryId is not { } id) return null;

        var inquiry = await inquiries.FirstOrDefaultAsync(i => i.Id == id, ct)
                      ?? throw new NotFoundException("That commission doesn't exist.", "commission_not_found");

        if (inquiry.AssignedDesignerUserId != designerId)
            throw new ForbiddenException(
                "That commission wasn't assigned to you.", "commission_not_assigned_to_you");

        return inquiry;
    }

    /// <summary>
    /// Scans the HTML, stages the package somewhere reviewable, and stores the preview image. Nothing
    /// here touches the live <c>templates/</c> path — that only happens on approval.
    /// </summary>
    private async Task ApplyContentAsync(CustomTemplate entity, SubmitTemplateRequest request, CancellationToken ct)
    {
        packager.Scan(request.Html);

        var package = await packager.PublishAsync(
            $"submissions/{entity.Id}", entity.Slug, "review", request.Html, ct);

        entity.Html = request.Html;
        entity.PackageUrl = package.PackageUrl;
        entity.ManifestJson = package.ManifestJson;
        entity.PreviewImageUrl = await StorePreviewAsync(entity.Id, request.PreviewImage, ct);
    }

    private async Task<string> StorePreviewAsync(Guid id, UploadedFile image, CancellationToken ct)
    {
        var extension = Path.GetExtension(image.FileName);
        if (string.IsNullOrWhiteSpace(extension)) extension = ".png";
        var key = $"submissions/{id}/preview{extension.ToLowerInvariant()}";
        return await storage.PutAsync(key, image.Content, image.ContentType, ct);
    }

    private static void Validate(SubmitTemplateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new BusinessRuleException("Give your template a name.", "template_name_required");
        if (string.IsNullOrWhiteSpace(request.Category))
            throw new BusinessRuleException("Pick a category for your template.", "template_category_required");
        if (string.IsNullOrWhiteSpace(request.Html))
            throw new BusinessRuleException("Attach your template's index.html.", "template_html_required");

        // A preview image is required so nothing can reach the gallery without one.
        if (request.PreviewImage is null || request.PreviewImage.Content.Length == 0)
            throw new BusinessRuleException(
                "Upload a preview image — it's what people see on the template card.", "template_preview_required");
        if (!request.PreviewImage.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            throw new BusinessRuleException(
                "The preview must be an image (PNG or JPEG).", "template_preview_not_an_image");
    }

    private async Task<CustomTemplate> LoadMineAsync(Guid id, CancellationToken ct)
    {
        var designerId = DesignerId();
        return await submissions.Query(tracking: true).FirstOrDefaultAsync(
                   t => t.Id == id && t.DesignerUserId == designerId, ct)
               ?? throw new NotFoundException("That submission doesn't exist.", "submission_not_found");
    }

    private Guid DesignerId() => currentUser.UserId ?? throw new UnauthorizedException();

    /// <summary>
    /// The signed-in designer, confirmed to still be active. Suspension has to be enforced on the
    /// REQUEST, not just at sign-in: a token issued before an admin suspended the account stays
    /// cryptographically valid for its whole lifetime, so checking only at login would let a
    /// suspended designer keep submitting for days — exactly what suspending is meant to stop.
    /// </summary>
    private async Task<Guid> ActiveDesignerIdAsync(CancellationToken ct)
    {
        var id = DesignerId();
        var user = await users.GetByIdAsync(id, ct) ?? throw new UnauthorizedException();
        if (!user.IsActive) throw new DesignerSuspendedException();
        return id;
    }

    internal static DesignerTemplateDto ToDto(CustomTemplate t, Template? published = null) => new(
        t.Id, t.Name, t.Slug, t.Category, t.Description, t.Status.ToString(), t.RejectionReason,
        t.PreviewImageUrl, t.PackageUrl, t.ManifestJson, t.PublishedTemplateId,
        published?.CommissionPrice ?? t.CommissionPrice,
        published?.UsagePrice ?? t.UsagePrice,
        published?.RequestedByEmail ?? t.RequestedByEmail,
        published?.RequesterConsentToPublish ?? t.RequesterConsentToPublish,
        published?.DesignerConsentToPublish ?? t.DesignerConsentToPublish,
        t.CreatedAt, t.UpdatedAt, published?.Visibility);

    /// <summary>A URL-safe slug from the template's name, suffixed so two "Aurora" submissions can't collide.</summary>
    private static string Slugify(string name)
    {
        var chars = name.Trim().ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        if (slug.Length == 0) slug = "template";
        if (slug.Length > 48) slug = slug[..48].TrimEnd('-');
        return $"{slug}-{Guid.NewGuid().ToString("n")[..6]}";
    }
}
