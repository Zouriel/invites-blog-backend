using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Designers;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Services.Designers;

/// <summary>
/// Releasing a commissioned template to the gallery. Both sides must agree, and each can only set
/// their own flag — so neither the designer nor the requester can publish the other's work alone.
/// The moment both are true the template flips from <see cref="TemplateVisibility.Dedicated"/> to
/// <see cref="TemplateVisibility.Public"/>, which is what makes its per-use fee chargeable.
/// </summary>
public sealed class TemplateReleaseService(
    ICurrentUser currentUser,
    ITemplateRepository templates,
    IUnitOfWork uow) : ITemplateReleaseService
{
    public async Task<TemplateReleaseDto> GetAsync(Guid templateId, CancellationToken ct = default)
    {
        var template = await LoadAsync(templateId, ct);
        EnsureVisibleToCaller(template);
        return ToDto(template);
    }

    public async Task<TemplateReleaseDto> ConsentAsDesignerAsync(Guid templateId, CancellationToken ct = default)
    {
        var template = await LoadAsync(templateId, ct);
        var designerId = currentUser.UserId ?? throw new UnauthorizedException();

        if (template.DesignerUserId != designerId)
            throw new ForbiddenException("This isn't your template.", "not_your_template");

        template.DesignerConsentToPublish = true;
        return await SaveAsync(template, ct);
    }

    public async Task<TemplateReleaseDto> ConsentAsRequesterAsync(Guid templateId, CancellationToken ct = default)
    {
        var template = await LoadAsync(templateId, ct);
        var email = VerifiedEmail();

        if (!string.Equals(template.AssignedEmail, email, StringComparison.OrdinalIgnoreCase))
            throw new ForbiddenException(
                "This template wasn't commissioned by you.", "not_your_commission");

        template.RequesterConsentToPublish = true;
        return await SaveAsync(template, ct);
    }

    public async Task<IReadOnlyList<TemplateReleaseDto>> ListForRequesterAsync(CancellationToken ct = default)
    {
        var email = VerifiedEmail();
        var list = await templates.Query()
            .Where(t => t.IsActive
                        && t.Visibility == TemplateVisibility.Dedicated
                        && t.AssignedEmail == email)
            .OrderByDescending(t => t.UpdatedAt)
            .ToListAsync(ct);

        return list.Select(ToDto).ToList();
    }

    /// <summary>
    /// Applies the consent and, once BOTH parties have agreed, actually releases the template. The
    /// flip is one-way here — un-consenting later would strand campaigns already started from the
    /// now-public template.
    /// </summary>
    private async Task<TemplateReleaseDto> SaveAsync(Template template, CancellationToken ct)
    {
        if (template.RequesterConsentToPublish && template.DesignerConsentToPublish
            && template.Visibility == TemplateVisibility.Dedicated)
        {
            template.Visibility = TemplateVisibility.Public;
            // A public template is freely reusable — the single-use showcase rule is dedicated-only.
            template.IsUsed = false;
            template.AssignedEmail = null;
        }

        template.UpdatedAt = DateTimeOffset.UtcNow;
        templates.Update(template);
        await uow.SaveChangesAsync(ct);
        return ToDto(template);
    }

    /// <summary>Only the two parties (or an admin) may look at a commission's release state.</summary>
    private void EnsureVisibleToCaller(Template template)
    {
        if (currentUser.HasPermission(Domain.Authorization.Permissions.Designer.Review)) return;
        if (currentUser.UserId is { } id && template.DesignerUserId == id) return;
        if (string.Equals(template.AssignedEmail, currentUser.Contact?.Trim().ToLowerInvariant(),
                StringComparison.OrdinalIgnoreCase)) return;

        throw new ForbiddenException("You can't view this template's release state.", "not_your_commission");
    }

    private string VerifiedEmail()
    {
        var email = currentUser.Contact?.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(email))
            throw new UnauthorizedException("Verify your email address first.", "email_not_verified");
        return email;
    }

    private async Task<Template> LoadAsync(Guid id, CancellationToken ct) =>
        await templates.Query(tracking: true).FirstOrDefaultAsync(t => t.Id == id, ct)
        ?? throw new NotFoundException("That template doesn't exist.", "template_not_found");

    private static TemplateReleaseDto ToDto(Template t) => new(
        t.Id, t.Name, t.Slug, t.PreviewImageUrl, t.Visibility, t.RequestedByEmail, t.DesignerName,
        t.UsagePrice, t.RequesterConsentToPublish, t.DesignerConsentToPublish,
        t.Visibility == TemplateVisibility.Public);
}
