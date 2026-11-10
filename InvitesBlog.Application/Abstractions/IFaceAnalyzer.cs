namespace InvitesBlog.Application.Abstractions;

/// <summary>
/// Finds the faces in a photo and gives each a fingerprint, on our own server (never a cloud API). Used only
/// for albums with face grouping on.
/// </summary>
public interface IFaceAnalyzer
{
    /// <summary>False when the model files aren't there: face grouping then reports itself unavailable.</summary>
    bool Available { get; }

    /// <summary>The faces in the picture, upright (EXIF orientation applied). Throws on an unreadable picture.</summary>
    Task<IReadOnlyList<DetectedFace>> AnalyzeAsync(byte[] image, CancellationToken ct);
}

/// <summary>A face: where it is as fractions (0-1) of the upright picture, how sure the detector is, and its fingerprint (unit length).</summary>
public sealed record DetectedFace(float X, float Y, float W, float H, float Score, float[] Embedding);
