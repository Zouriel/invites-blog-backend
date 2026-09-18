using InvitesBlog.Domain.Entities;

namespace InvitesBlog.Application.Designs;

/// <summary>
/// The one way to build a <see cref="TemplateVisibility.Imported"/> row — the placeholder template a
/// campaign pins when it has no gallery template behind it: an event started with nothing to render
/// (just its photos), and an event whose design the customer brought themselves.
///
/// <para>Every campaign must point at a template, so these rows exist to be pinned, not listed.
/// <see cref="TemplateVisibility.Imported"/> is what keeps them out of sight: every gallery read asks
/// for Public (or Dedicated once used), so this value is invisible to all of them. Three places used
/// to spell the row out by hand; they differed only in name, slug, description and package.</para>
/// </summary>
public static class ImportedTemplate
{
    /// <summary>
    /// An imported design declares no fields, and that is the whole point: the builder renders
    /// exactly what a template says it has, so an empty manifest is what makes the content step
    /// disappear for a design nobody can edit.
    /// </summary>
    public const string EmptyManifest = """{"fields":[],"images":[],"blocks":[],"theme":{}}""";

    /// <param name="name">Shown nowhere a guest looks; the event's title, so an admin can tell rows apart.</param>
    /// <param name="slug">Must be unique — callers put a fresh id or the campaign's id in it.</param>
    /// <param name="description">Never shown, but the column is NOT NULL.</param>
    /// <param name="packageUrl">The package folder, or empty while there is nothing to render yet.</param>
    /// <param name="previewUrl">
    /// The design's own picture, when it has one. A zip need contain no image at all, so for some
    /// designs there is genuinely nothing to preview; the column is NOT NULL, and empty is the honest
    /// value for "there isn't one" (readers go through <see cref="Common.TemplatePoster.OrNull"/>).
    /// </param>
    public static Template Create(
        string name, string slug, string description, string packageUrl = "", string? previewUrl = null)
    {
        var now = DateTimeOffset.UtcNow;
        return new Template
        {
            Id = Guid.NewGuid(),
            Name = name,
            Slug = slug,
            Description = description,
            Category = "Imported",
            Version = "1.0.0",
            PackageUrl = packageUrl,
            PreviewImageUrl = previewUrl ?? string.Empty,
            ManifestJson = EmptyManifest,
            SceneJson = "{}",
            Visibility = TemplateVisibility.Imported,
            IsActive = true,
            CreatedAt = now,
            UpdatedAt = now,
        };
    }
}
