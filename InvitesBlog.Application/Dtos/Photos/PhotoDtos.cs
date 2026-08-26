namespace InvitesBlog.Application.Dtos.Photos;

/// <summary>
/// One photo in an event's box. <c>CanDelete</c> is resolved per caller rather than left for the
/// client to work out — a guest may remove their own, a host may remove any, and neither rule is
/// something a browser should be trusted to evaluate.
/// </summary>
public sealed record EventPhotoDto(
    Guid Id,
    string Url,
    string ThumbUrl,
    /// <summary>The shot as taken. Nothing renders this — it is what "download" hands over.</summary>
    string OriginalUrl,
    int Width,
    int Height,
    string? UploaderName,
    bool CanDelete,
    DateTimeOffset CreatedAt);

/// <summary>An event's photo box as a guest or host sees it.</summary>
public sealed record EventPhotoBoxDto(
    Guid CampaignId,
    string EventTitle,
    int Count,
    /// <summary>Whether THIS caller may add to the box — false once the campaign is cancelled.</summary>
    bool CanUpload,
    IReadOnlyList<EventPhotoDto> Photos);
