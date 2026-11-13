using System.Net;
using System.Text.Json;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Common;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Phones;
using InvitesBlog.Application.Plans;
using InvitesBlog.Application.Services.Campaigns;
using InvitesBlog.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace InvitesBlog.Application.Services.Portfolios;

/// <summary>The Studio's controls for its portfolio (docs/PORTFOLIO.md). Every call is refused unless the caller's own Studio plan is in force.</summary>
public interface IPortfolioService
{
    Task<PortfolioDto> GetAsync(CancellationToken ct = default);
    Task<PortfolioDto> UpdateAsync(UpdatePortfolioRequest request, CancellationToken ct = default);
    Task<HandleCheckDto> CheckHandleAsync(string? handle, CancellationToken ct = default);
    Task<PortfolioDto> SetHandleAsync(string? handle, CancellationToken ct = default);
    Task<PortfolioDto> SetLogoAsync(byte[] content, string? contentType, CancellationToken ct = default);
    Task<PortfolioDto> RemoveLogoAsync(CancellationToken ct = default);
    Task<PortfolioDto> SetCoverAsync(byte[] content, string? contentType, CancellationToken ct = default);
    Task<PortfolioDto> RemoveCoverAsync(CancellationToken ct = default);
    Task<PortfolioEventPhotosDto> EventPhotosAsync(Guid campaignId, CancellationToken ct = default);
    Task<PortfolioDto> AddSectionAsync(SectionRequest request, CancellationToken ct = default);
    Task<PortfolioDto> UpdateSectionAsync(Guid id, SectionRequest request, CancellationToken ct = default);
    Task<PortfolioDto> DeleteSectionAsync(Guid id, CancellationToken ct = default);
    Task<PortfolioDto> OrderSectionsAsync(IReadOnlyList<Guid>? ids, CancellationToken ct = default);
    Task<PortfolioDto> AddCollectionAsync(CollectionRequest request, CancellationToken ct = default);
    Task<PortfolioDto> UpdateCollectionAsync(Guid id, CollectionRequest request, CancellationToken ct = default);
    Task<PortfolioDto> DeleteCollectionAsync(Guid id, CancellationToken ct = default);
    Task<PortfolioDto> OrderPhotosAsync(Guid collectionId, IReadOnlyList<Guid>? photoIds, CancellationToken ct = default);
    Task<AddPortfolioPhotosResultDto> AddPhotosAsync(AddPortfolioPhotosRequest request, CancellationToken ct = default);
    /// <summary>One picture uploaded straight into an uploads collection (past work): stored as web copies, metadata gone.</summary>
    Task<UploadPortfolioPhotoResultDto> UploadPhotoAsync(Guid collectionId, byte[] content, CancellationToken ct = default);
    Task<PortfolioDto> RemovePhotoAsync(Guid id, CancellationToken ct = default);
    Task<PortfolioDto> PublishAsync(bool published, CancellationToken ct = default);
    Task<IReadOnlyList<PortfolioEnquiryDto>> EnquiriesAsync(CancellationToken ct = default);
    Task MarkEnquiryReadAsync(Guid id, CancellationToken ct = default);
    Task<IReadOnlyList<PortfolioDesignDto>> DesignsAsync(CancellationToken ct = default);
    Task<PortfolioConsentDto> GetConsentAsync(Guid campaignId, CancellationToken ct = default);
    Task<PortfolioConsentDto> SetConsentAsync(Guid campaignId, bool consented, CancellationToken ct = default);
    /// <summary>Takes the copies of these album photos out of the caller's portfolio (with their files); how many went.</summary>
    Task<int> RemoveSourcesAsync(IReadOnlyCollection<Guid> sourcePhotoIds, CancellationToken ct = default);
}

