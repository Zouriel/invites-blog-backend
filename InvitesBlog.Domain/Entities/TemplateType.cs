namespace InvitesBlog.Domain.Entities;

/// <summary>
/// A managed template category/type (Wedding, Birthday, …). Admins add these through the admin
/// portal; the gallery filter, the builder wizard, and template uploads pick from this list.
/// </summary>
public sealed class TemplateType
{
    public Guid Id { get; set; }
    public string Name { get; set; } = default!;
    public string Slug { get; set; } = default!;   // unique, URL-safe
    public int SortOrder { get; set; }
    public bool IsActive { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}
