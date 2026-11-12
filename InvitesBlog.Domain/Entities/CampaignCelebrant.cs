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

    /// <summary>
    /// Chosen to take over the event when its owner deleted their account and this person had none yet.
    /// They become its owner the first time they sign in with this contact.
    /// </summary>
    public bool InheritsOwnership { get; set; }

    /// <summary>When the "you've been added" email went out, or null if it never did.</summary>
    public DateTimeOffset? NotifiedAt { get; set; }

    /// <summary>
    /// When the person this row matches saw that they were added: they opened the event's dashboard,
    /// or dismissed the "you've been added" card (POST /api/me/roles/{campaignId}/ack). Null until then,
    /// which is what lists the row in <c>newRoles</c> of GET /api/me/guidance (docs/GUIDANCE.md).
    /// Rows that existed before guidance shipped were set to the migration's time, so nobody gets a
    /// card for an event they have known about for months.
    /// </summary>
    public DateTimeOffset? AcknowledgedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
