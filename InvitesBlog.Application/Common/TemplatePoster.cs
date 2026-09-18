namespace InvitesBlog.Application.Common;

/// <summary>
/// The one test for "is <c>Template.PreviewImageUrl</c> actually a picture?".
///
/// <para>For a long time a template published without a poster had its live page written into that
/// column as a stand-in (<c>{packageUrl}index.html</c>) — a whole invitation, not an image — and every
/// screen that showed a thumbnail then filtered it back out with its own slightly different check.
/// New publishes store an empty string instead, and old rows keep their stand-in; this helper makes
/// both harmless, so the API never hands a page (or nothing) out as an image.</para>
/// </summary>
public static class TemplatePoster
{
    /// <summary>
    /// The URL when it points at an image; null when it is empty, a page (<c>.html</c>) or a folder
    /// (a package URL ending in <c>/</c>). Callers show their own fallback for null.
    /// </summary>
    public static string? OrNull(string? url) =>
        string.IsNullOrWhiteSpace(url)
        || url.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
        || url.EndsWith('/')
            ? null
            : url;
}
