using System.Text;
using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Dtos.Designers;
using InvitesBlog.Application.Services.Designers;
using InvitesBlog.Domain.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvitesBlog.Api.Controllers;

/// <summary>
/// A designer's own submissions. Uploads are multipart — the template's <c>index.html</c> and its
/// preview image are files, everything else is form fields.
/// </summary>
[Route("api/designer/templates")]
[HasPermission(Permissions.Designer.Manage)]
public sealed class DesignerTemplatesController(IDesignerTemplateService designer) : BaseApiController
{
    /// <summary>Dry-run the scan so the designer sees what we detected before they commit to submitting.</summary>
    [HttpPost("scan")]
    public async Task<IActionResult> Scan(IFormFile index, CancellationToken ct) =>
        Success(designer.Scan(await ReadTextAsync(index, ct)));

    [HttpGet]
    public async Task<IActionResult> Mine(CancellationToken ct) =>
        Success(await designer.ListMineAsync(ct));

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) =>
        Success(await designer.GetMineAsync(id, ct));

    [HttpPost]
    public async Task<IActionResult> Submit(
        [FromForm] string name,
        [FromForm] string category,
        [FromForm] string? description,
        IFormFile index,
        IFormFile preview,
        [FromForm] Guid? publishedTemplateId,
        [FromForm] string? requestedByEmail,
        [FromForm] decimal? commissionPrice,
        [FromForm] decimal? usagePrice,
        CancellationToken ct) =>
        Created(await designer.SubmitAsync(
            await BuildAsync(name, category, description, index, preview,
                publishedTemplateId, requestedByEmail, commissionPrice, usagePrice, ct), ct));

    [HttpPost("{id:guid}/resubmit")]
    public async Task<IActionResult> Resubmit(
        Guid id,
        [FromForm] string name,
        [FromForm] string category,
        [FromForm] string? description,
        IFormFile index,
        IFormFile preview,
        CancellationToken ct) =>
        Success(await designer.ResubmitAsync(
            id, await BuildAsync(name, category, description, index, preview, null, null, null, null, ct), ct));

    /// <summary>The designer's half of the two-party consent that releases a commission to the gallery.</summary>
    [HttpPost("{id:guid}/consent-to-publish")]
    public async Task<IActionResult> ConsentToPublish(Guid id, CancellationToken ct) =>
        Success(await designer.ConsentToPublishAsync(id, ct));

    private static async Task<SubmitTemplateRequest> BuildAsync(
        string name, string category, string? description, IFormFile index, IFormFile preview,
        Guid? publishedTemplateId, string? requestedByEmail, decimal? commissionPrice, decimal? usagePrice,
        CancellationToken ct) =>
        new(name, category, description ?? string.Empty,
            await ReadTextAsync(index, ct), await ReadFileAsync(preview, ct),
            publishedTemplateId, requestedByEmail, commissionPrice, usagePrice);

    private static async Task<string> ReadTextAsync(IFormFile? file, CancellationToken ct)
    {
        if (file is null) return string.Empty;
        await using var stream = file.OpenReadStream();
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(ct);
    }

    private static async Task<UploadedFile> ReadFileAsync(IFormFile? file, CancellationToken ct)
    {
        if (file is null) return new UploadedFile(string.Empty, string.Empty, []);
        await using var stream = file.OpenReadStream();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return new UploadedFile(file.FileName, file.ContentType, buffer.ToArray());
    }
}
