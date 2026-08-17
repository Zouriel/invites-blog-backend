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
    ICurrentUser currentUser,
    IRepository<Inquiry> inquiries,
    IRepository<AppUser> users,
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
            RequestedDesignerUserId = req.RequestedDesignerUserId,
            HasAttended = false,
            TemplateIssued = false,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await inquiries.AddAsync(inquiry, ct);
        await uow.SaveChangesAsync(ct);

        // Being asked for by name should reach the designer the same day, not whenever they next
        // open the site. Best-effort: a mail failure must never lose the request itself.
        if (inquiry.RequestedDesignerUserId is { } requestedId)
        {
            try
            {
                var designer = await users.GetByIdAsync(requestedId, ct);
                if (designer is { IsActive: true, Email: { Length: > 0 } to })
                    await email.SendAsync(BuildRequestedEmail(to, designer.DisplayName, inquiry), ct);
            }
            catch { /* swallow — the request is safely stored and visible in their queue */ }
        }

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
        return await ToDetailAsync(i, ct);
    }

    public async Task<InquiryDetailDto> AssignCommissionAsync(
        Guid id, AssignCommissionRequest req, CancellationToken ct = default)
    {
        var i = await inquiries.GetByIdAsync(id, ct) ?? throw new InquiryNotFoundException(id);

        if (req.DesignerUserId is { } designerId
            && !await users.AnyAsync(u => u.Id == designerId && u.IsActive, ct))
            throw new Exceptions.BusinessRuleException(
                "That designer account doesn't exist or has been suspended.", "unknown_designer");

        i.AssignedDesignerUserId = req.DesignerUserId;
        i.CommissionPrice = req.CommissionPrice;
        i.UsagePrice = req.UsagePrice;
        await uow.SaveChangesAsync(ct);

        return await ToDetailAsync(i, ct);
    }

    public async Task<IReadOnlyList<DesignerCommissionDto>> ListCommissionsForDesignerAsync(
        CancellationToken ct = default)
    {
        var designerId = currentUser.UserId ?? throw new Exceptions.UnauthorizedException();

        // Work they're on, PLUS requests that asked for them by name — otherwise a customer could
        // request a designer and that designer would never learn of it.
        var list = await inquiries.Query()
            .Where(i => i.AssignedDesignerUserId == designerId || i.RequestedDesignerUserId == designerId)
            .OrderByDescending(i => i.CreatedAt)
            .ToListAsync(ct);

        return list.Select(i => new DesignerCommissionDto(
            i.Id, i.Name, i.Email, i.Occasion, i.Message, i.Colors, i.References, i.Notes,
            i.CommissionPrice, i.UsagePrice, i.TemplateIssued, i.CreatedAt,
            Assigned: i.AssignedDesignerUserId == designerId,
            RequestedMe: i.RequestedDesignerUserId == designerId)).ToList();
    }

    private async Task<InquiryDetailDto> ToDetailAsync(Inquiry i, CancellationToken ct)
    {
        var assigned = i.AssignedDesignerUserId is { } id ? await users.GetByIdAsync(id, ct) : null;
        var requested = i.RequestedDesignerUserId is { } rid ? await users.GetByIdAsync(rid, ct) : null;
        return new InquiryDetailDto(
            i.Id, i.Name, i.Email, i.Occasion, i.Message, i.Colors, i.References, i.Notes,
            i.HasAttended, i.AttendedAt, i.TemplateIssued, i.TemplateIssuedAt, i.IssuedTemplateId, i.CreatedAt,
            i.AssignedDesignerUserId, assigned?.DisplayName, i.CommissionPrice, i.UsagePrice,
            i.RequestedDesignerUserId, requested?.DisplayName);
    }

    /// <summary>
    /// Designers a customer may ask for by name. Only active accounts with something published —
    /// a name with no work behind it is noise on a request form, and the list is public so it
    /// carries no email addresses.
    /// </summary>
    public async Task<IReadOnlyList<PublicDesignerDto>> ListPublicDesignersAsync(CancellationToken ct = default)
    {
        var published = await templates.Query()
            .Where(t => t.IsActive && t.DesignerUserId != null)
            .GroupBy(t => t.DesignerUserId!.Value)
            .Select(g => new { UserId = g.Key, Count = g.Count() })
            .ToListAsync(ct);
        if (published.Count == 0) return [];

        var ids = published.Select(p => p.UserId).ToList();
        var designers = await users.Query()
            .Where(u => ids.Contains(u.Id) && u.IsActive)
            .ToListAsync(ct);

        return designers
            .Select(u => new PublicDesignerDto(
                u.Id, u.DisplayName, published.First(p => p.UserId == u.Id).Count))
            .OrderBy(d => d.DisplayName)
            .ToList();
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
            template.UpdatedAt = DateTimeOffset.UtcNow;
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
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow
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

    /// <summary>Tells a designer that someone asked for them by name. No prices — terms come later.</summary>
    private EmailMessage BuildRequestedEmail(string to, string designerName, Inquiry inquiry)
    {
        var inviterBase = (config["Urls:InviterBase"] ?? "http://localhost:4200").TrimEnd('/');
        var link = $"{inviterBase}/designer/requests";
        var safeDesigner = System.Net.WebUtility.HtmlEncode(designerName);
        var safeWho = System.Net.WebUtility.HtmlEncode(inquiry.Name);
        var safeOccasion = System.Net.WebUtility.HtmlEncode(inquiry.Occasion);
        var safeBrief = System.Net.WebUtility.HtmlEncode(inquiry.Message);
        var html =
            "<div style=\"font-family:-apple-system,'Segoe UI',Roboto,Arial,sans-serif;max-width:520px;margin:0 auto;padding:24px;color:#2a1420\">" +
            $"<p style=\"font-size:16px;line-height:1.6\">Hi {safeDesigner},</p>" +
            $"<p style=\"font-size:16px;line-height:1.6\"><strong>{safeWho}</strong> asked for you by name for a {safeOccasion} invitation.</p>" +
            $"<blockquote style=\"margin:18px 0;padding:12px 16px;border-left:3px solid #f0c8d8;color:#5a3547;font-size:15px;line-height:1.6\">{safeBrief}</blockquote>" +
            $"<p style=\"text-align:center;margin:28px 0\"><a href=\"{link}\" style=\"display:inline-block;background:#db2777;color:#fff;text-decoration:none;padding:14px 30px;border-radius:999px;font-weight:600\">See the request</a></p>" +
            "<p style=\"font-size:12px;color:#8a5c72;line-height:1.6\">We'll agree the terms with them and hand it over — you'll see it move to \u201cTo build\u201d.<br>Sent via invites.blog</p></div>";
        return new EmailMessage(To: to, Subject: $"{safeWho} asked for you \u2728", Html: html, Stream: EmailStream.System);
    }

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
