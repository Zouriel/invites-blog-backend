using FluentValidation;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Common;
using InvitesBlog.Application.Dtos.Inquiries;
using InvitesBlog.Application.Exceptions.Inquiries;
using InvitesBlog.Application.Filters.Inquiries;
using InvitesBlog.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Services.Inquiries;

/// <summary>
/// Requests for a made-to-order invitation. A customer sends the public "Start an inquiry" form; the
/// owner reads it in the admin queue, keeps consultation notes and marks it attended. Everything after
/// that — agreeing the work, designing it and publishing it for them from the designer — happens
/// outside this pipeline.
/// </summary>
public sealed class InquiryService(
    IRepository<Inquiry> inquiries,
    IUnitOfWork uow,
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
            "attended" => query.Where(i => i.HasAttended),
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
                i.Id, i.Name, i.Email, i.Occasion, i.HasAttended, i.CreatedAt))
            .ToListAsync(ct);

        return PagedResult<InquiryListItemDto>.Create(items, total, filter);
    }

    public async Task<InquiryDetailDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        var i = await inquiries.GetByIdAsync(id, ct) ?? throw new InquiryNotFoundException(id);
        return new InquiryDetailDto(
            i.Id, i.Name, i.Email, i.Occasion, i.Message, i.Colors, i.References, i.Notes,
            i.HasAttended, i.AttendedAt, i.CreatedAt);
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

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
