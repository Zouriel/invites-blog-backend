using System.Text.Json;
using System.Text.Json.Nodes;
using FluentValidation;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Dtos.Campaigns;
using InvitesBlog.Application.Exceptions.Campaigns;
using InvitesBlog.Application.Phones;
using InvitesBlog.Application.Pricing;
using InvitesBlog.Application.Security;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace InvitesBlog.Application.Services.Campaigns;

/// <summary>
/// Campaign builder + no-registration access (§10.3 / §4.6). Ports the legacy CampaignsEndpoints,
/// the PaymentsEndpoints cancel flow (§14.3) and the PrivacyEndpoints hard-delete (§15.5) into the
/// layered architecture — behavior is preserved, only the plumbing changes.
/// </summary>
public sealed class CampaignService(
    ICurrentUser currentUser,
    ICampaignRepository campaigns,
    IInviterRepository inviters,
    IGuestRepository guests,
    IInviteRepository invites,
    IPaymentRepository payments,
    ITemplateRepository templates,
    IRepository<RsvpResponse> rsvpResponses,
    IRepository<DeliveryAttempt> deliveryAttempts,
    IRepository<CampaignAsset> campaignAssets,
    IRepository<UploadedGuestFile> uploadedFiles,
    IRepository<AuditLog> auditLogs,
    IRepository<Refund> refunds,
    IUnitOfWork uow,
    IEmailSender email,
    IStorageService storage,
    IPaymentProvider paymentProvider,
    PhoneNormalizer phones,
    IConfiguration config,
    IValidator<CreateCampaignRequest> createValidator,
    IValidator<UpdateContentRequest> contentValidator,
    IValidator<UpdateVenueRequest> venueValidator,
    IValidator<UpdateInviterRequest> inviterValidator,
    IValidator<UpdateDeliverySettingsRequest> deliveryValidator) : ICampaignService
{
    public async Task<CreateCampaignResponse> CreateAsync(CreateCampaignRequest req, CancellationToken ct = default)
    {
        await createValidator.ValidateAndThrowAsync(req, ct);

        var template = await templates.GetActiveByIdAsync(req.TemplateId, ct)
                       ?? throw new UnknownTemplateException();

        var rawToken = TokenService.GenerateToken();
        var now = DateTimeOffset.UtcNow;
        var campaign = new Campaign
        {
            Id = Guid.NewGuid(),
            TemplateId = template.Id,
            TemplateVersion = template.Version,
            AccessTokenHash = TokenService.Hash(rawToken),
            Title = req.Title,
            Slug = Slugify(req.Title),
            Status = CampaignStatus.Draft,
            EventStartAt = now.AddDays(30),
            CreatedAt = now,
            UpdatedAt = now
        };
        await campaigns.AddAsync(campaign, ct);
        await uow.SaveChangesAsync(ct);

        return new CreateCampaignResponse(campaign.Id, campaign.Status.ToString(), rawToken);
    }

    public async Task UpdateContentAsync(Guid id, UpdateContentRequest req, CancellationToken ct = default)
    {
        await contentValidator.ValidateAndThrowAsync(req, ct);
        var campaign = await LoadOwnedAsync(id, ct);

        if (req.CustomContentJson is not null) campaign.CustomContentJson = req.CustomContentJson;
        if (req.ThemeOverridesJson is not null) campaign.ThemeOverridesJson = req.ThemeOverridesJson;
        if (req.RulesJson is not null) campaign.RulesJson = req.RulesJson;
        if (req.IsSensitive is not null)
        {
            // Sensitive invites are OTP-gated, and OTP is email-only at launch — so every guest must
            // have an email or they could never open the invite (provider guide §1).
            if (req.IsSensitive.Value && !campaign.IsSensitive)
            {
                var list = await guests.ListByCampaignAsync(id, includeOptedOut: false, ct) ?? Array.Empty<Domain.Entities.Guest>();
                var withoutEmail = list.Count(g => string.IsNullOrWhiteSpace(g.Email));
                if (withoutEmail > 0)
                    throw new Exceptions.BusinessRuleException(
                        $"{withoutEmail} guest(s) have no email. Sensitive invites require OTP, which is email-only at launch — add emails for every guest first.",
                        "sensitive_requires_email");
            }
            campaign.IsSensitive = req.IsSensitive.Value;
        }
        if (req.EventStartAt is not null) campaign.EventStartAt = req.EventStartAt.Value;
        if (req.EventEndAt is not null) campaign.EventEndAt = req.EventEndAt;
        if (req.EventType is not null) campaign.EventType = req.EventType;
        campaign.UpdatedAt = DateTimeOffset.UtcNow;
        await uow.SaveChangesAsync(ct);
    }

    public async Task UpdateVenueAsync(Guid id, UpdateVenueRequest req, CancellationToken ct = default)
    {
        await venueValidator.ValidateAndThrowAsync(req, ct);
        var campaign = await LoadOwnedAsync(id, ct);

        var content = JsonNode.Parse(
            string.IsNullOrWhiteSpace(campaign.CustomContentJson) ? "{}" : campaign.CustomContentJson)!.AsObject();
        content["venue"] = new JsonObject
        {
            ["type"] = req.VenueType,
            ["name"] = req.VenueName,
            ["address"] = req.Address,
            ["mapLink"] = req.MapLink,
            ["city"] = req.City,
            ["room"] = req.Room,
            ["arrival"] = req.ArrivalInstructions,
            ["parking"] = req.ParkingInstructions,
            ["dressCode"] = req.DressCode
        };
        campaign.CustomContentJson = content.ToJsonString();
        if (!string.IsNullOrWhiteSpace(req.VenueType)) campaign.EventType = req.VenueType;
        campaign.UpdatedAt = DateTimeOffset.UtcNow;
        await uow.SaveChangesAsync(ct);
    }

    public async Task UpdateInviterAsync(Guid id, UpdateInviterRequest req, string? accessToken, CancellationToken ct = default)
    {
        await inviterValidator.ValidateAndThrowAsync(req, ct);
        var campaign = await LoadOwnedAsync(id, ct);

        var normEmail = req.Email.Trim().ToLowerInvariant();
        var phone = phones.Normalize(req.Phone, req.DefaultCountry ?? "MV");
        var inviter = await inviters.GetByEmailAsync(normEmail, ct);
        if (inviter is null)
        {
            inviter = new Inviter
            {
                Id = Guid.NewGuid(),
                Email = normEmail,
                Name = req.Name,
                PhoneE164 = phone.E164 ?? req.Phone ?? string.Empty,
                Organization = req.Organization,
                BillingName = req.BillingName,
                BillingCountry = req.BillingCountry,
                CreatedAt = DateTimeOffset.UtcNow
            };
            await inviters.AddAsync(inviter, ct);
        }
        else
        {
            inviter.Name = req.Name;
            inviter.PhoneE164 = phone.E164 ?? req.Phone ?? string.Empty;
            inviter.Organization = req.Organization ?? inviter.Organization;
            inviters.Update(inviter);
        }
        campaign.InviterId = inviter.Id;
        campaign.UpdatedAt = DateTimeOffset.UtcNow;
        await uow.SaveChangesAsync(ct);

        // Resume-your-invite magic link carries the caller's own access token (recovery path, §4.6.2 #4).
        var inviterBase = (config["Urls:InviterBase"] ?? "http://localhost:4200").TrimEnd('/');
        var resumeLink = $"{inviterBase}/create/{campaign.Id}/editor?resume={accessToken}";
        await email.SendAsync(new Application.Abstractions.EmailMessage(
            To: normEmail,
            Subject: "Resume your invite",
            Html: $"<p>Hi {System.Net.WebUtility.HtmlEncode(req.Name)},</p><p>Continue your invite any time:</p>" +
                  $"<p><a href=\"{resumeLink}\">Resume your invite</a></p>",
            Stream: Application.Abstractions.EmailStream.System,
            Tags: new[] { new KeyValuePair<string, string>("kind", "magic_link") }), ct);
    }

    public async Task UpdateDeliverySettingsAsync(Guid id, UpdateDeliverySettingsRequest req, CancellationToken ct = default)
    {
        await deliveryValidator.ValidateAndThrowAsync(req, ct);
        var campaign = await LoadOwnedAsync(id, ct);
        campaign.DeliverySettingsJson = req.DeliverySettingsJson;
        campaign.UpdatedAt = DateTimeOffset.UtcNow;
        await uow.SaveChangesAsync(ct);
    }

    public async Task SetRolesAsync(Guid id, SetRolesRequest req, CancellationToken ct = default)
    {
        var campaign = await LoadOwnedAsync(id, ct);

        // Clean the incoming roles (drop blank names/blocks, trim, dedupe blocks).
        var roles = (req.Roles ?? Array.Empty<RoleDefinitionDto>())
            .Where(r => !string.IsNullOrWhiteSpace(r.Name))
            .Select(r => new RoleDefinitionDto(
                r.Name.Trim(),
                (r.ContentBlocks ?? Array.Empty<string>())
                    .Where(b => !string.IsNullOrWhiteSpace(b)).Select(b => b.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase).ToList()))
            .ToList();

        // Persist the roles as authored — camelCase to match the API wire convention the apps read.
        campaign.RolesJson = JsonSerializer.Serialize(new { roles }, CamelCaseJson);

        // …and regenerate the §12 personalization rules so a guest holding a role sees its blocks.
        var rulesArray = new JsonArray();
        foreach (var role in roles)
            foreach (var block in role.ContentBlocks)
                rulesArray.Add(new JsonObject
                {
                    ["condition"] = new JsonObject { ["field"] = "role", ["operator"] = "equals", ["value"] = role.Name },
                    ["contentBlock"] = block
                });
        campaign.RulesJson = new JsonObject { ["rules"] = rulesArray }.ToJsonString();

        campaign.UpdatedAt = DateTimeOffset.UtcNow;
        await uow.SaveChangesAsync(ct);
    }

    public async Task<CampaignImageDto> AddImageAsync(
        Guid id, byte[] content, string contentType, string fileName, string? slot, CancellationToken ct = default)
    {
        var campaign = await LoadOwnedAsync(id, ct);   // possession-token ownership check (§11.2)

        if (content.Length == 0)
            throw new Exceptions.BusinessRuleException("The image file is empty.", "empty_image");
        if (content.Length > MaxImageBytes)
            throw new Exceptions.BusinessRuleException("Images must be 5 MB or smaller.", "image_too_large");
        if (string.IsNullOrWhiteSpace(contentType) || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
            throw new Exceptions.BusinessRuleException("Only image files can be uploaded here.", "not_an_image");

        var ext = System.IO.Path.GetExtension(fileName);
        if (string.IsNullOrWhiteSpace(ext)) ext = ExtensionFor(contentType);
        var key = $"campaigns/{campaign.Id:N}/images/{Guid.NewGuid():N}{ext}";
        var url = await storage.PutAsync(key, content, contentType, ct);

        await campaignAssets.AddAsync(new CampaignAsset
        {
            Id = Guid.NewGuid(),
            CampaignId = campaign.Id,
            Url = url,
            ContentType = contentType,
            SizeBytes = content.Length,
            Slot = string.IsNullOrWhiteSpace(slot) ? null : slot.Trim(),
            CreatedAt = DateTimeOffset.UtcNow
        }, ct);
        campaign.UpdatedAt = DateTimeOffset.UtcNow;
        await uow.SaveChangesAsync(ct);

        return new CampaignImageDto(url);
    }

    private static readonly JsonSerializerOptions CamelCaseJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private const long MaxImageBytes = 5 * 1024 * 1024;

    private static string ExtensionFor(string contentType) => contentType.ToLowerInvariant() switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/avif" => ".avif",
        "image/svg+xml" => ".svg",
        _ => ".img"
    };

    public async Task<CampaignSummaryDto> GetSummaryAsync(Guid id, CancellationToken ct = default)
    {
        var campaign = await LoadOwnedAsync(id, ct);
        var guestCount = await guests.CountByCampaignAsync(id, ct);
        var template = await templates.GetByIdAsync(campaign.TemplateId, ct);
        var price = PricingCalculator.CalculateInitial(
            Math.Max(guestCount, PricingCalculator.IncludedInvites), campaign.HasDesignerDiscount);

        return new CampaignSummaryDto(
            campaign.Id, campaign.Title, campaign.Slug, campaign.Status.ToString(),
            campaign.EventType, campaign.EventStartAt, campaign.EventEndAt,
            campaign.PaidInviteCapacity, campaign.HasDesignerDiscount, campaign.IsSensitive,
            campaign.CustomContentJson, campaign.ThemeOverridesJson, campaign.RulesJson, campaign.RolesJson, campaign.DeliverySettingsJson,
            guestCount,
            template is null ? null : new CampaignSummaryTemplateDto(template.Name, template.Slug, template.PackageUrl, template.ManifestJson),
            price);
    }

    public async Task<PriceBreakdown> GetPricingAsync(Guid id, int? inviteCount, CancellationToken ct = default)
    {
        var campaign = await LoadOwnedAsync(id, ct);
        var count = inviteCount ?? await guests.CountByCampaignAsync(id, ct);
        return PricingCalculator.CalculateInitial(count, campaign.HasDesignerDiscount);
    }

    public async Task ResendLinkAsync(ResendLinkRequest req, CancellationToken ct = default)
    {
        // Anonymous, rate-limited. Always succeeds so it never leaks which emails exist (§4.6).
        var normEmail = req.Email.Trim().ToLowerInvariant();
        var inviter = await inviters.GetByEmailAsync(normEmail, ct);
        if (inviter is null) return;

        var owned = await campaigns.Query(tracking: true).Where(c => c.InviterId == inviter.Id).ToListAsync(ct);
        var inviterBase = (config["Urls:InviterBase"] ?? "http://localhost:4200").TrimEnd('/');

        // Regenerate dashboard links (we only stored hashes) and email them.
        var links = new List<string>();
        foreach (var c in owned)
        {
            var raw = TokenService.GenerateToken();
            c.DashboardTokenHash = TokenService.Hash(raw);
            links.Add($"{inviterBase}/dashboard/{c.Id}?token={raw}");
        }
        await uow.SaveChangesAsync(ct);

        if (links.Count > 0)
            await email.SendAsync(new Application.Abstractions.EmailMessage(
                To: normEmail,
                Subject: "Your invites.blog links",
                Html: "<p>Here are your campaign links:</p><ul>" +
                      string.Join("", links.Select(l => $"<li><a href=\"{l}\">{l}</a></li>")) + "</ul>",
                Stream: Application.Abstractions.EmailStream.System,
                Tags: new[] { new KeyValuePair<string, string>("kind", "magic_link") }), ct);
    }

    public async Task<DashboardResponse> GetDashboardAsync(Guid id, string? token, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token)) throw new InvalidDashboardTokenException();
        var hash = TokenService.Hash(token);
        var campaign = await campaigns.GetByDashboardTokenHashAsync(id, hash, ct)
                       ?? throw new InvalidDashboardTokenException();

        var guestList = await guests.ListByCampaignAsync(id, includeOptedOut: true, ct);
        var inviteList = await invites.ListByCampaignAsync(id, ct);

        var report = new DashboardReportDto(
            Total: inviteList.Count,
            Sent: inviteList.Count(i => i.Status is InviteStatus.Sent or InviteStatus.Viewed),
            Failed: inviteList.Count(i => i.Status == InviteStatus.Failed),
            Viewed: inviteList.Count(i => i.ViewedAt != null),
            NotSent: inviteList.Count(i => i.Status == InviteStatus.NotSent),
            Rsvp: new DashboardRsvpDto(
                Going: inviteList.Count(i => i.RsvpStatus == RsvpStatus.Going),
                Maybe: inviteList.Count(i => i.RsvpStatus == RsvpStatus.Maybe),
                NotGoing: inviteList.Count(i => i.RsvpStatus == RsvpStatus.NotGoing)));

        // Latest real delivery channel per invite ("viber"/"email"), so the dashboard can show
        // "via Viber" / "via email". The "none" placeholder channel (not-sent) is excluded.
        var inviteIds = inviteList.Select(i => i.Id).ToList();
        var attemptList = await deliveryAttempts.Query()
            .Where(a => inviteIds.Contains(a.InviteId) && a.Channel != "none")
            .ToListAsync(ct);
        var latestChannel = attemptList
            .GroupBy(a => a.InviteId)
            .ToDictionary(grp => grp.Key, grp => grp.OrderByDescending(a => a.AttemptedAt).First().Channel);

        var guestRows = guestList.Select(g =>
        {
            var inv = inviteList.FirstOrDefault(i => i.GuestId == g.Id);
            var channel = inv is not null && latestChannel.TryGetValue(inv.Id, out var ch) ? ch : null;
            return new DashboardGuestDto(
                g.Id, g.Name, g.Email, g.PhoneE164, g.Role, g.Gender, g.OptedOut,
                inv?.Status.ToString() ?? "None",
                inv?.RsvpStatus.ToString() ?? "NoResponse",
                inv?.ViewedAt,
                channel);
        }).ToList();

        return new DashboardResponse(
            new DashboardCampaignDto(campaign.Id, campaign.Title, campaign.Status.ToString(), campaign.PaidInviteCapacity),
            report, guestRows);
    }

    public async Task<CancelCampaignResponse> CancelAsync(Guid id, CancellationToken ct = default)
    {
        var campaign = await LoadOwnedAsync(id, ct);

        var dispatched = campaign.Status is CampaignStatus.Dispatched
            or CampaignStatus.PartiallyDispatched or CampaignStatus.Dispatching;

        if (!dispatched)
        {
            // Pre-dispatch: auto full refund of any paid initial payment (§14.3).
            var paid = await payments.Query(tracking: true)
                .Where(p => p.CampaignId == id && p.Status == PaymentStatus.Paid).ToListAsync(ct);
            foreach (var p in paid)
            {
                var refundRes = await paymentProvider.RefundAsync(
                    new RefundRequest(p.ProviderPaymentId ?? p.ProviderSessionId ?? "", p.Amount), ct);
                await refunds.AddAsync(new Refund
                {
                    Id = Guid.NewGuid(),
                    PaymentId = p.Id,
                    Amount = p.Amount,
                    Status = refundRes.Success ? RefundStatus.Succeeded : RefundStatus.Failed,
                    ProviderRefundId = refundRes.ProviderRefundId,
                    CreatedAt = DateTimeOffset.UtcNow
                }, ct);
                p.Status = PaymentStatus.Refunded;
            }
            campaign.Status = CampaignStatus.Cancelled;
            await uow.SaveChangesAsync(ct);
            return new CancelCampaignResponse(true, paid.Count > 0);
        }

        // Post-dispatch: graceful cancellation — invite pages show a cancellation notice.
        campaign.Status = CampaignStatus.Cancelled;
        await uow.SaveChangesAsync(ct);
        return new CancelCampaignResponse(true, false, "Invite links now show a cancellation notice.");
    }

    public async Task<DeleteCampaignResponse> DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var campaign = await LoadOwnedAsync(id, ct);

        var guestList = await guests.ListByCampaignAsync(id, includeOptedOut: true, ct);
        var inviteList = await invites.ListByCampaignAsync(id, ct);
        var inviteIds = inviteList.Select(i => i.Id).ToList();

        var attempts = await deliveryAttempts.ListAsync(a => inviteIds.Contains(a.InviteId), ct);
        var responses = await rsvpResponses.ListAsync(r => inviteIds.Contains(r.InviteId), ct);
        var files = await uploadedFiles.ListAsync(u => u.CampaignId == id, ct);
        var assets = await campaignAssets.ListAsync(a => a.CampaignId == id, ct);

        deliveryAttempts.RemoveRange(attempts);
        rsvpResponses.RemoveRange(responses);
        invites.RemoveRange(inviteList);
        guests.RemoveRange(guestList);
        uploadedFiles.RemoveRange(files);
        campaignAssets.RemoveRange(assets);

        await auditLogs.AddAsync(new AuditLog
        {
            Id = Guid.NewGuid(),
            Action = "campaign.delete",
            Actor = "inviter",
            CampaignId = id,
            DataJson = JsonSerializer.Serialize(new { guests = guestList.Count }),
            CreatedAt = DateTimeOffset.UtcNow
        }, ct);

        campaigns.Remove(campaign);
        await uow.SaveChangesAsync(ct);
        return new DeleteCampaignResponse(true);
    }

    /// <summary>
    /// Loads the campaign and enforces that the caller's possession token maps to THIS campaign
    /// (§4.6.2 / §11.2). Returns a tracked entity so mutations flush on the shared unit of work.
    /// </summary>
    private async Task<Campaign> LoadOwnedAsync(Guid id, CancellationToken ct)
    {
        if (currentUser.CampaignId != id) throw new CampaignAccessDeniedException();
        return await campaigns.GetByIdAsync(id, ct) ?? throw new CampaignNotFoundException(id);
    }

    private static string Slugify(string input)
    {
        var slug = new string(input.ToLowerInvariant()
            .Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
        while (slug.Contains("--")) slug = slug.Replace("--", "-");
        slug = slug.Trim('-');
        return string.IsNullOrEmpty(slug) ? "event" : $"{slug}-{Guid.NewGuid().ToString("N")[..6]}";
    }
}
