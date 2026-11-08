namespace InvitesBlog.Domain.Entities;

/// <summary>
/// How many AI pictures one account made in a calendar month (UTC). The monthly limit is checked
/// against <see cref="Count"/>; only pictures that arrived are counted.
/// </summary>
public sealed class AiPictureUsage
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    /// <summary>yyyy-MM, UTC.</summary>
    public string Month { get; set; } = default!;
    public int Count { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
