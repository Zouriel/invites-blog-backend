using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Designers;
using InvitesBlog.Application.Exceptions;
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
        var designerId = DesignerId();
        Validate(request);

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
            RequestedByEmail = request.RequestedByEmail?.Trim().ToLowerInvariant(),
            CommissionPrice = request.CommissionPrice,
            UsagePrice = request.UsagePrice,
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

        return list.Select(ToDto).ToList();
    }

    public async Task<DesignerTemplateDto> GetMineAsync(Guid id, CancellationToken ct = default) =>
        ToDto(await LoadMineAsync(id, ct));

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

    internal static DesignerTemplateDto ToDto(CustomTemplate t) => new(
        t.Id, t.Name, t.Slug, t.Category, t.Description, t.Status.ToString(), t.RejectionReason,
        t.PreviewImageUrl, t.PackageUrl, t.ManifestJson, t.PublishedTemplateId,
        t.CommissionPrice, t.UsagePrice, t.RequestedByEmail,
        t.RequesterConsentToPublish, t.DesignerConsentToPublish, t.CreatedAt, t.UpdatedAt);

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
