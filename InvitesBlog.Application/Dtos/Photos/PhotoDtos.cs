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
    DateTimeOffset CreatedAt,
    /// <summary>The account that added it, when known: what Block and Report act on.</summary>
    Guid? UploaderUserId = null,
    /// <summary>
    /// What the item counts against its album, in bytes: the original plus its viewing copies, so a little
    /// more than saving or sharing it downloads. The app shows it as "about".
    /// </summary>
    long SizeBytes = 0,
    /// <summary>How many people hearted this (accounts still active). Shown in a corner of the tile.</summary>
    int FavouriteCount = 0,
    /// <summary>Whether the signed-in caller hearted it. Always false for a guest without an account.</summary>
    bool Favourited = false,
    /// <summary>The face groups (People) seen in it, when its album has face grouping on. Null otherwise.</summary>
    IReadOnlyList<Guid>? People = null);

/// <summary>An event's photo box as a guest or host sees it.</summary>
public sealed record EventPhotoBoxDto(
    Guid CampaignId,
    string EventTitle,
    int Count,
    /// <summary>
    /// Whether THIS caller may add to the box right now. It answers the same question the writer
    /// asks — the night, the quota, the cancellation — so a control that is offered is one that
    /// will work.
    /// </summary>
    bool CanUpload,
    IReadOnlyList<EventPhotoDto> Photos,
    /// <summary>
    /// Why adding is off, in the words the writer would have used. Null while it is on. Without it a
    /// button simply disappears, and "the button is gone" is not an answer to "why can't I add the
    /// photos from last night".
    /// </summary>
    string? ClosedNote = null,
    /// <summary>The venue the event is held at, whose name and logo head its albums.</summary>
    string? VenueName = null,
    string? VenueLogoUrl = null,
    /// <summary>A save the date: no album, so no photos to show and nothing to add.</summary>
    bool SaveTheDate = false,
    /// <summary>
    /// Face grouping, when any album in the box has it on: the notice to show, the People filter's groups, and
    /// whether this caller may name them. Null when it's off everywhere.
    /// </summary>
    FaceGroupingDto? Faces = null);

/// <summary>
/// Face grouping in a box. <c>People</c> are the groups worth filtering by (named, or in more than one photo),
/// most photos first. <c>Waiting</c> is how many photos are still to be looked at.
/// </summary>
public sealed record FaceGroupingDto(bool On, bool CanName, int Waiting, IReadOnlyList<FacePersonDto> People);

/// <summary>
/// One face group. The cover is a photo's thumbnail plus where the face is in it (fractions, 0-1), so a client
/// crops it on screen and no face picture is ever stored.
/// </summary>
public sealed record FacePersonDto(
    Guid Id, string? Name, int PhotoCount, string CoverUrl, float CoverX, float CoverY, float CoverW, float CoverH);

/// <summary>Naming a face group. Blank clears the name; a name another group already has merges the two.</summary>
public sealed record NameFacePersonRequest(string? Name);

/// <summary>"Same person as…": the group to move this one's faces into.</summary>
public sealed record MergeFacePersonRequest(Guid Into);
