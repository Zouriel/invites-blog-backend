namespace InvitesBlog.Domain.Entities;

/// <summary>
/// One face found in one photo of an album with face grouping on (<see cref="MediaBucket.FaceGroupingOn"/>).
///
/// <para><b>This is biometric data.</b> The <see cref="Embedding"/> is a face "fingerprint": the law counts it
/// as biometric data even though it never leaves the album. So it lives only here, is never compared across
/// albums, and goes with the photo, with the album, when the host switches grouping off, and when the person
/// opts out ("ignore my face"). The rows cascade from both the photo and the album, so a hard delete of either
/// can't leave one behind.</para>
/// </summary>
public sealed class PhotoFace
{
    public Guid Id { get; set; }
    public Guid BucketId { get; set; }
    public Guid PhotoId { get; set; }

    /// <summary>The group this face was put in. Every face that is kept has one.</summary>
    public Guid PersonId { get; set; }

    /// <summary>Where the face is, as fractions (0-1) of the photo's width and height, after turning it upright.</summary>
    public float X { get; set; }
    public float Y { get; set; }
    public float W { get; set; }
    public float H { get; set; }

    /// <summary>How sure the detector was (0-1).</summary>
    public float Score { get; set; }

    /// <summary>The face fingerprint (SFace, 128 numbers, unit length).</summary>
    public float[] Embedding { get; set; } = [];

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A group of faces in one album that look like the same person: the People section of the album filter.
/// Only the host names it. When the person opts out it becomes <see cref="Ignored"/>: its faces are deleted and
/// only <see cref="Centroid"/> is kept, so that new photos of them aren't grouped again; it goes with the album
/// or when grouping is switched off, like everything else here.
/// </summary>
public sealed class FacePerson
{
    public Guid Id { get; set; }
    public Guid BucketId { get; set; }

    /// <summary>What the host called them. Null until named.</summary>
    public string? Name { get; set; }

    public const int MaxNameLength = 60;

    /// <summary>"Ignore my face": nothing of theirs is shown or kept except <see cref="Centroid"/>.</summary>
    public bool Ignored { get; set; }

    /// <summary>The average of the group's fingerprints (unit length), what a new face is compared with.</summary>
    public float[] Centroid { get; set; } = [];

    /// <summary>How many faces went into <see cref="Centroid"/>, so the average can be updated one face at a time.</summary>
    public int FaceCount { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// A photo waiting to have its faces found. Its id is the photo's. Removed once the photo is done; a photo
/// that keeps failing stays as <see cref="FaceJobStatus.Failed"/> so it isn't tried for ever.
/// </summary>
public sealed class FaceJob
{
    public Guid Id { get; set; }
    public Guid BucketId { get; set; }
    public FaceJobStatus Status { get; set; }
    public int Attempts { get; set; }
    public string? Note { get; set; }
    public DateTimeOffset NextAttemptAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

public enum FaceJobStatus
{
    Waiting = 0,
    Running = 1,
    Failed = 2,
}
