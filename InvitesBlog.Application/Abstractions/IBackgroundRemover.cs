namespace InvitesBlog.Application.Abstractions;

/// <summary>Cuts the subject out of a picture, leaving the background see-through.</summary>
public interface IBackgroundRemover
{
    /// <summary>False when the model isn't installed on this server.</summary>
    bool Available { get; }

    /// <summary>The picture as a PNG whose background is transparent, the same size as it was (capped).</summary>
    Task<byte[]> CutOutAsync(byte[] content, CancellationToken ct);
}

/// <param name="Data">The picture as a data: URL (as the design holds it).</param>
/// <param name="Name">The asset's name, for the new file's.</param>
public sealed record RemoveBackgroundRequest(string Data, string? Name);
