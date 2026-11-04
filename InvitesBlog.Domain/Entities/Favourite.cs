namespace InvitesBlog.Domain.Entities;

/// <summary>
/// A photo or video someone hearted in an album they can see. A reference, never a copy: it takes
/// no album space, and it stops showing (in their Favourites and in the counts) once the media is
/// deleted or they can no longer see it.
/// </summary>
public sealed class Favourite
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid PhotoId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}
