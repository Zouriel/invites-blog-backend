namespace InvitesBlog.Domain.Entities;

/// <summary>
/// Somebody an event is FOR: the couple at a wedding, the child at a birthday. Neither the person
/// who organised it nor a guest.
///
/// <para>Linked to an account by contact, never by user id: whenever someone signs in with this email
/// or phone, the event is in their list, including years later and including if they had no account
/// when they were added. Read-only by default; <see cref="CanManage"/> lets them run it like the
/// organiser.</para>
/// </summary>
public sealed class CampaignCelebrant
{
    public Guid Id { get; set; }
    public Guid CampaignId { get; set; }
    public string Name { get; set; } = default!;

    /// <summary>Lowercased. At least one of this and <see cref="PhoneE164"/> is set.</summary>
    public string? Email { get; set; }

    /// <summary>E.164, normalised the same way account phones are.</summary>
    public string? PhoneE164 { get; set; }

    /// <summary>Whether they may edit and send like the organiser. Only the organiser can turn it on.</summary>
    public bool CanManage { get; set; }

    /// <summary>When the "you've been added" email went out, or null if it never did.</summary>
    public DateTimeOffset? NotifiedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
