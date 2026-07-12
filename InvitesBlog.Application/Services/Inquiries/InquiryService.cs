using FluentValidation;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Common;
using InvitesBlog.Application.Dtos.Inquiries;
using InvitesBlog.Application.Exceptions.Inquiries;
using InvitesBlog.Application.Filters.Inquiries;
using InvitesBlog.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace InvitesBlog.Application.Services.Inquiries;

/// <summary>
/// Custom-invitation inquiry pipeline. A customer submits the public "Start an inquiry" form; the owner
/// works it in the admin queue (consultation fields + attended flag); then issues a dedicated template
/// reserved for the customer's email and emails them a "your invitation is ready" link.
/// </summary>
public sealed class InquiryService(
    IRepository<Inquiry> inquiries,
    ITemplateRepository templates,
    IUnitOfWork uow,
    IEmailSender email,
    IConfiguration config,
    IValidator<SubmitInquiryRequest> submitValidator) : IInquiryService
{
    public async Task<SubmitInquiryResponse> SubmitAsync(SubmitInquiryRequest req, CancellationToken ct = default)
    {
        await submitValidator.ValidateAndThrowAsync(req, ct);
        var inquiry = new Inquiry
        {
            Id = Guid.NewGuid(),
            Name = req.Name.Trim(),
            Email = req.Email.Trim().ToLowerInvariant(),
            Occasion = req.Occasion.Trim(),
            Message = req.Message.Trim(),
            HasAttended = false,
            TemplateIssued = false,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await inquiries.AddAsync(inquiry, ct);
        await uow.SaveChangesAsync(ct);
        return new SubmitInquiryResponse(inquiry.Id);
    }

    public async Task<PagedResult<InquiryListItemDto>> ListAsync(InquiryFilter filter, CancellationToken ct = default)
    {
        var query = inquiries.Query();

        // Pipeline tab.
        query = filter.Status?.Trim().ToLowerInvariant() switch
        {
            "unattended" => query.Where(i => !i.HasAttended),
            "attended-unissued" => query.Where(i => i.HasAttended && !i.TemplateIssued),
            _ => query, // "all" (or unset)
        };

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var term = filter.Search.Trim().ToLower();
            query = query.Where(i =>
                i.Name.ToLower().Contains(term) ||
                i.Email.ToLower().Contains(term) ||
                i.Occasion.ToLower().Contains(term));
        }

        var total = await query.CountAsync(ct);
        var items = await query
            .OrderBy(i => i.HasAttended)   // unattended (false) first
            .ThenBy(i => i.CreatedAt)      // then oldest first
            .Skip(filter.Skip).Take(filter.PageSize)
            .Select(i => new InquiryListItemDto(
                i.Id, i.Name, i.Email, i.Occasion, i.HasAttended, i.TemplateIssued, i.CreatedAt))
            .ToListAsync(ct);

        return PagedResult<InquiryListItemDto>.Create(items, total, filter);
    }

    public async Task<InquiryDetailDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        var i = await inquiries.GetByIdAsync(id, ct) ?? throw new InquiryNotFoundException(id);
        return new InquiryDetailDto(
            i.Id, i.Name, i.Email, i.Occasion, i.Message, i.Colors, i.References, i.Notes,
            i.HasAttended, i.AttendedAt, i.TemplateIssued, i.TemplateIssuedAt, i.IssuedTemplateId, i.CreatedAt);
    }

    public async Task UpdateAsync(Guid id, UpdateInquiryRequest req, CancellationToken ct = default)
    {
        var i = await inquiries.GetByIdAsync(id, ct) ?? throw new InquiryNotFoundException(id);
        i.Colors = Clean(req.Colors);
        i.References = Clean(req.References);
        i.Notes = Clean(req.Notes);
        // Stamp/clear the attended time only when the flag actually changes.
        if (req.HasAttended && !i.HasAttended) { i.HasAttended = true; i.AttendedAt = DateTimeOffset.UtcNow; }
        else if (!req.HasAttended && i.HasAttended) { i.HasAttended = false; i.AttendedAt = null; }
        await uow.SaveChangesAsync(ct);
    }

    public async Task<InquiryIssuedResponse> IssueTemplateAsync(Guid id, IssueTemplateData data, CancellationToken ct = default)
    {
        var inquiry = await inquiries.GetByIdAsync(id, ct) ?? throw new InquiryNotFoundException(id);

        var slug = data.Slug.Trim().ToLowerInvariant();
        var version = string.IsNullOrWhiteSpace(data.Version) ? "1.0.0" : data.Version.Trim();

        // Create or update a DEDICATED template reserved for this customer's email (mirrors the admin
        // upload's create-or-update, but the assigned email comes from the inquiry, not typed by hand).
        var existing = await templates.FirstOrDefaultAsync(t => t.Slug == slug && t.Version == version, ct);
        Template template;
        if (existing is not null)
        {
            template = (await templates.GetByIdAsync(existing.Id, ct))!;
            template.Name = data.Name;
            template.Category = data.Category;
            template.Description = data.Description ?? template.Description;
            template.ManifestJson = data.ManifestJson;
            template.PackageUrl = data.PackageUrl;
            template.PreviewImageUrl = $"{data.PackageUrl}index.html";
            template.Visibility = TemplateVisibility.Dedicated;
            template.AssignedEmail = inquiry.Email;
            template.IsActive = true;
            templates.Update(template);
        }
        else
        {
            template = new Template
            {
                Id = Guid.NewGuid(),
                Name = data.Name,
                Slug = slug,
                Version = version,
                Category = data.Category,
                Description = data.Description ?? $"A {data.Category.ToLowerInvariant()} invitation.",
                PreviewImageUrl = $"{data.PackageUrl}index.html",
                IsPremium = false,
                DesignerName = "invites.blog",
                SceneJson = "{}",
                ManifestJson = data.ManifestJson,
                PackageUrl = data.PackageUrl,
                IsActive = true,
                Visibility = TemplateVisibility.Dedicated,
                AssignedEmail = inquiry.Email,
                CreatedAt = DateTimeOffset.UtcNow
            };
            await templates.AddAsync(template, ct);
        }

        var now = DateTimeOffset.UtcNow;
        inquiry.TemplateIssued = true;
        inquiry.TemplateIssuedAt = now;
        inquiry.IssuedTemplateId = template.Id;
        // Issuing implies the customer was consulted — keep the pipeline consistent so an issued
        // inquiry never reads as "not attended".
        if (!inquiry.HasAttended) { inquiry.HasAttended = true; inquiry.AttendedAt = now; }
        await uow.SaveChangesAsync(ct);

        // Notify the customer their invitation is ready. Non-fatal if the email provider hiccups —
        // issuance is already committed and the customer can still reach it via /request-template.
        var emailed = false;
        try
        {
            await email.SendAsync(BuildReadyEmail(inquiry.Email, inquiry.Name, template.Name), ct);
            emailed = true;
        }
        catch { /* swallow — issuance succeeded */ }

        return new InquiryIssuedResponse(template.Id, slug, emailed);
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private EmailMessage BuildReadyEmail(string to, string name, string templateName)
    {
        var inviterBase = (config["Urls:InviterBase"] ?? "http://localhost:4200").TrimEnd('/');
        var link = $"{inviterBase}/request-template";
        var safeName = System.Net.WebUtility.HtmlEncode(string.IsNullOrWhiteSpace(name) ? "there" : name);
        var safeTpl = System.Net.WebUtility.HtmlEncode(templateName);
        var html =
            "<div style=\"font-family:-apple-system,'Segoe UI',Roboto,Arial,sans-serif;max-width:520px;margin:0 auto;padding:24px;color:#2a1420\">" +
            $"<p style=\"font-size:16px;line-height:1.6\">Dear {safeName},</p>" +
            $"<p style=\"font-size:16px;line-height:1.6\">Wonderful news — your custom invitation, <strong>{safeTpl}</strong>, is ready to view.</p>" +
            $"<p style=\"text-align:center;margin:28px 0\"><a href=\"{link}\" style=\"display:inline-block;background:#db2777;color:#fff;text-decoration:none;padding:14px 30px;border-radius:999px;font-weight:600\">View your invitation</a></p>" +
            $"<p style=\"font-size:12px;color:#8a5c72;line-height:1.6\">Open the link and verify this email address to see it. Or paste this into your browser:<br><a href=\"{link}\" style=\"color:#b9748f\">{link}</a><br>Sent via invites.blog</p></div>";
        return new EmailMessage(To: to, Subject: "Your invitation is ready ✨", Html: html, Stream: EmailStream.System);
    }
}
