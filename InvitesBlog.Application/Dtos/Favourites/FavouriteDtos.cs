namespace InvitesBlog.Application.Dtos.Favourites;

/// <summary>What a heart tap returns: the item's new count, and whether the caller now has it.</summary>
public sealed record FavouriteStateDto(int FavouriteCount, bool Favourited);

/// <summary>
/// One item in someone's Favourites: the same fields an album tile has, plus the album it lives in,
/// so the page can filter by album and uploader exactly as an album page does.
/// </summary>
public sealed record FavouriteDto(
    Guid Id,
    string Url,
    string ThumbUrl,
    string OriginalUrl,
    string ContentType,
    int Width,
    int Height,
    string? UploaderName,
    bool CanDelete,
    DateTimeOffset CreatedAt,
    Guid? UploaderUserId,
    long SizeBytes,
    int FavouriteCount,
    bool Favourited,
    Guid? AlbumId,
    Guid? CampaignId,
    string AlbumTitle,
    DateTimeOffset FavouritedAt);

public sealed record FavouritesPageDto(IReadOnlyList<FavouriteDto> Items, int TotalCount, int Page, int PageSize);

public sealed record FavouritesDownloadRequest(IReadOnlyList<Guid>? Ids);
