namespace InvitesBlog.Application.Common;

/// <summary>
/// The file extension an upload is stored under when its own name doesn't carry one. The extension
/// is only part of the storage key — what the file is SERVED as is the content type stored with it —
/// so an unknown type gets a neutral <c>.img</c> rather than a guess that would mislabel it.
///
/// <para>One table because campaign images, event photos and designer posters each carried their
/// own copy of it, and the copies had drifted apart (only one knew about HEIC or video).</para>
/// </summary>
public static class MediaFileTypes
{
    /// <summary>The extension, with its leading dot (".jpg").</summary>
    public static string ExtensionFor(string contentType) => contentType.ToLowerInvariant() switch
    {
        "image/jpeg" => ".jpg",
        "image/png" => ".png",
        "image/gif" => ".gif",
        "image/webp" => ".webp",
        "image/avif" => ".avif",
        "image/heic" => ".heic",
        "video/mp4" => ".mp4",
        "video/quicktime" => ".mov",
        "video/webm" => ".webm",
        _ => contentType.StartsWith("video/", StringComparison.OrdinalIgnoreCase) ? ".mp4" : ".img"
    };
}