public sealed class PortfolioService(
    ICurrentUser currentUser,
    IRepository<AppUser> users,
    IRepository<Portfolio> portfolios,
    IRepository<PortfolioSection> sections,
    IRepository<PortfolioCollection> collections,
    IRepository<PortfolioPhoto> photos,
    IRepository<PortfolioConsent> consents,
    IRepository<PortfolioHandleRedirect> redirects,
    IRepository<PortfolioEnquiry> enquiries,
    IRepository<EventPhoto> eventPhotos,
    IRepository<Template> templates,
    ICampaignOwnershipService ownership,
    IStorageService storage,
    IImageOptimizer images,
    IHeicConverter heic,
    PhoneNormalizer phones,
    IConfiguration config,
    IUnitOfWork uow) : IPortfolioService
{
    public const int PhotoLimit = 500;
    public const int FullEdge = 2048;
    public const int ThumbEdge = 1024;
    public const int LogoEdge = 512;
    public const int MaxBody = 1500;
    public const int MaxPackages = 12;
    public const int MaxLines = 10;
    public const int MaxReviews = 20;

    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static CodedRuleException StudioRequired() =>
        new("The portfolio comes with Studio or Photographer.", "studio_required", HttpStatusCode.Forbidden);

    // ---------- the portfolio itself ----------

    public async Task<PortfolioDto> GetAsync(CancellationToken ct = default) => await DtoAsync(await MineAsync(ct), ct);

    public async Task<PortfolioDto> UpdateAsync(UpdatePortfolioRequest r, CancellationToken ct = default)
    {
        var p = await MineAsync(ct, tracking: true);
        var me = await MeAsync(ct);
        if (r.DisplayName is not null)
        {
            var name = Clean(r.DisplayName, 80);
            if (name is null) throw new CodedRuleException("Give your studio a name.", "name_required");
            p.DisplayName = name;
        }
        if (r.Tagline is not null) p.Tagline = Clean(r.Tagline, 140);
        if (r.Theme is not null)
            p.Theme = PortfolioChoices.Themes.Any(t => t.Key == r.Theme) ? r.Theme : throw Invalid("theme");
        if (r.Accent is not null)
            p.Accent = PortfolioChoices.Accents.Any(a => a.Key == r.Accent) ? r.Accent : throw Invalid("colour");
        if (r.Font is not null)
            p.Font = PortfolioChoices.Fonts.Any(f => f.Key == r.Font) ? r.Font : throw Invalid("font");
        if (r.LogoPosition is not null)
            p.LogoPosition = PortfolioChoices.LogoPositions.Any(x => x.Key == r.LogoPosition) ? r.LogoPosition : throw Invalid("logo position");
        if (r.LogoSize is not null)
            p.LogoSize = PortfolioChoices.LogoSizes.Any(x => x.Key == r.LogoSize) ? r.LogoSize : throw Invalid("logo size");
        if (r.ClearCover == true)
        {
            p.CoverPhotoId = null;
            await DeleteFileAsync(p.CoverImageUrl, ct);
            p.CoverImageUrl = null;
        }
        else if (r.CoverPhotoId is { } cover)
        {
            // Choosing one of the portfolio's photos replaces an uploaded cover: the latest choice wins.
            p.CoverPhotoId = await OwnPhotoAsync(p, cover, ct);
            await DeleteFileAsync(p.CoverImageUrl, ct);
            p.CoverImageUrl = null;
        }
        if (r.Contact is { } c)
        {
            if (c.WhatsApp is not null) p.WhatsApp = Phone(c.WhatsApp, me, "WhatsApp");
            if (c.Viber is not null) p.Viber = Phone(c.Viber, me, "Viber");
            if (c.Phone is not null) p.Phone = Phone(c.Phone, me, "phone");
            if (c.Instagram is not null) p.Instagram = Instagram(c.Instagram);
            if (c.Email is not null) p.Email = Email(c.Email);
            if (c.Enquiries is { } on) p.EnquiriesOn = on;
        }
        if (r.SetupDone is { } done) p.SetupDone = done;
        p.UpdatedAt = DateTimeOffset.UtcNow;
        await uow.SaveChangesAsync(ct);
        return await DtoAsync(p, ct);
    }

    // ---------- handle ----------

    public async Task<HandleCheckDto> CheckHandleAsync(string? raw, CancellationToken ct = default)
    {
        var p = await MineAsync(ct);
        var handle = PortfolioHandles.Normalize(raw);
        var (reason, message) = await HandleProblemAsync(p, handle, ct);
        string? suggestion = null;
        if (reason is "taken" or "reserved" or "invalid")
            // A reserved or malformed name says nothing about the studio: suggest from its own name instead.
            suggestion = await SuggestAsync(p, reason == "taken" ? handle : p.DisplayName, ct);
        return new HandleCheckDto(handle, reason is "ok" or "yours", reason, message, suggestion);
    }

    public async Task<PortfolioDto> SetHandleAsync(string? raw, CancellationToken ct = default)
    {
        var p = await MineAsync(ct, tracking: true);
        var handle = PortfolioHandles.Normalize(raw);
        var (reason, message) = await HandleProblemAsync(p, handle, ct);
        if (reason == "yours") return await DtoAsync(p, ct);
        if (reason != "ok")
            throw new CodedRuleException(message, $"handle_{reason}", reason == "taken" ? HttpStatusCode.Conflict : HttpStatusCode.BadRequest);

        var now = DateTimeOffset.UtcNow;
        // Taking back a handle this portfolio changed away from: its redirect row goes.
        foreach (var own in await redirects.Query(tracking: true).Where(x => x.Handle == handle).ToListAsync(ct))
            redirects.Remove(own);
        if (p.Handle is { } old && old != handle)
        {
            // The old one keeps leading here (and stays taken) for 30 days, then is free.
            var existing = await redirects.Query(tracking: true).FirstOrDefaultAsync(x => x.Handle == old, ct);
            if (existing is null)
                await redirects.AddAsync(new PortfolioHandleRedirect
                {
                    Id = Guid.NewGuid(), Handle = old, PortfolioId = p.Id, ExpiresAt = now + PortfolioHandles.RedirectFor,
                }, ct);
            else { existing.PortfolioId = p.Id; existing.ExpiresAt = now + PortfolioHandles.RedirectFor; }
        }
        p.Handle = handle;
        p.UpdatedAt = now;
        try
        {
            await uow.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two studios pressing at the same moment: the unique index decided.
            throw new CodedRuleException("Someone else just took that name. Try another.", "handle_taken", HttpStatusCode.Conflict);
        }
        return await DtoAsync(p, ct);
    }

    private async Task<(string Reason, string Message)> HandleProblemAsync(Portfolio p, string handle, CancellationToken ct)
    {
        if (PortfolioHandles.ShapeProblem(handle) is { } shape) return ("invalid", shape);
        if (p.Handle == handle) return ("yours", "This is your link.");
        if (PortfolioHandles.IsReserved(handle)) return ("reserved", "That name is kept for invites.blog. Try another.");
        var now = DateTimeOffset.UtcNow;
        if (await portfolios.AnyAsync(x => x.Handle == handle && x.Id != p.Id, ct)
            || await redirects.AnyAsync(x => x.Handle == handle && x.ExpiresAt > now && x.PortfolioId != p.Id, ct))
            return ("taken", "Someone already has that name. Try another.");
        return ("ok", "You can have this name.");
    }

    private async Task<string> SuggestAsync(Portfolio p, string from, CancellationToken ct)
    {
        var stem = PortfolioHandles.FromName(string.IsNullOrWhiteSpace(from) ? p.DisplayName : from);
        if (stem.Length > PortfolioHandles.Max - 3) stem = stem[..(PortfolioHandles.Max - 3)].Trim('.');
        foreach (var candidate in new[] { stem, stem + ".studio", stem + ".photo", stem + "_mv" }
                     .Concat(Enumerable.Range(2, 40).Select(i => $"{stem}{i}")))
        {
            if (candidate.Length > PortfolioHandles.Max) continue;
            var (reason, _) = await HandleProblemAsync(p, candidate, ct);
            if (reason is "ok" or "yours") return candidate;
        }
        return $"{stem}{Random.Shared.Next(100, 999)}";
    }

    // ---------- logo ----------

    public async Task<PortfolioDto> SetLogoAsync(byte[] content, string? contentType, CancellationToken ct = default)
    {
        var p = await MineAsync(ct, tracking: true);
        var type = ImageSniffer.Detect(content);
        if (type is null || !ImageSniffer.IsPhoto(content, type))
            throw new CodedRuleException(ImageSniffer.Refusal, "not_an_image");
        var image = images.OptimizeForDisplay(content, type, LogoEdge);
        var url = await storage.PutAsync(
            $"portfolios/{p.Id:N}/logo-{Guid.NewGuid():N}{MediaFileTypes.ExtensionFor(image.ContentType)}", image.Content, image.ContentType, ct);
        await DeleteFileAsync(p.LogoUrl, ct);
        p.LogoUrl = url;
        p.UpdatedAt = DateTimeOffset.UtcNow;
        await uow.SaveChangesAsync(ct);
        return await DtoAsync(p, ct);
    }

    public async Task<PortfolioDto> RemoveLogoAsync(CancellationToken ct = default)
    {
        var p = await MineAsync(ct, tracking: true);
        await DeleteFileAsync(p.LogoUrl, ct);
        p.LogoUrl = null;
        p.UpdatedAt = DateTimeOffset.UtcNow;
        await uow.SaveChangesAsync(ct);
        return await DtoAsync(p, ct);
    }

    // ---------- cover picture ----------

    public const int CoverEdge = 2400;

    public async Task<PortfolioDto> SetCoverAsync(byte[] content, string? contentType, CancellationToken ct = default)
    {
        var p = await MineAsync(ct, tracking: true);
        var type = ImageSniffer.Detect(content);
        if (type is null || !ImageSniffer.IsPhoto(content, type))
            throw new CodedRuleException(ImageSniffer.Refusal, "not_an_image");
        var image = await Task.Run(() => images.OptimizeForDisplay(content, type, CoverEdge), ct);
        var url = await storage.PutAsync(
            $"portfolios/{p.Id:N}/cover-{Guid.NewGuid():N}{MediaFileTypes.ExtensionFor(image.ContentType)}", image.Content, image.ContentType, ct);
        await DeleteFileAsync(p.CoverImageUrl, ct);
        p.CoverImageUrl = url;
        p.CoverPhotoId = null;
        p.UpdatedAt = DateTimeOffset.UtcNow;
        await uow.SaveChangesAsync(ct);
        return await DtoAsync(p, ct);
    }

    public async Task<PortfolioDto> RemoveCoverAsync(CancellationToken ct = default)
    {
        var p = await MineAsync(ct, tracking: true);
        await DeleteFileAsync(p.CoverImageUrl, ct);
        p.CoverImageUrl = null;
        p.UpdatedAt = DateTimeOffset.UtcNow;
        await uow.SaveChangesAsync(ct);
        return await DtoAsync(p, ct);
    }

    // ---------- sections ----------

    public async Task<PortfolioDto> AddSectionAsync(SectionRequest r, CancellationToken ct = default)
    {
        var p = await MineAsync(ct);
        var kind = r.Kind ?? string.Empty;
        if (!PortfolioSectionKinds.Addable.Contains(kind))
            throw new CodedRuleException("That kind of section can't be added here.", "section_kind");
        if (await sections.AnyAsync(s => s.PortfolioId == p.Id && s.Kind == kind, ct))
            throw new CodedRuleException("Your page already has that section.", "section_exists", HttpStatusCode.Conflict);
        var data = await ApplyDataAsync(p, new SectionData(), r, ct);
        await sections.AddAsync(new PortfolioSection
        {
            Id = Guid.NewGuid(), PortfolioId = p.Id, Kind = kind, Position = await NextSectionPositionAsync(p.Id, ct, beforeContact: kind != PortfolioSectionKinds.Contact),
            Hidden = r.Hidden ?? false, DataJson = JsonSerializer.Serialize(data, Json),
        }, ct);
        await uow.SaveChangesAsync(ct);
        return await DtoAsync(p, ct);
    }

    public async Task<PortfolioDto> UpdateSectionAsync(Guid id, SectionRequest r, CancellationToken ct = default)
    {
        var p = await MineAsync(ct);
        var s = await sections.Query(tracking: true).FirstOrDefaultAsync(x => x.Id == id && x.PortfolioId == p.Id, ct)
                ?? throw new NotFoundException("That section isn't there any more.", "section_not_found");
        if (s.Kind == PortfolioSectionKinds.Cover)
        {
            if (r.Hidden == true) throw Locked();
        }
        else if (r.Hidden is { } hidden) s.Hidden = hidden;
        s.DataJson = JsonSerializer.Serialize(await ApplyDataAsync(p, Read(s.DataJson), r, ct), Json);
        await uow.SaveChangesAsync(ct);
        return await DtoAsync(p, ct);
    }

    public async Task<PortfolioDto> DeleteSectionAsync(Guid id, CancellationToken ct = default)
    {
        var p = await MineAsync(ct);
        var s = await sections.Query(tracking: true).FirstOrDefaultAsync(x => x.Id == id && x.PortfolioId == p.Id, ct)
                ?? throw new NotFoundException("That section isn't there any more.", "section_not_found");
        if (s.Kind == PortfolioSectionKinds.Cover) throw Locked();
        if (s.CollectionId is { } cid) return await DeleteCollectionAsync(cid, ct);
        sections.Remove(s);
        await uow.SaveChangesAsync(ct);
        return await DtoAsync(p, ct);
    }

    public async Task<PortfolioDto> OrderSectionsAsync(IReadOnlyList<Guid>? ids, CancellationToken ct = default)
    {
        var p = await MineAsync(ct);
        var rows = await sections.Query(tracking: true).Where(x => x.PortfolioId == p.Id).ToListAsync(ct);
        if (ids is null || ids.Count != rows.Count || ids.Distinct().Count() != rows.Count || rows.Any(r => !ids.Contains(r.Id)))
            throw new CodedRuleException("The page changed. Refresh and try again.", "order_mismatch", HttpStatusCode.Conflict);
        // The cover is always first, whatever order arrives.
        var ordered = rows.Where(r => r.Kind == PortfolioSectionKinds.Cover)
            .Concat(ids.Select(i => rows.First(r => r.Id == i)).Where(r => r.Kind != PortfolioSectionKinds.Cover)).ToList();
        for (var i = 0; i < ordered.Count; i++) ordered[i].Position = i;
        await uow.SaveChangesAsync(ct);
        return await DtoAsync(p, ct);
    }

    private async Task<SectionData> ApplyDataAsync(Portfolio p, SectionData d, SectionRequest r, CancellationToken ct)
    {
        if (r.Title is not null) d = d with { Title = Clean(r.Title, 80) };
        if (r.Body is not null) d = d with { Body = Clean(r.Body, MaxBody, multiline: true) };
        if (r.ClearPhoto == true) d = d with { PhotoId = null };
        else if (r.PhotoId is { } photo) d = d with { PhotoId = await OwnPhotoAsync(p, photo, ct) };
        if (r.TemplateIds is not null)
        {
            var mine = (await DesignsAsync(ct)).Select(x => x.Id).ToHashSet();
            d = d with { TemplateIds = r.TemplateIds.Where(mine.Contains).Distinct().Take(24).ToList() };
        }
        if (r.Packages is not null)
            d = d with
            {
                Packages = r.Packages.Take(MaxPackages)
                    .Select(x => new PortfolioPackageDto(Clean(x.Name, 60) ?? "", Clean(x.Price, 40),
                        (x.Lines ?? []).Select(l => Clean(l, 120)).OfType<string>().Take(MaxLines).ToList()))
                    .Where(x => x.Name.Length > 0).ToList(),
            };
        if (r.Reviews is not null)
            d = d with
            {
                Reviews = r.Reviews.Take(MaxReviews)
                    .Select(x => new PortfolioReviewDto(Clean(x.Quote, 600, multiline: true) ?? "", Clean(x.Name, 60)))
                    .Where(x => x.Quote.Length > 0).ToList(),
            };
        return d;
    }

    // ---------- collections ----------

    public async Task<PortfolioDto> AddCollectionAsync(CollectionRequest r, CancellationToken ct = default)
    {
        var p = await MineAsync(ct);
        var kind = r.Kind ?? PortfolioCollectionKinds.Events;
        if (!PortfolioCollectionKinds.IsKnown(kind)) throw Invalid("kind of collection");
        await NewCollectionAsync(p, r.Title, r.Layout, ct, kind);
        await uow.SaveChangesAsync(ct);
        return await DtoAsync(p, ct);
    }

    private async Task<PortfolioCollection> NewCollectionAsync(Portfolio p, string? title, string? layout, CancellationToken ct,
        string kind = PortfolioCollectionKinds.Events)
    {
        var c = new PortfolioCollection
        {
            Id = Guid.NewGuid(), PortfolioId = p.Id, Title = Clean(title, 80) ?? "My work",
            Layout = Layout(layout ?? "grid"), Kind = kind, CreatedAt = DateTimeOffset.UtcNow,
        };
        await collections.AddAsync(c, ct);
        await sections.AddAsync(new PortfolioSection
        {
            Id = Guid.NewGuid(), PortfolioId = p.Id, Kind = PortfolioSectionKinds.Collection, CollectionId = c.Id,
            Position = await NextSectionPositionAsync(p.Id, ct, beforeContact: true), DataJson = "{}",
        }, ct);
        return c;
    }

    public async Task<PortfolioDto> UpdateCollectionAsync(Guid id, CollectionRequest r, CancellationToken ct = default)
    {
        var p = await MineAsync(ct);
        var c = await CollectionAsync(p, id, ct, tracking: true);
        if (r.Title is not null) c.Title = Clean(r.Title, 80) ?? throw new CodedRuleException("Give the collection a name.", "title_required");
        if (r.Layout is not null) c.Layout = Layout(r.Layout);
        if (r.ClearCover == true) c.CoverPhotoId = null;
        else if (r.CoverPhotoId is { } cover)
            c.CoverPhotoId = await photos.AnyAsync(x => x.Id == cover && x.CollectionId == c.Id, ct)
                ? cover : throw new NotFoundException("That photo isn't in this collection.", "photo_not_found");
        await uow.SaveChangesAsync(ct);
        return await DtoAsync(p, ct);
    }

    public async Task<PortfolioDto> DeleteCollectionAsync(Guid id, CancellationToken ct = default)
    {
        var p = await MineAsync(ct);
        var c = await CollectionAsync(p, id, ct, tracking: true);
        var rows = await photos.Query(tracking: true).Where(x => x.CollectionId == c.Id).ToListAsync(ct);
        await RemovePhotoRowsAsync(rows, ct);
        foreach (var s in await sections.Query(tracking: true).Where(x => x.CollectionId == c.Id).ToListAsync(ct)) sections.Remove(s);
        collections.Remove(c);
        await uow.SaveChangesAsync(ct);
        return await DtoAsync(p, ct);
    }

    public async Task<PortfolioDto> OrderPhotosAsync(Guid collectionId, IReadOnlyList<Guid>? ids, CancellationToken ct = default)
    {
        var p = await MineAsync(ct);
        var c = await CollectionAsync(p, collectionId, ct);
        var rows = await photos.Query(tracking: true).Where(x => x.CollectionId == c.Id).ToListAsync(ct);
        if (ids is null || ids.Count != rows.Count || ids.Distinct().Count() != rows.Count || rows.Any(r => !ids.Contains(r.Id)))
            throw new CodedRuleException("The collection changed. Refresh and try again.", "order_mismatch", HttpStatusCode.Conflict);
        for (var i = 0; i < ids.Count; i++) rows.First(r => r.Id == ids[i]).Position = i;
        await uow.SaveChangesAsync(ct);
        return await DtoAsync(p, ct);
    }

    // ---------- photos ----------

    public async Task<AddPortfolioPhotosResultDto> AddPhotosAsync(AddPortfolioPhotosRequest r, CancellationToken ct = default)
    {
        var p = await MineAsync(ct);
        var me = p.UserId;
        var ids = (r.PhotoIds ?? []).Distinct().ToList();
        if (ids.Count == 0) throw new CodedRuleException("Choose some photos first.", "no_photos");
        if (ids.Count > PhotoLimit) throw LimitReached(0);

        var found = await eventPhotos.Query()
            .Where(x => ids.Contains(x.Id) && x.DeletedAt == null)
            .ToListAsync(ct);
        // Only the Studio's own uploads (owner: never a guest's photo); each one that can't be used says why.
        var organised = await OrganisedAsync(found.Select(x => x.CampaignId), ct);
        var skipped = new List<SkippedPortfolioPhotoDto>();
        var own = new List<EventPhoto>();
        foreach (var x in found)
        {
            if (Ineligible(x, me, organised) is { } why) skipped.Add(new SkippedPortfolioPhotoDto(x.Id, why));
            else own.Add(x);
        }
        foreach (var gone in ids.Where(i => found.All(x => x.Id != i))) skipped.Add(new SkippedPortfolioPhotoDto(gone, Reasons.Missing));

        if (own.Count > 0)
        {
            var events = own.Select(x => x.CampaignId).Distinct().ToList();
            var consented = await consents.Query()
                .Where(x => x.UserId == me && events.Contains(x.CampaignId))
                .Select(x => x.CampaignId).ToListAsync(ct);
            if (own.Any(x => x.CampaignId is null || !consented.Contains(x.CampaignId.Value)))
                throw new CodedRuleException(
                    "First confirm, in the album's settings, that the people this event is for agreed to its photos being shown.",
                    "no_consent", HttpStatusCode.Forbidden);
        }

        PortfolioCollection collection;
        if (r.CollectionId is { } cid)
        {
            collection = await CollectionAsync(p, cid, ct);
            // Picks and uploads never mix (owner): an uploads collection only takes pictures uploaded into it.
            if (collection.Kind != PortfolioCollectionKinds.Events)
                throw new CodedRuleException(
                    "That collection is for pictures you upload. Choose one of your event collections or make a new one.",
                    "wrong_kind", HttpStatusCode.Conflict);
        }
        else if (!string.IsNullOrWhiteSpace(r.NewCollectionTitle) && own.Count > 0) collection = await NewCollectionAsync(p, r.NewCollectionTitle, "grid", ct);
        else if (own.Count == 0) throw NothingAdded(0, skipped);
        else throw new CodedRuleException("Choose a collection or name a new one.", "collection_required");

        var already = 0;
        var toAdd = own;
        if (collection is not null && own.Count > 0)
        {
            var sources = own.Select(x => (Guid?)x.Id).ToList();
            var there = await photos.Query().Where(x => x.CollectionId == collection.Id && sources.Contains(x.SourcePhotoId))
                .Select(x => x.SourcePhotoId).ToListAsync(ct);
            toAdd = own.Where(x => !there.Contains(x.Id)).ToList();
            already = own.Count - toAdd.Count;
            var count = await photos.Query().CountAsync(x => x.PortfolioId == p.Id, ct);
            if (count + toAdd.Count > PhotoLimit) throw LimitReached(count);
        }

        var added = 0;
        if (toAdd.Count > 0)
        {
            var next = await photos.Query().Where(x => x.CollectionId == collection.Id)
                .Select(x => (int?)x.Position).MaxAsync(ct) ?? -1;
            // A few at a time: each is two reads of storage and two writes.
            var copies = new List<PortfolioPhoto?>();
            foreach (var batch in toAdd.Chunk(4))
                copies.AddRange(await Task.WhenAll(batch.Select(x => CopyAsync(p, collection.Id, x, ct))));
            for (var i = 0; i < copies.Count; i++)
            {
                if (copies[i] is not { } copy)
                {
                    skipped.Add(new SkippedPortfolioPhotoDto(toAdd[i].Id, Reasons.Missing));
                    continue;
                }
                copy.Position = ++next;
                await photos.AddAsync(copy, ct);
                added++;
            }
            p = await portfolios.Query(tracking: true).FirstAsync(x => x.Id == p.Id, ct);
            p.UpdatedAt = DateTimeOffset.UtcNow;
        }
        // Never a "success" with nothing in it: the caller shows why and lets them choose again.
        if (added == 0 && already == 0) throw NothingAdded(0, skipped);
        await uow.SaveChangesAsync(ct);

        int Count(string reason) => skipped.Count(s => s.Reason == reason);
        var message = AddedMessage(added, already, Count(Reasons.Guest), Count(Reasons.Video), Count(Reasons.OtherAccount), Count(Reasons.Missing));
        return new AddPortfolioPhotosResultDto(added, already, Count(Reasons.Guest), Count(Reasons.Video),
            collection.Id, message, await DtoAsync(p, ct), Count(Reasons.OtherAccount), Count(Reasons.Missing), skipped);
    }

    // ---------- uploads (past work, straight into an uploads collection) ----------

    /// <summary>Largest file taken (the controller refuses bigger ones before reading them).</summary>
    public const long MaxUploadBytes = 30L * 1024 * 1024;
    /// <summary>Largest picture decoded: 80 megapixels is beyond any camera a studio uses, and about 320 MB to hold.</summary>
    public const long MaxUploadPixels = 80_000_000;
    /// <summary>Decoding a big picture takes a lot of memory; at most two at once in this API.</summary>
    private static readonly SemaphoreSlim UploadGate = new(2, 2);
    /// <summary>The last count-and-insert of an upload, one at a time, so uploads sent side by side can't pass the cap together.</summary>
    private static readonly SemaphoreSlim UploadSaveGate = new(1, 1);

    public const string UploadRefusal = "That file isn't a picture we can use. Upload a JPEG, PNG, WebP or HEIC photo.";

    public async Task<UploadPortfolioPhotoResultDto> UploadPhotoAsync(Guid collectionId, byte[] content, CancellationToken ct = default)
    {
        var p = await MineAsync(ct);
        var c = await CollectionAsync(p, collectionId, ct);
        if (c.Kind != PortfolioCollectionKinds.Uploads)
            throw new CodedRuleException(
                "This collection holds photos picked from your events. Make an upload collection for pictures from your device.",
                "wrong_kind", HttpStatusCode.Conflict);
        var have = await photos.Query().CountAsync(x => x.PortfolioId == p.Id, ct);
        if (have >= PhotoLimit) throw LimitReached(have);
        if (content.LongLength > MaxUploadBytes)
            throw new CodedRuleException("That picture is too big. Use one under 30 MB.", "too_big", HttpStatusCode.RequestEntityTooLarge);

        // The file's own bytes say what it is; its name and label are whatever the device said.
        var type = ImageSniffer.Detect(content);
        if (type is not ("image/jpeg" or "image/png" or "image/webp" or "image/heic"))
            throw new CodedRuleException(UploadRefusal, "not_an_image");

        WebCopies? copies;
        WebCopyProblem problem;
        await UploadGate.WaitAsync(ct);
        try
        {
            var bytes = type == "image/heic" ? await heic.ToJpegAsync(content, ct) : content;
            if (bytes is null) (copies, problem) = (null, WebCopyProblem.Unreadable);
            else
                (copies, problem) = await Task.Run(() =>
                {
                    var made = images.MakeWebCopies(bytes, FullEdge, ThumbEdge, MaxUploadPixels, out var why);
                    return (made, why);
                }, ct);
        }
        finally
        {
            UploadGate.Release();
        }
        if (problem == WebCopyProblem.TooManyPixels)
            throw new CodedRuleException("That picture has too many pixels. Use one under 80 megapixels.", "too_many_pixels");
        if (copies is null)
            throw new CodedRuleException("We couldn't open that picture. Try it again, or save it as a JPEG first.", "unreadable", HttpStatusCode.UnprocessableEntity);

        var id = Guid.NewGuid();
        var stem = $"portfolios/{p.Id:N}/{id:N}";
        var fullUrl = storage.PutAsync($"{stem}{MediaFileTypes.ExtensionFor(copies.Full.ContentType)}", copies.Full.Content, copies.Full.ContentType, ct);
        var thumbUrl = storage.PutAsync($"{stem}_m{MediaFileTypes.ExtensionFor(copies.Thumb.ContentType)}", copies.Thumb.Content, copies.Thumb.ContentType, ct);
        await Task.WhenAll(fullUrl, thumbUrl);

        // Uploads run side by side: count again, one at a time, now the slow part is done, so the cap holds.
        PortfolioPhoto row;
        Portfolio tracked;
        await UploadSaveGate.WaitAsync(ct);
        try
        {
            have = await photos.Query().CountAsync(x => x.PortfolioId == p.Id, ct);
            if (have >= PhotoLimit)
            {
                await DeleteFileAsync(fullUrl.Result, ct);
                await DeleteFileAsync(thumbUrl.Result, ct);
                throw LimitReached(have);
            }
            var next = await photos.Query().Where(x => x.CollectionId == c.Id).Select(x => (int?)x.Position).MaxAsync(ct) ?? -1;
            row = new PortfolioPhoto
            {
                Id = id, PortfolioId = p.Id, CollectionId = c.Id, SourcePhotoId = null, CampaignId = null,
                Url = fullUrl.Result, ThumbUrl = thumbUrl.Result, Width = copies.Full.Width, Height = copies.Full.Height,
                Position = next + 1, CreatedAt = DateTimeOffset.UtcNow,
            };
            await photos.AddAsync(row, ct);
            tracked = await portfolios.Query(tracking: true).FirstAsync(x => x.Id == p.Id, ct);
            tracked.UpdatedAt = DateTimeOffset.UtcNow;
            await uow.SaveChangesAsync(ct);
        }
        finally
        {
            UploadSaveGate.Release();
        }
        var dto = await DtoAsync(tracked, ct);
        return new UploadPortfolioPhotoResultDto(PhotoDto(row), dto.PhotoCount, PhotoLimit, Math.Max(0, PhotoLimit - dto.PhotoCount), dto);
    }

    /// <summary>Why a photo can't go in the portfolio (a code from <see cref="Reasons"/>), or null when it can.</summary>
    internal static string? Ineligible(EventPhoto x, Guid me, IReadOnlySet<Guid> organised)
    {
        var mine = x.UploaderUserId == me
                   // Host uploads from before uploaders were recorded (2026-11-03): no guest, no account AND no name.
                   // In an event this studio organises, those were added through its own dashboard or app. A name
                   // without a guest is a QR code or invitation camera upload by whoever typed it: never assumed to
                   // be the organiser's (they can say "This is me", see PortfolioClaimService).
                   || (x.UploaderUserId is null && x.GuestId is null && string.IsNullOrWhiteSpace(x.UploaderName)
                       && x.CreatedAt < UploaderRecordedFrom && x.CampaignId is { } cid && organised.Contains(cid));
        if (!mine) return x.GuestId is not null || x.UploaderUserId is null ? Reasons.Guest : Reasons.OtherAccount;
        return x.ContentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ? Reasons.Video : null;
    }

    /// <summary>Since this moment every upload records the account that made it (when signed in).</summary>
    public static readonly DateTimeOffset UploaderRecordedFrom = new(2026, 11, 4, 0, 0, 0, TimeSpan.Zero);

    public static class Reasons
    {
        public const string Guest = "guest";
        public const string OtherAccount = "other_account";
        public const string Video = "video";
        public const string Missing = "missing";

        public static string Text(string reason) => reason switch
        {
            Guest => "Added by a guest",
            OtherAccount => "Added by someone else",
            Video => "Videos can't be added",
            _ => "This photo couldn't be read",
        };
    }

    /// <summary>The events among <paramref name="ids"/> that the caller organises.</summary>
    private async Task<IReadOnlySet<Guid>> OrganisedAsync(IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var set = new HashSet<Guid>();
        foreach (var id in ids.OfType<Guid>().Distinct())
            if (await ownership.AccessAsync(id, ct) == CampaignAccess.Organiser) set.Add(id);
        return set;
    }

    public static CodedRuleException NothingAdded(int added, IReadOnlyList<SkippedPortfolioPhotoDto> skipped)
    {
        int Count(string reason) => skipped.Count(s => s.Reason == reason);
        var guest = Count(Reasons.Guest);
        var other = Count(Reasons.OtherAccount);
        var video = Count(Reasons.Video);
        var missing = Count(Reasons.Missing);
        const string rule = "Only photos you uploaded yourself can go in your portfolio.";
        string message;
        if (skipped.Count > 0 && guest == skipped.Count) message = $"None of these could be added: they were added by guests. {rule}";
        else if (skipped.Count > 0 && other == skipped.Count) message = $"None of these could be added: they were added by someone else. {rule}";
        else if (skipped.Count > 0 && video == skipped.Count) message = "None of these could be added: videos can't go in a portfolio yet. Choose photos.";
        else if (skipped.Count > 0 && missing == skipped.Count) message = "None of these could be added: we couldn't read them. Try again, or choose other photos.";
        else
        {
            var parts = new List<string>();
            if (guest > 0) parts.Add(guest == 1 ? "1 was added by a guest" : $"{guest} were added by guests");
            if (other > 0) parts.Add(other == 1 ? "1 was added by someone else" : $"{other} were added by someone else");
            if (video > 0) parts.Add(video == 1 ? "1 is a video" : $"{video} are videos");
            if (missing > 0) parts.Add(missing == 1 ? "1 couldn't be read" : $"{missing} couldn't be read");
            message = parts.Count == 0 ? "None of these could be added. Choose your own photos." : $"None of these could be added: {string.Join(", ", parts)}. {rule}";
        }
        return new CodedRuleException(message, "nothing_added", HttpStatusCode.UnprocessableEntity);
    }

    public async Task<PortfolioEventPhotosDto> EventPhotosAsync(Guid campaignId, CancellationToken ct = default)
    {
        var p = await MineAsync(ct);
        if (await ownership.AccessAsync(campaignId, ct) != CampaignAccess.Organiser)
            throw new CodedRuleException("Only the event's organiser can do this.", "not_organiser", HttpStatusCode.Forbidden);
        var organised = new HashSet<Guid> { campaignId };
        var rows = await eventPhotos.Query().Where(x => x.CampaignId == campaignId && x.DeletedAt == null)
            .OrderByDescending(x => x.CreatedAt).Take(5000).ToListAsync(ct);
        var sources = rows.Select(x => (Guid?)x.Id).ToList();
        var inPortfolio = (await photos.Query().Where(x => x.PortfolioId == p.Id && x.CampaignId == campaignId && x.SourcePhotoId != null)
            .Select(x => x.SourcePhotoId!.Value).ToListAsync(ct)).ToHashSet();
        var list = rows.Select(x =>
        {
            var why = Ineligible(x, p.UserId, organised);
            return new PortfolioEventPhotoDto(x.Id, x.ThumbUrl, x.Url, x.Width, x.Height, why is null, why,
                why is null ? null : Reasons.Text(why), inPortfolio.Contains(x.Id));
        }).ToList();
        var consented = await consents.AnyAsync(x => x.UserId == p.UserId && x.CampaignId == campaignId, ct);
        return new PortfolioEventPhotosDto(consented, list.Count(x => x.Eligible),
            list.Count(x => x.Reason is Reasons.Guest or Reasons.OtherAccount), list.Count(x => x.Reason == Reasons.Video), list);
    }

    public static string AddedMessage(int added, int already, int guest, int video, int other = 0, int missing = 0)
    {
        var parts = new List<string>
        {
            added == 0 ? "No photos added." : added == 1 ? "1 photo added." : $"{added} photos added.",
        };
        if (already > 0) parts.Add(already == 1 ? "1 was already there." : $"{already} were already there.");
        if (guest > 0) parts.Add(guest == 1 ? "1 was added by a guest and can't be used." : $"{guest} were added by guests and can't be used.");
        if (other > 0) parts.Add(other == 1 ? "1 was added by someone else and can't be used." : $"{other} were added by someone else and can't be used.");
        if (video > 0) parts.Add(video == 1 ? "Videos can't be added yet." : $"{video} videos can't be added yet.");
        if (missing > 0) parts.Add(missing == 1 ? "1 couldn't be read." : $"{missing} couldn't be read.");
        return string.Join(' ', parts);
    }

    private async Task<PortfolioPhoto?> CopyAsync(Portfolio p, Guid collectionId, EventPhoto source, CancellationToken ct)
    {
        // The viewing copy (already at most 2048px) is the source: the original may be huge and is never needed here.
        var key = storage.KeyFor(source.Url) ?? storage.KeyFor(source.OriginalUrl);
        var bytes = key is null ? null : await storage.GetAsync(key, ct);
        if (bytes is null) return null;
        var type = ImageSniffer.Detect(bytes) ?? source.ContentType;
        var id = Guid.NewGuid();
        // The album's viewing copy is already at most 2048px and has no camera metadata: kept as it is, it isn't
        // compressed a second time. Only a larger one (an old row) is made smaller.
        var full = await Task.Run(() => images.Preserve(bytes, type) is var kept && Math.Max(kept.Width, kept.Height) <= FullEdge
            ? kept : images.OptimizeForDisplay(bytes, type, FullEdge), ct);
        var thumb = await Task.Run(() => images.OptimizeForDisplay(bytes, type, ThumbEdge), ct);
        var stem = $"portfolios/{p.Id:N}/{id:N}";
        var fullUrl = storage.PutAsync($"{stem}{MediaFileTypes.ExtensionFor(full.ContentType)}", full.Content, full.ContentType, ct);
        var thumbUrl = storage.PutAsync($"{stem}_m{MediaFileTypes.ExtensionFor(thumb.ContentType)}", thumb.Content, thumb.ContentType, ct);
        await Task.WhenAll(fullUrl, thumbUrl);
        return new PortfolioPhoto
        {
            Id = id, PortfolioId = p.Id, CollectionId = collectionId, SourcePhotoId = source.Id, CampaignId = source.CampaignId,
            Url = fullUrl.Result, ThumbUrl = thumbUrl.Result, Width = full.Width, Height = full.Height,
            CreatedAt = DateTimeOffset.UtcNow,
        };
    }

    public async Task<PortfolioDto> RemovePhotoAsync(Guid id, CancellationToken ct = default)
    {
        var p = await MineAsync(ct);
        var row = await photos.Query(tracking: true).FirstOrDefaultAsync(x => x.Id == id && x.PortfolioId == p.Id, ct)
                  ?? throw new NotFoundException("That photo isn't in your portfolio.", "photo_not_found");
        await RemovePhotoRowsAsync([row], ct);
        await uow.SaveChangesAsync(ct);
        return await DtoAsync(p, ct);
    }

    /// <summary>Removes photo rows and their files, and any cover that pointed at them. The caller saves.</summary>
    internal async Task RemovePhotoRowsAsync(IReadOnlyList<PortfolioPhoto> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return;
        var ids = rows.Select(r => r.Id).ToList();
        var portfolioIds = rows.Select(r => r.PortfolioId).Distinct().ToList();
        foreach (var owner in await portfolios.Query(tracking: true).Where(x => portfolioIds.Contains(x.Id) && x.CoverPhotoId != null && ids.Contains(x.CoverPhotoId.Value)).ToListAsync(ct))
            owner.CoverPhotoId = null;
        foreach (var c in await collections.Query(tracking: true).Where(x => x.CoverPhotoId != null && ids.Contains(x.CoverPhotoId.Value)).ToListAsync(ct))
            c.CoverPhotoId = null;
        photos.RemoveRange(rows);
        foreach (var r in rows)
        {
            await DeleteFileAsync(r.Url, ct);
            await DeleteFileAsync(r.ThumbUrl, ct);
        }
    }

    public async Task<int> RemoveSourcesAsync(IReadOnlyCollection<Guid> sourcePhotoIds, CancellationToken ct = default)
    {
        var p = await MineAsync(ct);
        if (sourcePhotoIds.Count == 0) return 0;
        var ids = sourcePhotoIds.Select(x => (Guid?)x).ToList();
        var rows = await photos.Query(tracking: true).Where(x => x.PortfolioId == p.Id && ids.Contains(x.SourcePhotoId)).ToListAsync(ct);
        await RemovePhotoRowsAsync(rows, ct);
        await uow.SaveChangesAsync(ct);
        return rows.Count;
    }

    // ---------- publish ----------

    public async Task<PortfolioDto> PublishAsync(bool published, CancellationToken ct = default)
    {
        var p = await MineAsync(ct, tracking: true);
        if (published && p.Handle is null)
            throw new CodedRuleException("Choose your link first.", "handle_required");
        if (published && !p.IsPublished) p.PublishedAt = DateTimeOffset.UtcNow;
        p.IsPublished = published;
        if (published) p.SetupDone = true;
        p.UpdatedAt = DateTimeOffset.UtcNow;
        await uow.SaveChangesAsync(ct);
        return await DtoAsync(p, ct);
    }

    // ---------- enquiries and designs ----------

    public async Task<IReadOnlyList<PortfolioEnquiryDto>> EnquiriesAsync(CancellationToken ct = default)
    {
        var p = await MineAsync(ct);
        return await enquiries.Query().Where(x => x.PortfolioId == p.Id).OrderByDescending(x => x.CreatedAt).Take(500)
            .Select(x => new PortfolioEnquiryDto(x.Id, x.Name, x.Contact, x.EventDate, x.Message, x.CreatedAt, x.ReadAt != null))
            .ToListAsync(ct);
    }

    public async Task MarkEnquiryReadAsync(Guid id, CancellationToken ct = default)
    {
        var p = await MineAsync(ct);
        var row = await enquiries.Query(tracking: true).FirstOrDefaultAsync(x => x.Id == id && x.PortfolioId == p.Id, ct)
                  ?? throw new NotFoundException("That enquiry isn't there.", "enquiry_not_found");
        row.ReadAt ??= DateTimeOffset.UtcNow;
        await uow.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<PortfolioDesignDto>> DesignsAsync(CancellationToken ct = default)
    {
        var p = await MineAsync(ct);
        return await PublishedDesigns(templates.Query(), p.UserId)
            .Select(t => new PortfolioDesignDto(t.Id, t.Name, t.PreviewImageUrl))
            .ToListAsync(ct);
    }

    /// <summary>The studio's own designs that are published to the gallery and not taken down.</summary>
    internal static IQueryable<Template> PublishedDesigns(IQueryable<Template> q, Guid userId) =>
        q.Where(t => t.DesignerUserId == userId && t.IsActive && t.Visibility == TemplateVisibility.Public && t.UnlistedByAdminAt == null)
            .OrderByDescending(t => t.CreatedAt);

    // ---------- consent ----------

    public async Task<PortfolioConsentDto> GetConsentAsync(Guid campaignId, CancellationToken ct = default)
    {
        var p = await MineAsync(ct);
        return await ConsentDtoAsync(p, campaignId, ct);
    }

    public async Task<PortfolioConsentDto> SetConsentAsync(Guid campaignId, bool consented, CancellationToken ct = default)
    {
        var p = await MineAsync(ct);
        var row = await consents.Query(tracking: true).FirstOrDefaultAsync(x => x.UserId == p.UserId && x.CampaignId == campaignId, ct);
        if (consented)
        {
            if (!await ownership.OwnsAsync(campaignId, ct))
                throw new CodedRuleException("Only the event's organiser can do this.", "not_organiser", HttpStatusCode.Forbidden);
            if (row is null)
            {
                await consents.AddAsync(new PortfolioConsent
                {
                    Id = Guid.NewGuid(), UserId = p.UserId, CampaignId = campaignId, ConsentedAt = DateTimeOffset.UtcNow,
                }, ct);
                try { await uow.SaveChangesAsync(ct); }
                catch (DbUpdateException) { /* the same tap twice: already recorded */ }
            }
        }
        else
        {
            // Taking it back takes the event's photos out of the portfolio (the app asked first).
            var rows = await photos.Query(tracking: true).Where(x => x.PortfolioId == p.Id && x.CampaignId == campaignId).ToListAsync(ct);
            await RemovePhotoRowsAsync(rows, ct);
            if (row is not null) consents.Remove(row);
            await uow.SaveChangesAsync(ct);
        }
        return await ConsentDtoAsync(p, campaignId, ct);
    }

    private async Task<PortfolioConsentDto> ConsentDtoAsync(Portfolio p, Guid campaignId, CancellationToken ct)
    {
        var row = await consents.Query().FirstOrDefaultAsync(x => x.UserId == p.UserId && x.CampaignId == campaignId, ct);
        var count = await photos.Query().CountAsync(x => x.PortfolioId == p.Id && x.CampaignId == campaignId, ct);
        return new PortfolioConsentDto(row is not null, row?.ConsentedAt, count);
    }

    // ---------- helpers ----------

    private async Task<AppUser> MeAsync(CancellationToken ct)
    {
        var id = currentUser.UserId ?? throw new UnauthorizedException();
        return await users.GetByIdAsync(id, ct) ?? throw new UnauthorizedException();
    }

    /// <summary>The caller's portfolio, made (as a draft with a cover and contact section) on first use. Studio only.</summary>
    private async Task<Portfolio> MineAsync(CancellationToken ct, bool tracking = false)
    {
        var me = await MeAsync(ct);
        if (!AddOns.StudioActive(me, DateTimeOffset.UtcNow)) throw StudioRequired();
        var p = await portfolios.Query(tracking).FirstOrDefaultAsync(x => x.UserId == me.Id, ct);
        if (p is not null) return p;

        var now = DateTimeOffset.UtcNow;
        p = new Portfolio
        {
            Id = Guid.NewGuid(), UserId = me.Id, DisplayName = Clean(me.DisplayName, 80) ?? "My studio",
            Email = me.Email, CreatedAt = now, UpdatedAt = now,
        };
        await portfolios.AddAsync(p, ct);
        var cover = new PortfolioSection { Id = Guid.NewGuid(), PortfolioId = p.Id, Kind = PortfolioSectionKinds.Cover, Position = 0 };
        var contact = new PortfolioSection { Id = Guid.NewGuid(), PortfolioId = p.Id, Kind = PortfolioSectionKinds.Contact, Position = 1 };
        await sections.AddAsync(cover, ct);
        await sections.AddAsync(contact, ct);
        try
        {
            await uow.SaveChangesAsync(ct);
        }
        catch (DbUpdateException)
        {
            // Two first visits at once: the other one made it. Forget ours (removing an added row detaches it).
            sections.Remove(cover);
            sections.Remove(contact);
            portfolios.Remove(p);
        }
        return await portfolios.Query(tracking).FirstAsync(x => x.UserId == me.Id, ct);
    }

    private async Task<PortfolioCollection> CollectionAsync(Portfolio p, Guid id, CancellationToken ct, bool tracking = false) =>
        await collections.Query(tracking).FirstOrDefaultAsync(x => x.Id == id && x.PortfolioId == p.Id, ct)
        ?? throw new NotFoundException("That collection isn't there any more.", "collection_not_found");

    private async Task<Guid> OwnPhotoAsync(Portfolio p, Guid id, CancellationToken ct) =>
        await photos.AnyAsync(x => x.Id == id && x.PortfolioId == p.Id, ct)
            ? id : throw new NotFoundException("That photo isn't in your portfolio.", "photo_not_found");

    private async Task<int> NextSectionPositionAsync(Guid portfolioId, CancellationToken ct, bool beforeContact = false)
    {
        var rows = await sections.Query(tracking: true).Where(x => x.PortfolioId == portfolioId).OrderBy(x => x.Position).ToListAsync(ct);
        // A new section goes above the contact section when contact is last, so the page still ends with it.
        if (beforeContact && rows.Count > 0 && rows[^1].Kind == PortfolioSectionKinds.Contact)
        {
            var at = rows[^1].Position;
            rows[^1].Position = at + 1;
            return at;
        }
        return rows.Count == 0 ? 0 : rows[^1].Position + 1;
    }

    private async Task DeleteFileAsync(string? url, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(url) || storage.KeyFor(url) is not { } key) return;
        try { await storage.DeleteAsync(key, ct); }
        catch (Exception e) when (e is not OperationCanceledException) { /* a leftover file is not worth failing over */ }
    }

    internal static SectionData Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new SectionData();
        try { return JsonSerializer.Deserialize<SectionData>(json, Json) ?? new SectionData(); }
        catch (JsonException) { return new SectionData(); }
    }

    private string? Phone(string raw, AppUser me, string what)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var result = phones.Normalize(raw, me.Country);
        if (!result.IsUsable || result.E164 is null)
            throw new CodedRuleException($"That {what} number doesn't look right. {result.Warning ?? PhoneNormalizer.NeedsCountryCode}", "invalid_contact");
        return result.E164;
    }

    private static string? Instagram(string raw)
    {
        var s = raw.Trim();
        if (s.Length == 0) return null;
        // People paste the whole link as often as the name.
        var at = s.IndexOf("instagram.com/", StringComparison.OrdinalIgnoreCase);
        if (at >= 0) s = s[(at + "instagram.com/".Length)..];
        s = s.Split('?', '/')[0].TrimStart('@').ToLowerInvariant();
        if (s.Length is < 1 or > 30 || !s.All(c => c is >= 'a' and <= 'z' or >= '0' and <= '9' or '.' or '_'))
            throw new CodedRuleException("That Instagram name doesn't look right.", "invalid_contact");
        return s;
    }

    private static string? Email(string raw)
    {
        var s = raw.Trim().ToLowerInvariant();
        if (s.Length == 0) return null;
        if (s.Length > 254 || !System.Net.Mail.MailAddress.TryCreate(s, out var addr) || addr.Address != s || !s.Contains('.'))
            throw new CodedRuleException("That email address doesn't look right.", "invalid_contact");
        return s;
    }

    internal static string? Clean(string? text, int max, bool multiline = false)
    {
        if (text is null) return null;
        var s = multiline
            ? string.Join('\n', text.Replace("\r", "").Split('\n').Select(l => l.Trim())).Trim()
            : string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        while (multiline && s.Contains("\n\n\n")) s = s.Replace("\n\n\n", "\n\n");
        if (s.Length == 0) return null;
        return s.Length > max ? s[..max].TrimEnd() : s;
    }

    private static string Layout(string layout) =>
        PortfolioChoices.Layouts.Any(l => l.Key == layout) ? layout : throw Invalid("layout");

    private static CodedRuleException Invalid(string what) => new($"Choose a {what} from the list.", "invalid_choice");

    private static CodedRuleException Locked() => new("The cover always stays at the top of your page.", "section_locked");

    private static CodedRuleException LimitReached(int have) =>
        new($"A portfolio holds up to {PhotoLimit} photos. You have {have}, so you can add {Math.Max(0, PhotoLimit - have)} more.",
            "photo_limit", HttpStatusCode.Conflict);

    // ---------- the DTO ----------

    private async Task<PortfolioDto> DtoAsync(Portfolio p, CancellationToken ct)
    {
        var sectionRows = await sections.Query().Where(x => x.PortfolioId == p.Id).OrderBy(x => x.Position).ToListAsync(ct);
        var collectionRows = await collections.Query().Where(x => x.PortfolioId == p.Id).ToListAsync(ct);
        var photoRows = await photos.Query().Where(x => x.PortfolioId == p.Id).OrderBy(x => x.Position).ToListAsync(ct);
        var unread = await enquiries.Query().CountAsync(x => x.PortfolioId == p.Id && x.ReadAt == null, ct);
        return Build(p, sectionRows, collectionRows, photoRows, unread, config.InviterBase());
    }

    internal static PortfolioPhotoDto PhotoDto(PortfolioPhoto x) =>
        new(x.Id, x.Url, x.ThumbUrl, x.Width, x.Height, x.CampaignId, x.SourcePhotoId);

    /// <summary>The collections in section order, each with its photos in order.</summary>
    internal static List<PortfolioCollectionDto> Collections(
        IReadOnlyList<PortfolioSection> sectionRows, IReadOnlyList<PortfolioCollection> collectionRows, IReadOnlyList<PortfolioPhoto> photoRows)
    {
        var order = sectionRows.Where(s => s.CollectionId is not null).Select(s => s.CollectionId!.Value).ToList();
        return collectionRows
            .OrderBy(c => order.IndexOf(c.Id) is var i && i < 0 ? int.MaxValue : i).ThenBy(c => c.CreatedAt)
            .Select(c =>
            {
                var list = photoRows.Where(x => x.CollectionId == c.Id).OrderBy(x => x.Position).Select(PhotoDto).ToList();
                var cover = list.FirstOrDefault(x => x.Id == c.CoverPhotoId) ?? list.FirstOrDefault();
                return new PortfolioCollectionDto(c.Id, c.Title, c.Layout, c.CoverPhotoId, cover?.ThumbUrl, list, c.Kind);
            }).ToList();
    }

    /// <summary>The page's cover picture: the chosen one, else the first photo of the first collection.</summary>
    /// <summary>The picture the page shows as its cover: an uploaded one, else the chosen/first photo.</summary>
    internal static string? CoverUrl(Portfolio p, IReadOnlyList<PortfolioCollectionDto> cols, IReadOnlyList<PortfolioPhoto> photoRows) =>
        p.CoverImageUrl ?? Cover(p, cols, photoRows)?.Url;

    internal static PortfolioPhotoDto? Cover(Portfolio p, IReadOnlyList<PortfolioCollectionDto> cols, IReadOnlyList<PortfolioPhoto> photoRows) =>
        photoRows.Where(x => x.Id == p.CoverPhotoId).Select(PhotoDto).FirstOrDefault()
        ?? cols.SelectMany(c => c.Photos).FirstOrDefault();

    internal static PortfolioDto Build(
        Portfolio p, IReadOnlyList<PortfolioSection> sectionRows, IReadOnlyList<PortfolioCollection> collectionRows,
        IReadOnlyList<PortfolioPhoto> photoRows, int unread, string siteBase)
    {
        var cols = Collections(sectionRows, collectionRows, photoRows);
        var byId = photoRows.ToDictionary(x => x.Id);
        var sectionDtos = sectionRows.Select(s =>
        {
            var d = Read(s.DataJson);
            var photoUrl = d.PhotoId is { } pid && byId.TryGetValue(pid, out var ph) ? ph.Url : null;
            return new PortfolioSectionDto(
                s.Id, s.Kind, s.Hidden, s.Kind == PortfolioSectionKinds.Cover, s.CollectionId,
                d.Title, d.Body, photoUrl is null ? null : d.PhotoId, photoUrl,
                d.TemplateIds ?? [], d.Packages ?? [], d.Reviews ?? []);
        }).ToList();
        var cover = Cover(p, cols, photoRows);
        return new PortfolioDto(
            p.Id, p.Handle, p.Handle is null ? null : $"{siteBase}/@{p.Handle}", p.DisplayName, p.Tagline, p.LogoUrl,
            p.CoverImageUrl is null && cover is not null ? p.CoverPhotoId : null, p.CoverImageUrl ?? cover?.Url, p.Theme, p.Accent, p.Font,
            new PortfolioContactDto(p.WhatsApp, p.Viber, p.Instagram, p.Phone, p.Email, p.EnquiriesOn),
            p.IsPublished, p.PublishedAt, p.SetupDone, photoRows.Count, PhotoLimit, unread, sectionDtos, cols,
            p.CoverImageUrl,
            p.CoverImageUrl is not null ? "upload" : cover is null ? "none" : p.CoverPhotoId is { } chosen && cover.Id == chosen ? "photo" : "auto",
            PortfolioChoices.LogoPosition(p.LogoPosition), PortfolioChoices.LogoSize(p.LogoSize));
    }
}

/// <summary>Kind-specific content of a section, stored as jsonb.</summary>
public sealed record SectionData
{
    public string? Title { get; init; }
    public string? Body { get; init; }
    public Guid? PhotoId { get; init; }
    public List<Guid>? TemplateIds { get; init; }
    public List<PortfolioPackageDto>? Packages { get; init; }
    public List<PortfolioReviewDto>? Reviews { get; init; }
}
