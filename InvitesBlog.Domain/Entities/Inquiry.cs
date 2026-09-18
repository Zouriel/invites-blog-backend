namespace InvitesBlog.Domain.Entities;

/// <summary>
/// A customer inquiry for a made-to-order invitation, captured from the public "Start an inquiry" form.
/// It arrives <c>unattended</c>; the owner talks to the customer, keeps consultation notes
/// (colors/references/notes) and marks it attended. The invitation is then made in the designer and
/// published for the customer's email — outside this record.
/// </summary>
public sealed class Inquiry
{
    public Guid Id { get; set; }

    // ----- Submitted by the customer -----
    public string Name { get; set; } = default!;
    /// <summary>Lowercased.</summary>
    public string Email { get; set; } = default!;
    public string Occasion { get; set; } = default!;
    public string Message { get; set; } = default!;

    // ----- Filled by the owner after meeting the customer (all nullable) -----
    public string? Colors { get; set; }
    public string? References { get; set; }
    public string? Notes { get; set; }

    // ----- Pipeline state -----
    /// <summary>True once the owner has met/consulted the customer about this inquiry.</summary>
    public bool HasAttended { get; set; }
    public DateTimeOffset? AttendedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
