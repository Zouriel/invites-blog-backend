namespace InvitesBlog.Domain.Entities;

/// <summary>
/// A customer inquiry for a made-to-order (dedicated) invitation. Captured from the public "Start an
/// inquiry" form, then worked through a small pipeline: it arrives <c>unattended</c>; the owner meets the
/// customer and fills the consultation fields (colors/references/notes) + marks it attended; finally a
/// dedicated template is issued to the customer's email and <see cref="TemplateIssued"/> flips.
/// </summary>
public sealed class Inquiry
{
    public Guid Id { get; set; }

    // ----- Submitted by the customer -----
    public string Name { get; set; } = default!;
    /// <summary>Lowercased — becomes the issued template's <c>AssignedEmail</c> (the dedicated key).</summary>
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

    /// <summary>True once a dedicated template has been issued (and the "ready" email sent).</summary>
    public bool TemplateIssued { get; set; }
    public DateTimeOffset? TemplateIssuedAt { get; set; }
    /// <summary>The dedicated <see cref="Template"/> issued for this inquiry, if any.</summary>
    public Guid? IssuedTemplateId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
