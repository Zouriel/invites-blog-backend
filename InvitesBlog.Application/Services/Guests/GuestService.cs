using System.Text;
using System.Text.Json;
using FluentValidation;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Guests;
using InvitesBlog.Application.Exceptions.Campaigns;
using InvitesBlog.Application.Exceptions.Guests;
using InvitesBlog.Application.Guests;
using InvitesBlog.Application.Phones;
using InvitesBlog.Application.Security;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Services.Guests;

/// <summary>
/// §10.4 Guest upload + post-payment add/fix/resend. All actions are campaign-scoped: the possession
/// token must map to the target campaign. Guests are only materialized on confirm/add, honoring the
/// hashed suppression list (§15.3) and dedupe on E.164 / lowercased email.
/// </summary>
public sealed class GuestService(
    ICurrentUser currentUser,
    ICampaignRepository campaigns,
    IGuestRepository guests,
    IInviteRepository invites,
    ISuppressionRepository suppression,
    IRepository<UploadedGuestFile> uploads,
    IRepository<DeliveryAttempt> deliveryAttempts,
    IUnitOfWork uow,
    GuestUploadParser parser,
    PhoneNormalizer phones,
    IValidator<ConfirmUploadRequest> confirmValidator) : IGuestService
{
    public async Task<GuestUploadSummaryDto> UploadAsync(
        Guid campaignId, Stream fileStream, string fileName, string defaultCountry, CancellationToken ct = default)
    {
        await EnsureCampaignAsync(campaignId, ct);

        var result = parser.Parse(fileStream, defaultCountry);
        if (result.FileRejected)
            throw new GuestFileRejectedException(result.FileRejectionReason ?? "The file was rejected.");

        // Persist the parse result; guests are only materialized on confirm.
        var upload = new UploadedGuestFile
        {
            Id = Guid.NewGuid(),
            CampaignId = campaignId,
            FileName = fileName,
            DefaultCountry = defaultCountry,
            TotalRows = result.TotalRows,
            ValidRows = result.ValidRows,
            InvalidRows = result.InvalidRows,
            Duplicates = result.Duplicates,
            ResultJson = JsonSerializer.Serialize(result.ValidGuests),
            Confirmed = false,
            CreatedAt = DateTimeOffset.UtcNow
        };
        await uploads.AddAsync(upload, ct);
        await uow.SaveChangesAsync(ct);

        return new GuestUploadSummaryDto(
            upload.Id,
            result.TotalRows,
            result.ValidRows,
            result.InvalidRows,
            result.Duplicates,
            result.MissingPhone,
            result.MissingEmail,
            result.RoleDistribution,
            result.GenderDistribution,
            result.Warnings,
            result.Errors,
            result.CanContinue);
    }

    public async Task<byte[]> ExportErrorsCsvAsync(Guid campaignId, Guid uploadId, CancellationToken ct = default)
    {
        await EnsureCampaignAsync(campaignId, ct);

        var upload = await uploads.FirstOrDefaultAsync(u => u.Id == uploadId && u.CampaignId == campaignId, ct)
                     ?? throw new UploadNotFoundException(uploadId);

        // The accepted guest list is stored; export it as a convenience report (§4.4.6).
        var parsed = JsonSerializer.Deserialize<List<ParsedGuest>>(upload.ResultJson) ?? new();
        var sb = new StringBuilder("email,phone,name,role,gender\r\n");
        foreach (var g in parsed)
            sb.Append(Csv(g.Email)).Append(',').Append(Csv(g.PhoneE164)).Append(',')
              .Append(Csv(g.Name)).Append(',').Append(Csv(g.Role)).Append(',')
              .Append(Csv(g.Gender)).Append("\r\n");
        return Encoding.UTF8.GetBytes(sb.ToString());
    }

    /// <summary>RFC-4180 quoting + neutralizes spreadsheet formula injection (=/+/-/@ leading values).</summary>
    private static string Csv(string? field)
    {
        var s = field ?? "";
        if (s.Length > 0 && (s[0] is '=' or '+' or '-' or '@')) s = "'" + s;
        if (s.IndexOfAny(new[] { ',', '"', '\n', '\r' }) >= 0)
            s = "\"" + s.Replace("\"", "\"\"") + "\"";
        return s;
    }

    public async Task<ConfirmUploadResultDto> ConfirmUploadAsync(
        Guid campaignId, ConfirmUploadRequest req, CancellationToken ct = default)
    {
        await confirmValidator.ValidateAndThrowAsync(req, ct);
        await EnsureCampaignAsync(campaignId, ct);

        var upload = await uploads.Query(tracking: true)
                         .FirstOrDefaultAsync(u => u.Id == req.UploadId && u.CampaignId == campaignId, ct)
                     ?? throw new UploadNotFoundException(req.UploadId);

        var parsed = JsonSerializer.Deserialize<List<ParsedGuest>>(upload.ResultJson) ?? new();
        var added = await MaterializeGuestsAsync(campaignId, parsed, ct);
        upload.Confirmed = true;
        await uow.SaveChangesAsync(ct);

        return new ConfirmUploadResultDto(added, parsed.Count - added);
    }

    public async Task<AddGuestOutcome> AddGuestAsync(Guid campaignId, AddGuestRequest req, CancellationToken ct = default)
    {
        var campaign = await EnsureCampaignAsync(campaignId, ct);

        var phone = string.IsNullOrWhiteSpace(req.Phone)
            ? null
            : phones.Normalize(req.Phone, req.DefaultCountry ?? "MV").E164;
        if (string.IsNullOrWhiteSpace(req.Email) && string.IsNullOrWhiteSpace(phone))
            throw new GuestContactRequiredException();

        var parsed = new ParsedGuest(
            req.Email?.Trim().ToLowerInvariant(), phone, req.Phone,
            string.IsNullOrWhiteSpace(req.Name) ? "Guest" : req.Name!,
            req.Role, string.IsNullOrWhiteSpace(req.Gender) ? "unspecified" : req.Gender!, "{}");

        var added = await MaterializeGuestsAsync(campaignId, new[] { parsed }, ct);
        await uow.SaveChangesAsync(ct);

        var guestCount = await guests.CountByCampaignAsync(campaignId, ct);
        var withinCapacity = guestCount <= campaign.PaidInviteCapacity;

        // If already dispatched and capacity allows, send this new guest immediately.
        Guid? dispatchGuestId = null;
        if (added == 1 && withinCapacity &&
            campaign.Status is CampaignStatus.Dispatched or CampaignStatus.PartiallyDispatched)
        {
            var latest = await guests.Query()
                .Where(x => x.CampaignId == campaignId)
                .OrderByDescending(x => x.CreatedAt)
                .FirstAsync(ct);
            dispatchGuestId = latest.Id;
        }

        var response = new AddGuestResultDto(
            added, guestCount, campaign.PaidInviteCapacity, guestCount > campaign.PaidInviteCapacity);
        return new AddGuestOutcome(response, dispatchGuestId);
    }

    public async Task UpdateGuestAsync(Guid campaignId, Guid guestId, UpdateGuestRequest req, CancellationToken ct = default)
    {
        await EnsureCampaignAsync(campaignId, ct);

        var guest = await guests.Query(tracking: true)
                        .FirstOrDefaultAsync(g => g.Id == guestId && g.CampaignId == campaignId, ct)
                    ?? throw new GuestNotFoundException(guestId);

        if (req.Email is not null)
            guest.Email = string.IsNullOrWhiteSpace(req.Email) ? null : req.Email.Trim().ToLowerInvariant();
        if (req.Phone is not null)
        {
            guest.PhoneRaw = req.Phone;
            guest.PhoneE164 = phones.Normalize(req.Phone, req.DefaultCountry ?? "MV").E164;
        }
        if (req.Name is not null) guest.Name = req.Name;
        if (req.Role is not null) guest.Role = req.Role;
        if (req.Gender is not null) guest.Gender = req.Gender;

        await uow.SaveChangesAsync(ct);
    }

    public async Task PrepareResendAsync(Guid campaignId, Guid guestId, CancellationToken ct = default)
    {
        await EnsureCampaignAsync(campaignId, ct);

        if (!await guests.AnyAsync(g => g.Id == guestId && g.CampaignId == campaignId, ct))
            throw new GuestNotFoundException(guestId);

        // Free resend: max 3 per 24h. Count non-OTP delivery attempts on the guest's invites (§4.7.4).
        var since = DateTimeOffset.UtcNow.AddHours(-24);
        var guestInviteIds = invites.Query().Where(i => i.GuestId == guestId).Select(i => i.Id);
        var recent = await deliveryAttempts.Query()
            .Where(a => guestInviteIds.Contains(a.InviteId) && a.AttemptedAt >= since && !a.IsOtp)
            .CountAsync(ct);
        if (recent >= 3)
            throw new ResendLimitExceededException();
    }

    /// <summary>The possession token must map to this campaign (§4.6.2); loads it for pricing/state.</summary>
    private async Task<Campaign> EnsureCampaignAsync(Guid campaignId, CancellationToken ct)
    {
        if (currentUser.CampaignId != campaignId)
            throw new CampaignAccessDeniedException();
        return await campaigns.GetByIdAsync(campaignId, ct)
               ?? throw new CampaignNotFoundException(campaignId);
    }

    /// <summary>Insert guests, skipping any contact on the hashed suppression list (§15.3) and dupes.</summary>
    private async Task<int> MaterializeGuestsAsync(Guid campaignId, IEnumerable<ParsedGuest> parsed, CancellationToken ct)
    {
        var suppressedSet = (await suppression.ListHashesAsync(ct)).ToHashSet();
        var emailSet = (await guests.Query()
            .Where(g => g.CampaignId == campaignId && g.Email != null)
            .Select(g => g.Email!).ToListAsync(ct)).ToHashSet();
        var phoneSet = (await guests.Query()
            .Where(g => g.CampaignId == campaignId && g.PhoneE164 != null)
            .Select(g => g.PhoneE164!).ToListAsync(ct)).ToHashSet();

        var added = 0;
        foreach (var p in parsed)
        {
            if (p.Email is not null && suppressedSet.Contains(TokenService.HashContact(p.Email))) continue;
            if (p.PhoneE164 is not null && suppressedSet.Contains(TokenService.HashContact(p.PhoneE164))) continue;
            if (p.Email is not null && !emailSet.Add(p.Email)) continue;
            if (p.PhoneE164 is not null && !phoneSet.Add(p.PhoneE164)) continue;

            await guests.AddAsync(new Guest
            {
                Id = Guid.NewGuid(),
                CampaignId = campaignId,
                Email = p.Email,
                PhoneE164 = p.PhoneE164,
                PhoneRaw = p.PhoneRaw,
                Name = p.Name,
                Role = p.Role,
                Gender = p.Gender,
                MetadataJson = p.MetadataJson,
                CreatedAt = DateTimeOffset.UtcNow
            }, ct);
            added++;
        }
        return added;
    }
}
