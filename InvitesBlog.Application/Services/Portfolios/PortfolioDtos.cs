namespace InvitesBlog.Application.Services.Portfolios;

public sealed record PortfolioContactDto(string? WhatsApp, string? Viber, string? Instagram, string? Phone, string? Email, bool Enquiries);

public sealed record PortfolioPackageDto(string Name, string? Price, IReadOnlyList<string>? Lines);

public sealed record PortfolioReviewDto(string Quote, string? Name);

public sealed record PortfolioPhotoDto(Guid Id, string Url, string ThumbUrl, int Width, int Height, Guid? CampaignId, Guid? SourcePhotoId);

/// <param name="Kind">events (picks from event albums) | uploads (uploaded straight into it). Fixed when made.</param>
public sealed record PortfolioCollectionDto(Guid Id, string Title, string Layout, Guid? CoverPhotoId, string? CoverUrl, IReadOnlyList<PortfolioPhotoDto> Photos,
    string Kind = PortfolioCollectionKinds.Events);

public sealed record PortfolioSectionDto(
    Guid Id, string Kind, bool Hidden, bool Locked, Guid? CollectionId,
    string? Title, string? Body, Guid? PhotoId, string? PhotoUrl,
    IReadOnlyList<Guid> TemplateIds, IReadOnlyList<PortfolioPackageDto> Packages, IReadOnlyList<PortfolioReviewDto> Reviews);

public sealed record PortfolioDto(
    Guid Id, string? Handle, string? PublicUrl, string DisplayName, string? Tagline, string? LogoUrl,
    Guid? CoverPhotoId, string? CoverUrl, string Theme, string Accent, string Font, PortfolioContactDto Contact,
    bool Published, DateTimeOffset? PublishedAt, bool SetupDone, int PhotoCount, int PhotoLimit, int UnreadEnquiries,
    IReadOnlyList<PortfolioSectionDto> Sections, IReadOnlyList<PortfolioCollectionDto> Collections,
    string? CoverImageUrl = null, string CoverSource = "none", string LogoPosition = "bottom-left", string LogoSize = "medium");

/// <summary>PUT /api/me/portfolio. Null = unchanged; "" clears a text.</summary>
public sealed record UpdatePortfolioRequest(
    string? DisplayName, string? Tagline, string? Theme, string? Accent, string? Font, Guid? CoverPhotoId,
    bool? ClearCover, UpdatePortfolioContact? Contact, bool? SetupDone, string? LogoPosition = null, string? LogoSize = null);

public sealed record UpdatePortfolioContact(string? WhatsApp, string? Viber, string? Instagram, string? Phone, string? Email, bool? Enquiries);

/// <param name="Reason">ok | yours | invalid | reserved | taken.</param>
public sealed record HandleCheckDto(string Handle, bool Available, string Reason, string Message, string? Suggestion);

public sealed record SetHandleRequest(string? Handle);

public sealed record SectionRequest(
    string? Kind, bool? Hidden, string? Title, string? Body, Guid? PhotoId, bool? ClearPhoto,
    IReadOnlyList<Guid>? TemplateIds, IReadOnlyList<PortfolioPackageDto>? Packages, IReadOnlyList<PortfolioReviewDto>? Reviews);

public sealed record OrderRequest(IReadOnlyList<Guid>? Ids);

public sealed record PhotoOrderRequest(IReadOnlyList<Guid>? PhotoIds);

/// <param name="Kind">Only read when the collection is made (events | uploads, default events); never changes after.</param>
public sealed record CollectionRequest(string? Title, string? Layout, Guid? CoverPhotoId, bool? ClearCover, string? Kind = null);

/// <summary>POST /api/me/portfolio/collections/{id}/photos: one uploaded picture, stored.</summary>
public sealed record UploadPortfolioPhotoResultDto(PortfolioPhotoDto Photo, int PhotoCount, int PhotoLimit, int PhotosLeft, PortfolioDto Portfolio);

public sealed record AddPortfolioPhotosRequest(IReadOnlyList<Guid>? PhotoIds, Guid? CollectionId, string? NewCollectionTitle);

/// <param name="Reason">guest | other_account | video | missing (the file couldn't be read).</param>
public sealed record SkippedPortfolioPhotoDto(Guid PhotoId, string Reason);

public sealed record AddPortfolioPhotosResultDto(
    int Added, int AlreadyIn, int SkippedGuest, int SkippedVideo, Guid CollectionId, string Message, PortfolioDto Portfolio,
    int SkippedOther = 0, int SkippedMissing = 0, IReadOnlyList<SkippedPortfolioPhotoDto>? Skipped = null);

/// <summary>One album photo as the portfolio picker sees it: can it go in, and if not, why.</summary>
/// <param name="Reason">null when eligible; guest | other_account | video.</param>
public sealed record PortfolioEventPhotoDto(
    Guid Id, string ThumbUrl, string Url, int Width, int Height, bool Eligible, string? Reason, string? ReasonText, bool InPortfolio);

public sealed record PortfolioEventPhotosDto(
    bool Consented, int EligibleCount, int GuestCount, int VideoCount, IReadOnlyList<PortfolioEventPhotoDto> Photos);

public sealed record PublishRequest(bool Published);

public sealed record PortfolioEnquiryDto(Guid Id, string Name, string Contact, string? EventDate, string Message, DateTimeOffset CreatedAt, bool Read);

public sealed record PortfolioDesignDto(Guid Id, string Name, string? PreviewUrl);

public sealed record PortfolioConsentDto(bool Consented, DateTimeOffset? ConsentedAt, int PhotosInPortfolio);

public sealed record SetConsentRequest(bool Consented);

// ----- public -----

public sealed record PublicPortfolioSectionDto(
    string Kind, string? Title, string? Body, string? PhotoUrl,
    PublicCollectionDto? Collection, IReadOnlyList<PortfolioDesignDto>? Designs,
    IReadOnlyList<PortfolioPackageDto>? Packages, IReadOnlyList<PortfolioReviewDto>? Reviews);

public sealed record PublicCollectionDto(Guid Id, string Title, string Layout, string? CoverUrl, IReadOnlyList<PortfolioPhotoDto> Photos,
    string Kind = PortfolioCollectionKinds.Events);

public sealed record PublicPortfolioDto(
    string Handle, string Url, string DisplayName, string? Tagline, string? LogoUrl, string? CoverUrl,
    string Theme, string Accent, string Font, PortfolioContactDto Contact, IReadOnlyList<PublicPortfolioSectionDto> Sections,
    string LogoPosition = "bottom-left", string LogoSize = "medium");

/// <summary>What the public page/endpoint finds for a handle.</summary>
public sealed record PortfolioLookup(PublicPortfolioDto? Portfolio, string? RedirectTo);

public sealed record PortfolioEnquiryRequest(string? Name, string? Contact, string? EventDate, string? Message, string? Website);

public sealed record PortfolioReportRequest(Guid? PhotoId, string? Reason, string? Note, string? Contact);

public sealed record PortfolioOkDto(bool Ok, string Message);
