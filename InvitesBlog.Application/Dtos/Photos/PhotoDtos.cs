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
    /// <summary>
    /// The shot as taken. Nothing renders this — it is what "download" hands over. For a video it is
    /// the same object as <c>Url</c>: there is no smaller viewing copy without transcoding.
    /// </summary>
    string OriginalUrl,
    /// <summary>
    /// What this actually is, so a caller can tell a video from a photograph without guessing at the
    /// file extension. <c>ThumbUrl</c> is a still either way; for a video it is the poster frame.
    /// </summary>
    string ContentType,
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
