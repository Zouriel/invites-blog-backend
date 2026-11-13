namespace InvitesBlog.Domain.Entities;

/// <summary>
/// A Studio account's public portfolio at <c>invites.blog/@handle</c> (docs/PORTFOLIO.md). One per account.
/// Only a Studio (plan kind Venue) in force may edit it or have it shown; when the Studio ends it is kept,
/// unavailable, for 90 days and then removed.
/// </summary>
public sealed class Portfolio
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }

    /// <summary>Lowercase a-z 0-9 . _ (3-30), unique. Null until the studio picks one.</summary>
    public string? Handle { get; set; }

    public string DisplayName { get; set; } = string.Empty;
    public string? Tagline { get; set; }
    public string? LogoUrl { get; set; }
    /// <summary>A <see cref="PortfolioPhoto"/> chosen as the cover; null = the first photo of the first collection.</summary>
    public Guid? CoverPhotoId { get; set; }
    /// <summary>A cover picture uploaded for the page itself (not a portfolio photo). Wins over <see cref="CoverPhotoId"/>.</summary>
    public string? CoverImageUrl { get; set; }

    /// <summary>Named choices (see PortfolioChoices).</summary>
    public string Theme { get; set; } = "oxblood";
    public string Accent { get; set; } = "theme";
    public string Font { get; set; } = "classic";

    public string? WhatsApp { get; set; }
    public string? Viber { get; set; }
    public string? Instagram { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public bool EnquiriesOn { get; set; } = true;

    public bool IsPublished { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public bool SetupDone { get; set; }

    /// <summary>
    /// When the retention sweep first found the account no longer on Studio (null while it is). The page is unavailable
    /// from then; 90 days later the portfolio and its files are deleted. Cleared if Studio comes back.
    /// </summary>
    public DateTimeOffset? StudioEndedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>One block on the page, in <see cref="Position"/> order. Kind-specific content is in <see cref="DataJson"/>.</summary>
public sealed class PortfolioSection
{
    public Guid Id { get; set; }
    public Guid PortfolioId { get; set; }
    /// <summary>cover | collection | about | designs | packages | reviews | contact.</summary>
    public string Kind { get; set; } = default!;
    public int Position { get; set; }
    public bool Hidden { get; set; }
    public Guid? CollectionId { get; set; }
    public string DataJson { get; set; } = "{}";
}

public sealed class PortfolioCollection
{
    public Guid Id { get; set; }
    public Guid PortfolioId { get; set; }
    public string Title { get; set; } = default!;
    /// <summary>grid | masonry | slideshow.</summary>
    public string Layout { get; set; } = "grid";
    public Guid? CoverPhotoId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A web-sized copy of one of the studio's own event photos. A copy, not a reference: it takes no event space,
/// and deleting the event or the album leaves it. Removed when the studio takes back the event's consent.
/// </summary>
public sealed class PortfolioPhoto
{
    public Guid Id { get; set; }
    public Guid PortfolioId { get; set; }
    public Guid CollectionId { get; set; }
    /// <summary>The event photo it was copied from (kept as a plain id: the photo may be deleted later).</summary>
    public Guid? SourcePhotoId { get; set; }
    /// <summary>The event it came from, so taking back that event's consent can remove it.</summary>
    public Guid? CampaignId { get; set; }
    public string Url { get; set; } = default!;
    public string ThumbUrl { get; set; } = default!;
    public int Width { get; set; }
    public int Height { get; set; }
    public int Position { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// The studio's own declaration, once per event, that the people the event is for agreed to its photos being
/// shown in the studio's portfolio. Who and when. Nobody else is told (owner, 2026-11-13).
/// </summary>
public sealed class PortfolioConsent
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid CampaignId { get; set; }
    public DateTimeOffset ConsentedAt { get; set; }
}

/// <summary>
/// "These are mine": a Studio organiser said the photos uploaded in its event under one name (or one guest) were taken
/// by the studio itself, so they become its own uploads. Kept after an undo (<see cref="UndoneAt"/>) as the record of
/// who said so and when.
/// </summary>
public sealed class PortfolioPhotoClaim
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid CampaignId { get; set; }
    /// <summary>"guest:{guid}" or "name:{lower-case name}".</summary>
    public string Key { get; set; } = default!;
    public string Name { get; set; } = default!;
    public int PhotoCount { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UndoneAt { get; set; }
}

/// <summary>A handle the studio changed away from: it redirects to the portfolio until <see cref="ExpiresAt"/>.</summary>
public sealed class PortfolioHandleRedirect
{
    public Guid Id { get; set; }
    public string Handle { get; set; } = default!;
    public Guid PortfolioId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>An enquiry a visitor sent from the portfolio page.</summary>
public sealed class PortfolioEnquiry
{
    public Guid Id { get; set; }
    public Guid PortfolioId { get; set; }
    public string Name { get; set; } = default!;
    public string Contact { get; set; } = default!;
    public string? EventDate { get; set; }
    public string Message { get; set; } = default!;
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
}
