using System.Net;
using System.Text.Json;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Common;
using InvitesBlog.Application.Dtos.Designs;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Services.Campaigns;
using InvitesBlog.Application.Services.Designers;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace InvitesBlog.Application.Services.Designs;

/// <summary>The design failed Check at publish; <see cref="AppException.Errors"/> lists why.</summary>
public sealed class DesignCheckFailedException(IReadOnlyList<ApiError> errors)
    : AppException("Fix the problems Check found before publishing.", HttpStatusCode.BadRequest, "design_check_failed", errors);

public interface IDesignService
{
    object Catalog();
    Task<IReadOnlyList<DesignSummaryDto>> ListMineAsync(CancellationToken ct = default);
    Task<DesignDto> GetAsync(Guid id, CancellationToken ct = default);
    Task<DesignDto> CreateAsync(CreateDesignRequest request, CancellationToken ct = default);
    Task<SavedDesignDto> SaveAsync(Guid id, SaveDesignRequest request, CancellationToken ct = default);
    Task<DesignDto> DuplicateAsync(Guid id, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    DesignPreviewDto Preview(DesignPreviewRequest request);
    Task<DesignPreviewDto> CheckAsync(Guid id, CancellationToken ct = default);
    DesignAssetDto ImportAsset(byte[] content, string fileName, string contentType);
    Task<DesignImportSourceDto> ImportSourceAsync(Guid templateId, CancellationToken ct = default);
    Task<PublishResultDto> PublishAsync(Guid id, PublishDesignRequest request, CancellationToken ct = default);
    Task<DesignDto> SetVisibilityAsync(Guid id, SetDesignVisibilityRequest request, CancellationToken ct = default);
    Task<IReadOnlyList<DesignEventDto>> EventsAsync(Guid id, CancellationToken ct = default);
    Task<DesignEventDto> UpgradeEventAsync(Guid id, Guid campaignId, CancellationToken ct = default);
}

/// <summary>
/// The visual designer's API. Everything that reaches a guest is compiled HERE from the scene the
/// server holds; nothing the browser sends as HTML is ever stored or served.
///
/// <para><b>Who can do what.</b> A design belongs to one account and only that account opens it.
/// Editing an existing template needs to own it (<see cref="Template.DesignerUserId"/>) or hold
/// <see cref="Permissions.Templates.Manage"/>, which is how an admin reworks a platform template.</para>
///
/// <para><b>Publishing without review.</b> Public publishes go live immediately. What stands in for a
/// reviewer: the server-side compile and Check, a per-account daily limit, reports, and admin unlist /
/// remove (<see cref="TemplateReportService"/>).</para>
/// </summary>
public sealed class DesignService(
    ICurrentUser currentUser,
    IRepository<TemplateDesign> designs,
    IRepository<TemplateDesignPublish> publishes,
    IRepository<AppUser> users,
    IRepository<TemplateType> templateTypes,
    ITemplateRepository templates,
    ICampaignRepository campaigns,
    ICampaignOwnershipService ownership,
    ICampaignService campaignService,
    IDesignEngine engine,
    ITemplatePackager packager,
    IStorageService storage,
    IEmailSender email,
    IConfiguration config,
    IUnitOfWork uow) : IDesignService
{
    public const int PublicPublishesPerDay = 5;
    public const int MaxDesignsPerAccount = 200;
    public const int MaxPosterBytes = 2 * 1024 * 1024;

    public object Catalog() => engine.Catalog();

    public async Task<IReadOnlyList<DesignSummaryDto>> ListMineAsync(CancellationToken ct = default)
    {
        var me = Me();
        var rows = await designs.Query()
            .Where(d => d.OwnerUserId == me)
            .OrderByDescending(d => d.UpdatedAt)
            .ToListAsync(ct);
        var templateInfo = await TemplateInfoAsync(rows.Select(r => r.TemplateId).OfType<Guid>().ToList(), ct);

        return rows.Select(d => new DesignSummaryDto(
            d.Id, d.Name, d.Revision, d.PublishedRevision, d.UpdatedAt, d.LastPublishedAt,
            d.TemplateId is { } tid ? templateInfo.GetValueOrDefault(tid) : null)).ToList();
    }

    public async Task<DesignDto> GetAsync(Guid id, CancellationToken ct = default) =>
        await ToDtoAsync(await LoadOwnedAsync(id, ct), ct);

    public async Task<DesignDto> CreateAsync(CreateDesignRequest request, CancellationToken ct = default)
    {
        var me = Me();
        if (await designs.CountAsync(d => d.OwnerUserId == me, ct) >= MaxDesignsPerAccount)
            throw new BusinessRuleException(
                $"You have {MaxDesignsPerAccount} designs — delete some you no longer need first.", "design_limit");

        if (request.CampaignId is { } campaignId && !await ownership.OwnsAsync(campaignId, ct))
            throw new ForbiddenException("That event isn't yours.");

        string sceneJson;
        string name;
        Guid? templateId = null;
        int? publishedRevision = null;

        if (request.FromTemplateId is { } fromTemplate)
        {
            var template = await LoadEditableTemplateAsync(fromTemplate, ct);

            // One design per template per person: opening it again resumes, it doesn't fork.
            var existing = await designs.Query(tracking: true)
                .FirstOrDefaultAsync(d => d.OwnerUserId == me && d.TemplateId == template.Id, ct);
            if (existing is not null) return await ToDtoAsync(existing, ct);

            if (engine.IsDesignedScene(template.SceneJson))
            {
                sceneJson = engine.Normalize(template.SceneJson);
                publishedRevision = 1;
            }
            else if (request.Scene is { ValueKind: JsonValueKind.Object } imported)
            {
                // Converted in the browser from the hand-written HTML. Not what is live yet — the live
                // version is still the original — so it starts as unpublished changes.
                sceneJson = engine.Normalize(imported.GetRawText());
            }
            else
            {
                throw new BusinessRuleException(
                    "That template was written by hand, so it has to be converted before it can be edited.", "import_required");
            }
            name = template.Name;
            templateId = template.Id;
        }
        else if (request.Scene is { ValueKind: JsonValueKind.Object } scene)
        {
            sceneJson = engine.Normalize(scene.GetRawText());
            name = "Imported design";
        }
        else
        {
            var starter = string.IsNullOrWhiteSpace(request.Starter) ? "blank" : request.Starter.Trim();
            sceneJson = engine.Starter(starter)
                        ?? throw new BusinessRuleException("That starter doesn't exist.", "starter_unknown");
            name = "Untitled template";
        }

        if (!string.IsNullOrWhiteSpace(request.Name)) name = CleanName(request.Name);

        var now = DateTimeOffset.UtcNow;
        var design = new TemplateDesign
        {
            Id = Guid.NewGuid(),
            OwnerUserId = me,
            Name = name,
            SceneJson = sceneJson,
            Revision = 1,
            TemplateId = templateId,
            PublishedRevision = publishedRevision,
            CampaignId = request.CampaignId,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await designs.AddAsync(design, ct);
        await uow.SaveChangesAsync(ct);
        return await ToDtoAsync(design, ct);
    }

    public async Task<SavedDesignDto> SaveAsync(Guid id, SaveDesignRequest request, CancellationToken ct = default)
    {
        var design = await LoadOwnedAsync(id, ct, tracking: true);
        if (request.BaseRevision != design.Revision)
            throw new InvalidStateException(
                "This design was changed somewhere else — reload to get the latest version.", "design_conflict");

        design.SceneJson = engine.Normalize(request.Scene.GetRawText());
        if (!string.IsNullOrWhiteSpace(request.Name)) design.Name = CleanName(request.Name);
        design.Revision++;
        design.UpdatedAt = DateTimeOffset.UtcNow;
        await uow.SaveChangesAsync(ct);
        return new SavedDesignDto(design.Revision, design.UpdatedAt);
    }

    public async Task<DesignDto> DuplicateAsync(Guid id, CancellationToken ct = default)
    {
        var source = await LoadOwnedAsync(id, ct);
        var me = Me();
        if (await designs.CountAsync(d => d.OwnerUserId == me, ct) >= MaxDesignsPerAccount)
            throw new BusinessRuleException(
                $"You have {MaxDesignsPerAccount} designs — delete some you no longer need first.", "design_limit");

        var now = DateTimeOffset.UtcNow;
        var copy = new TemplateDesign
        {
            Id = Guid.NewGuid(),
            OwnerUserId = me,
            // A copy is a new template: it doesn't publish over the original.
            Name = CleanName($"{source.Name} (copy)"),
            SceneJson = source.SceneJson,
            Revision = 1,
            CreatedAt = now,
            UpdatedAt = now,
        };
        await designs.AddAsync(copy, ct);
        await uow.SaveChangesAsync(ct);
        return await ToDtoAsync(copy, ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var design = await LoadOwnedAsync(id, ct, tracking: true);
        // The published template (if any) is left alone: events use it, and it's managed from My templates.
        designs.Remove(design);
        await uow.SaveChangesAsync(ct);
    }

    public DesignPreviewDto Preview(DesignPreviewRequest request)
    {
        Me();
        var sample = request.Sample is "empty" or "roles" ? request.Sample : "filled";
        var build = engine.Build(request.Scene.GetRawText(), new DesignBuildOptions(
            EditorPreview: request.Editor ?? true,
            Sample: sample,
            Blocks: request.Blocks?.ToList(),
            Hidden: request.Hidden?.ToHashSet(StringComparer.Ordinal),
            Scroll: Math.Max(0, request.Scroll ?? 0)));
        return new DesignPreviewDto(build.Html, build.Bytes, build.Issues, build.Structure, !build.Errors);
    }

    public async Task<DesignPreviewDto> CheckAsync(Guid id, CancellationToken ct = default)
    {
        var design = await LoadOwnedAsync(id, ct);
        var build = engine.Build(design.SceneJson, new DesignBuildOptions(Title: design.Name));
        return new DesignPreviewDto(string.Empty, build.Bytes, build.Issues, build.Structure, !build.Errors);
    }

    public DesignAssetDto ImportAsset(byte[] content, string fileName, string contentType)
    {
        Me();
        return engine.ImportAsset(content, fileName, contentType);
    }

    public async Task<DesignImportSourceDto> ImportSourceAsync(Guid templateId, CancellationToken ct = default)
    {
        var template = await LoadEditableTemplateAsync(templateId, ct);
        var me = Me();
        var existing = await designs.Query()
            .Where(d => d.OwnerUserId == me && d.TemplateId == template.Id)
            .Select(d => (Guid?)d.Id)
            .FirstOrDefaultAsync(ct);

        if (existing is not null || engine.IsDesignedScene(template.SceneJson))
            return new DesignImportSourceDto(template.Id, template.Name, template.Version, true, null, existing);

        var html = await packager.ReadPackageAsync(template.Slug, template.Version, ct)
                   ?? throw new NotFoundException("That template's files aren't available to convert.", "source_not_found");
        return new DesignImportSourceDto(template.Id, template.Name, template.Version, false, engine.ImportDocument(html), null);
    }

    public async Task<PublishResultDto> PublishAsync(Guid id, PublishDesignRequest request, CancellationToken ct = default)
    {
        var me = Me();
        var design = await LoadOwnedAsync(id, ct, tracking: true);
        if (request.Revision != design.Revision)
            throw new InvalidStateException(
                "The design changed after you opened Publish — check it again and publish.", "design_conflict");

        var visibility = request.Visibility switch
        {
            "Public" => TemplateVisibility.Public,
            "Private" or "Person" => TemplateVisibility.Private,
            _ => throw new BusinessRuleException("Choose who can use this template.", "visibility_required"),
        };
        // "Person": private, and reserved for one other account — they use it, nobody else does.
        string? assignedEmail = null;
        if (request.Visibility == "Person")
        {
            assignedEmail = NormalizeEmail(request.AssignedEmail)
                ?? throw new BusinessRuleException("Enter the email address of the person it's for.", "assigned_email_required");
            var ownEmail = (await users.GetByIdAsync(me, ct))?.Email;
            if (string.Equals(assignedEmail, NormalizeEmail(ownEmail), StringComparison.Ordinal))
                throw new BusinessRuleException("That's your own email — choose Private to keep it for your events.", "assigned_email_self");
        }
        var name = CleanName(request.Name);
        var description = (request.Description ?? string.Empty).Trim();
        if (description.Length > 500) description = description[..500];

        var type = await templateTypes.Query()
            .FirstOrDefaultAsync(t => t.IsActive && t.Name == request.Category, ct)
            ?? throw new BusinessRuleException("Choose what kind of event this template is for.", "category_required");

        Template? template = null;
        if (design.TemplateId is { } linked)
        {
            template = await templates.Query(tracking: true).FirstOrDefaultAsync(t => t.Id == linked, ct);
            if (template is not null && !CanEdit(template))
                throw new ForbiddenException("You can no longer publish over that template.", "not_your_template");
        }

        var effectiveVisibility = template?.Visibility == TemplateVisibility.Dedicated ? TemplateVisibility.Dedicated : visibility;
        if (effectiveVisibility == TemplateVisibility.Public)
        {
            await EnsureCanPublishPubliclyAsync(me, template, ct);
            if (description.Length < 10)
                throw new BusinessRuleException("Describe the template in a sentence — the gallery shows it.", "description_required");
        }

        var build = engine.Build(design.SceneJson, new DesignBuildOptions(Title: name));
        if (build.Errors)
            throw new DesignCheckFailedException(build.Issues
                .Where(i => i.Severity == "error")
                .Select(i => new ApiError(i.Message, i.ElementId, i.Code))
                .ToList());

        var slug = template?.Slug ?? Slugify(name);
        var version = template is null ? "1.0.0" : TemplateVersion.Next(template.Version);

        // The published document is compiled from the stored scene just now, never from anything the browser sent.
        var package = await packager.PublishAsync($"templates/{slug}@{version}", slug, version, build.Html, ct);

        string? posterUrl = null;
        if (request.Poster is { Length: > 0 } poster)
        {
            var sniffed = ImageSniffer.Detect(poster);
            if (sniffed is not ("image/png" or "image/jpeg" or "image/webp"))
                throw new BusinessRuleException("The preview image has to be a PNG, JPEG or WebP.", "poster_invalid");
            if (poster.Length > MaxPosterBytes)
                throw new BusinessRuleException("The preview image is too large — keep it under 2MB.", "poster_too_large");
            var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(poster))[..8].ToLowerInvariant();
            posterUrl = await storage.PutAsync(
                $"templates/{slug}@{version}/poster.{hash}{MediaFileTypes.ExtensionFor(sniffed)}", poster, sniffed, ct);
        }

        var owner = await users.GetByIdAsync(me, ct);
        var now = DateTimeOffset.UtcNow;
        if (template is null)
        {
            template = new Template
            {
                Id = Guid.NewGuid(),
                Slug = slug,
                CreatedAt = now,
                DesignerUserId = me,
                DesignerName = owner?.DisplayName ?? "Community designer",
            };
            await templates.AddAsync(template, ct);
        }
        template.Name = name;
        template.Category = type.Name;
        template.Description = description.Length > 0 ? description : $"A {type.Name.ToLowerInvariant()} invitation.";
        template.Version = version;
        template.ManifestJson = package.ManifestJson;
        template.PackageUrl = package.PackageUrl;
        template.SceneJson = design.SceneJson;
        var previouslyAssigned = template.AssignedEmail;
        template.Visibility = effectiveVisibility;
        // A dedicated template keeps the email it was made for; anything else is reserved only when asked.
        if (effectiveVisibility != TemplateVisibility.Dedicated) template.AssignedEmail = assignedEmail;
        template.IsActive = true;
        // A new poster wins; without one, a real poster from an earlier version is kept. Anything else
        // is stored as "no poster" (empty — the column is non-null), never the live page as a
        // stand-in: that is a page, not an image, and every reader goes through TemplatePoster.OrNull.
        if (posterUrl is not null) template.PreviewImageUrl = posterUrl;
        else if (TemplatePoster.OrNull(template.PreviewImageUrl) is null) template.PreviewImageUrl = string.Empty;
        template.UpdatedAt = now;

        design.Name = name;
        design.TemplateId = template.Id;
        design.PublishedRevision = design.Revision;
        design.LastPublishedAt = now;
        design.UpdatedAt = now;

        await publishes.AddAsync(new TemplateDesignPublish
        {
            Id = Guid.NewGuid(),
            DesignId = design.Id,
            UserId = me,
            TemplateId = template.Id,
            Version = version,
            Visibility = effectiveVisibility,
            Revision = design.Revision,
            Bytes = build.Bytes,
            PublishedAt = now,
        }, ct);
        await uow.SaveChangesAsync(ct);

        // Attaching is the last step and allowed to fail on its own: the template is published either
        // way, and an event that already has an invitation simply keeps it. A template made for someone
        // else is theirs to use, so it never becomes the designer's own event's invitation.
        Guid? attached = null;
        var campaignToAttach = request.CampaignId ?? design.CampaignId;
        if (assignedEmail is null && campaignToAttach is { } campaignId && await ownership.OwnsAsync(campaignId, ct))
        {
            var campaign = await campaigns.GetByIdAsync(campaignId, ct);
            if (campaign is not null && string.IsNullOrWhiteSpace(campaign.TemplatePackageUrl))
            {
                await campaignService.AttachTemplateAsync(campaignId, template.Id, ct);
                attached = campaignId;
            }
        }

        // Tell the person when it's newly theirs; a new version for the same person doesn't need a second email.
        if (assignedEmail is not null && !string.Equals(previouslyAssigned, assignedEmail, StringComparison.Ordinal))
        {
            try { await email.SendAsync(BuildMadeForYouEmail(assignedEmail, template.DesignerName ?? "A designer", template.Name), ct); }
            catch { /* the template is published either way; it's in their templates when they sign in */ }
        }

        return new PublishResultDto(
            await ToDtoAsync(design, ct), template.Id, template.Slug, version, effectiveVisibility,
            build.Issues.Where(i => i.Severity != "error").ToList(), attached);
    }

    public async Task<DesignDto> SetVisibilityAsync(Guid id, SetDesignVisibilityRequest request, CancellationToken ct = default)
    {
        var me = Me();
        var design = await LoadOwnedAsync(id, ct, tracking: true);
        var template = design.TemplateId is { } tid
            ? await templates.Query(tracking: true).FirstOrDefaultAsync(t => t.Id == tid, ct)
            : null;
        if (template is null)
            throw new BusinessRuleException("Publish the design first.", "not_published");
        if (!CanEdit(template))
            throw new ForbiddenException("That isn't your template.", "not_your_template");
        if (template.Visibility == TemplateVisibility.Dedicated)
            throw new BusinessRuleException("This template was made for one customer — it stays with them and out of the gallery.", "dedicated_template");
        if (template.AssignedEmail is not null && request.Visibility == "Public")
            throw new BusinessRuleException("This template was made for someone — it stays with them and out of the gallery.", "assigned_template");

        switch (request.Visibility)
        {
            case "Public" when template.Visibility != TemplateVisibility.Public:
                await EnsureCanPublishPubliclyAsync(me, template, ct);
                if (string.IsNullOrWhiteSpace(template.Description) || template.Description.Trim().Length < 10)
                    throw new BusinessRuleException("Add a description before putting this in the gallery.", "description_required");
                template.Visibility = TemplateVisibility.Public;
                // Listing counts toward the daily limit the same as a publish does.
                await publishes.AddAsync(new TemplateDesignPublish
                {
                    Id = Guid.NewGuid(), DesignId = design.Id, UserId = me, TemplateId = template.Id,
                    Version = template.Version, Visibility = TemplateVisibility.Public,
                    Revision = design.PublishedRevision ?? design.Revision, Bytes = 0, PublishedAt = DateTimeOffset.UtcNow,
                }, ct);
                break;
            case "Private":
                // Unlisting is always allowed; invitations already made with it are untouched.
                template.Visibility = TemplateVisibility.Private;
                break;
            case "Public":
                break;
            default:
                throw new BusinessRuleException("Choose Private or Public.", "visibility_required");
        }
        template.UpdatedAt = DateTimeOffset.UtcNow;
        await uow.SaveChangesAsync(ct);
        return await ToDtoAsync(design, ct);
    }

    public async Task<IReadOnlyList<DesignEventDto>> EventsAsync(Guid id, CancellationToken ct = default)
    {
        var design = await LoadOwnedAsync(id, ct);
        if (design.TemplateId is not { } templateId) return [];
        var template = await templates.GetByIdAsync(templateId, ct);
        if (template is null) return [];

        // Only the caller's own events — a public template's other users are nobody else's business.
        // Ownership has several routes (creator, host contact, managing celebrant), so it is decided
        // per event by the same check every campaign action uses.
        var candidates = await campaigns.Query()
            .Where(c => c.TemplateId == templateId)
            .OrderByDescending(c => c.CreatedAt)
            .Take(300)
            .Select(c => new { c.Id, c.Title, c.TemplateVersion, c.Status, c.EventStartAt })
            .ToListAsync(ct);
        var rows = new List<(Guid Id, string Title, string TemplateVersion, Domain.Enums.CampaignStatus Status, DateTimeOffset EventStartAt)>();
        foreach (var c in candidates)
            if (await ownership.OwnsAsync(c.Id, ct)) rows.Add((c.Id, c.Title, c.TemplateVersion, c.Status, c.EventStartAt));

        return rows.Select(c => new DesignEventDto(
            c.Id, c.Title, c.TemplateVersion, c.TemplateVersion == template.Version, c.Status.ToString(), c.EventStartAt)).ToList();
    }

    public async Task<DesignEventDto> UpgradeEventAsync(Guid id, Guid campaignId, CancellationToken ct = default)
    {
        var design = await LoadOwnedAsync(id, ct);
        if (design.TemplateId is not { } templateId)
            throw new BusinessRuleException("Publish the design first.", "not_published");
        if (!await ownership.OwnsAsync(campaignId, ct))
            throw new ForbiddenException("That event isn't yours.");

        var template = await templates.GetByIdAsync(templateId, ct)
                       ?? throw new NotFoundException("That template no longer exists.", "template_not_found");
        var campaign = await campaigns.Query(tracking: true).FirstOrDefaultAsync(c => c.Id == campaignId, ct)
                       ?? throw new NotFoundException("That event no longer exists.");
        if (campaign.TemplateId != template.Id)
            throw new BusinessRuleException("That event uses a different template.", "template_mismatch");

        // Re-pinning is the owner's explicit choice: every invitation for this event renders the new
        // version from now on. Values for fields the new version dropped stay stored and simply go unused.
        campaign.TemplateVersion = template.Version;
        campaign.TemplatePackageUrl = template.PackageUrl;
        campaign.TemplateManifestJson = string.IsNullOrWhiteSpace(template.ManifestJson) ? "{}" : template.ManifestJson;
        campaign.UpdatedAt = DateTimeOffset.UtcNow;
        await uow.SaveChangesAsync(ct);

        return new DesignEventDto(campaign.Id, campaign.Title, campaign.TemplateVersion, true, campaign.Status.ToString(), campaign.EventStartAt);
    }

    // ----- Helpers -----------------------------------------------------------------------------------

    private Guid Me() => currentUser.UserId ?? throw new UnauthorizedException("Sign in to design templates.");

    private async Task<TemplateDesign> LoadOwnedAsync(Guid id, CancellationToken ct, bool tracking = false)
    {
        var me = Me();
        var design = await designs.Query(tracking).FirstOrDefaultAsync(d => d.Id == id, ct);
        // Someone else's design is reported as missing, not forbidden — its existence isn't theirs to learn.
        if (design is null || design.OwnerUserId != me)
            throw new NotFoundException("That design doesn't exist.", "design_not_found");
        return design;
    }

    private bool CanEdit(Template template) =>
        currentUser.HasPermission(Permissions.Templates.Manage)
        || (currentUser.UserId is { } me && template.DesignerUserId == me);

    private async Task<Template> LoadEditableTemplateAsync(Guid templateId, CancellationToken ct)
    {
        var template = await templates.GetByIdAsync(templateId, ct);
        if (template is null || template.Visibility == TemplateVisibility.Imported || !CanEdit(template))
            throw new NotFoundException("That template isn't one you can edit.", "template_not_editable");
        return template;
    }

    private async Task<int> PublicPublishesTodayAsync(Guid me, CancellationToken ct)
    {
        var since = DateTimeOffset.UtcNow.AddDays(-1);
        return await publishes.CountAsync(p => p.UserId == me && p.Visibility == TemplateVisibility.Public && p.PublishedAt > since, ct);
    }

    private async Task<string?> PublicBlockedReasonAsync(Guid me, Template? template, CancellationToken ct)
    {
        var user = await users.GetByIdAsync(me, ct);
        if (user?.PublicPublishingRevokedAt is not null)
            return "Publishing to the gallery was turned off for this account after a template was removed.";
        if (template?.UnlistedByAdminAt is not null)
            return "This template was taken out of the gallery after a report. You can still use it privately.";
        if (!currentUser.HasPermission(Permissions.Templates.Manage)
            && await PublicPublishesTodayAsync(me, ct) >= PublicPublishesPerDay)
            return $"You've published {PublicPublishesPerDay} templates to the gallery today — try again tomorrow, or publish privately.";
        return null;
    }

    private async Task EnsureCanPublishPubliclyAsync(Guid me, Template? template, CancellationToken ct)
    {
        var reason = await PublicBlockedReasonAsync(me, template, ct);
        if (reason is not null) throw new ForbiddenException(reason, "public_publishing_blocked");
    }

    private async Task<DesignDto> ToDtoAsync(TemplateDesign design, CancellationToken ct)
    {
        var me = Me();
        var info = design.TemplateId is { } tid ? (await TemplateInfoAsync([tid], ct)).GetValueOrDefault(tid) : null;
        var history = await publishes.Query()
            .Where(p => p.DesignId == design.Id)
            .OrderByDescending(p => p.PublishedAt)
            .Take(20)
            .Select(p => new DesignPublishDto(p.Version, p.Visibility, p.Revision, p.Bytes, p.PublishedAt))
            .ToListAsync(ct);
        var template = design.TemplateId is { } id2 ? await templates.GetByIdAsync(id2, ct) : null;
        var blocked = await PublicBlockedReasonAsync(me, template, ct);
        var left = currentUser.HasPermission(Permissions.Templates.Manage)
            ? PublicPublishesPerDay
            : Math.Max(0, PublicPublishesPerDay - await PublicPublishesTodayAsync(me, ct));

        // Normalised on the way out too, so a design saved by an older editor opens already converted.
        string sceneJson;
        try { sceneJson = engine.Normalize(design.SceneJson); }
        catch (BusinessRuleException) { sceneJson = design.SceneJson; }
        using var doc = JsonDocument.Parse(sceneJson);
        return new DesignDto(
            design.Id, design.Name, doc.RootElement.Clone(), design.Revision, design.PublishedRevision,
            design.UpdatedAt, design.CampaignId, info, history, blocked, left);
    }

    private async Task<Dictionary<Guid, DesignTemplateDto>> TemplateInfoAsync(IReadOnlyCollection<Guid> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return new();
        var rows = await templates.Query().Where(t => ids.Contains(t.Id)).ToListAsync(ct);
        var usage = await campaigns.Query()
            .Where(c => ids.Contains(c.TemplateId))
            .GroupBy(c => new { c.TemplateId, c.TemplateVersion })
            .Select(g => new { g.Key.TemplateId, g.Key.TemplateVersion, Count = g.Count() })
            .ToListAsync(ct);

        return rows.ToDictionary(t => t.Id, t =>
        {
            var mine = usage.Where(u => u.TemplateId == t.Id).ToList();
            return new DesignTemplateDto(
                t.Id, t.Name, t.Slug, t.Version, t.Visibility, t.IsActive, t.Category, t.Description,
                TemplatePoster.OrNull(t.PreviewImageUrl), t.UnlistedByAdminAt is not null,
                mine.Sum(u => u.Count),
                mine.Where(u => u.TemplateVersion != t.Version).Sum(u => u.Count),
                t.Visibility == TemplateVisibility.Private ? t.AssignedEmail : null);
        });
    }

    private static string? NormalizeEmail(string? value)
    {
        var e = (value ?? string.Empty).Trim().ToLowerInvariant();
        return e.Length is > 3 and <= 254 && System.Text.RegularExpressions.Regex.IsMatch(e, @"^[^@\s]+@[^@\s]+\.[^@\s]+$") ? e : null;
    }

    /// <summary>Tells someone a designer made a template for them, and where to find it.</summary>
    private EmailMessage BuildMadeForYouEmail(string to, string designerName, string templateName)
    {
        var inviterBase = config.InviterBase();
        var link = $"{inviterBase}/my-templates?tab=requests";
        var safeDesigner = System.Net.WebUtility.HtmlEncode(designerName);
        var safeTpl = System.Net.WebUtility.HtmlEncode(templateName);
        var html =
            "<div style=\"font-family:-apple-system,'Segoe UI',Roboto,Arial,sans-serif;max-width:520px;margin:0 auto;padding:24px;color:#152026\">" +
            "<p style=\"font-size:16px;line-height:1.6\">Hello,</p>" +
            $"<p style=\"font-size:16px;line-height:1.6\"><strong>{safeDesigner}</strong> designed an invitation template just for you: <strong>{safeTpl}</strong>. Only you can use it.</p>" +
            $"<p style=\"text-align:center;margin:28px 0\"><a href=\"{link}\" style=\"display:inline-block;background:#1b3d59;color:#fff;text-decoration:none;padding:14px 30px;border-radius:999px;font-weight:600\">See your template</a></p>" +
            $"<p style=\"font-size:12px;color:#5a6b75;line-height:1.6\">Sign in with this email address to use it. Or paste this into your browser:<br><a href=\"{link}\" style=\"color:#1b3d59\">{link}</a><br>Sent via invites.blog</p></div>";
        return new EmailMessage(To: to, Subject: $"{designerName} made an invitation for you", Html: html, Stream: EmailStream.System);
    }

    private static string CleanName(string name)
    {
        var trimmed = string.Join(' ', (name ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (trimmed.Length == 0) throw new BusinessRuleException("Give the template a name.", "name_required");
        return trimmed.Length > 80 ? trimmed[..80] : trimmed;
    }

    private static string Slugify(string name)
    {
        var chars = name.ToLowerInvariant().Select(c => char.IsAsciiLetterOrDigit(c) ? c : '-').ToArray();
        var slug = string.Join('-', new string(chars).Split('-', StringSplitOptions.RemoveEmptyEntries));
        if (slug.Length == 0) slug = "template";
        if (slug.Length > 48) slug = slug[..48].TrimEnd('-');
        return $"{slug}-{Guid.NewGuid().ToString("n")[..6]}";
    }
}
