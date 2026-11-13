namespace InvitesBlog.Application.Services.Portfolios;

public sealed record PortfolioContactDto(string? WhatsApp, string? Viber, string? Instagram, string? Phone, string? Email, bool Enquiries);

public sealed record PortfolioPackageDto(string Name, string? Price, IReadOnlyList<string>? Lines);

public sealed record PortfolioReviewDto(string Quote, string? Name);

public sealed record PortfolioPhotoDto(Guid Id, string Url, string ThumbUrl, int Width, int Height, Guid? CampaignId, Guid? SourcePhotoId);

public sealed record PortfolioCollectionDto(Guid Id, string Title, string Layout, Guid? CoverPhotoId, string? CoverUrl, IReadOnlyList<PortfolioPhotoDto> Photos);

public sealed record PortfolioSectionDto(
    Guid Id, string Kind, bool Hidden, bool Locked, Guid? CollectionId,
    string? Title, string? Body, Guid? PhotoId, string? PhotoUrl,
    IReadOnlyList<Guid> TemplateIds, IReadOnlyList<PortfolioPackageDto> Packages, IReadOnlyList<PortfolioReviewDto> Reviews);

public sealed record PortfolioDto(
    Guid Id, string? Handle, string? PublicUrl, string DisplayName, string? Tagline, string? LogoUrl,
    Guid? CoverPhotoId, string? CoverUrl, string Theme, string Accent, string Font, PortfolioContactDto Contact,
    bool Published, DateTimeOffset? PublishedAt, bool SetupDone, int PhotoCount, int PhotoLimit, int UnreadEnquiries,
    IReadOnlyList<PortfolioSectionDto> Sections, IReadOnlyList<PortfolioCollectionDto> Collections);

/// <summary>PUT /api/me/portfolio. Null = unchanged; "" clears a text.</summary>
public sealed record UpdatePortfolioRequest(
    string? DisplayName, string? Tagline, string? Theme, string? Accent, string? Font, Guid? CoverPhotoId,
    bool? ClearCover, UpdatePortfolioContact? Contact, bool? SetupDone);

public sealed record UpdatePortfolioContact(string? WhatsApp, string? Viber, string? Instagram, string? Phone, string? Email, bool? Enquiries);

/// <param name="Reason">ok | yours | invalid | reserved | taken.</param>
public sealed record HandleCheckDto(string Handle, bool Available, string Reason, string Message, string? Suggestion);

public sealed record SetHandleRequest(string? Handle);

public sealed record SectionRequest(
    string? Kind, bool? Hidden, string? Title, string? Body, Guid? PhotoId, bool? ClearPhoto,
    IReadOnlyList<Guid>? TemplateIds, IReadOnlyList<PortfolioPackageDto>? Packages, IReadOnlyList<PortfolioReviewDto>? Reviews);

public sealed record OrderRequest(IReadOnlyList<Guid>? Ids);

public sealed record PhotoOrderRequest(IReadOnlyList<Guid>? PhotoIds);

public sealed record CollectionRequest(string? Title, string? Layout, Guid? CoverPhotoId, bool? ClearCover);

public sealed record AddPortfolioPhotosRequest(IReadOnlyList<Guid>? PhotoIds, Guid? CollectionId, string? NewCollectionTitle);

public sealed record AddPortfolioPhotosResultDto(
    int Added, int AlreadyIn, int SkippedGuest, int SkippedVideo, Guid CollectionId, string Message, PortfolioDto Portfolio);

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

public sealed record PublicCollectionDto(Guid Id, string Title, string Layout, string? CoverUrl, IReadOnlyList<PortfolioPhotoDto> Photos);

public sealed record PublicPortfolioDto(
    string Handle, string Url, string DisplayName, string? Tagline, string? LogoUrl, string? CoverUrl,
    string Theme, string Accent, string Font, PortfolioContactDto Contact, IReadOnlyList<PublicPortfolioSectionDto> Sections);

/// <summary>What the public page/endpoint finds for a handle.</summary>
public sealed record PortfolioLookup(PublicPortfolioDto? Portfolio, string? RedirectTo);

public sealed record PortfolioEnquiryRequest(string? Name, string? Contact, string? EventDate, string? Message, string? Website);

public sealed record PortfolioReportRequest(Guid? PhotoId, string? Reason, string? Note, string? Contact);

public sealed record PortfolioOkDto(bool Ok, string Message);
