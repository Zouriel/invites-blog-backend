namespace InvitesBlog.Application.Services.Contacts;

/// <summary>
/// A second contact we could offer to add to the caller's inbox, masked so the list itself never
/// discloses an address the caller has not proved they own.
/// </summary>
/// <param name="ContactType">"phone" | "email" — the kind of the contact being offered.</param>
/// <param name="Masked">Display form, e.g. <c>a•••d@example.com</c> or <c>+960•••9157</c>.</param>
/// <param name="InviteCount">How many invitations would join the inbox if they link it.</param>
public sealed record LinkableContact(string ContactType, string Masked, int InviteCount);

/// <summary>Result of proving a second contact.</summary>
/// <param name="Linked">False when the pairing was already on file.</param>
public sealed record ContactLinkResult(bool Linked, string ContactType, string Masked);

/// <summary>
/// Widens an invitee's inbox to a second contact — the case where a host invited someone by email
/// but the person signs in with the phone number a different host had for them (or vice versa).
/// <para>
/// The pairing is discovered from guest rows, which nobody verifies: whoever uploads a list can put
/// any email beside any number. So a discovered pairing only decides what we OFFER; it never grants
/// access. The caller has to prove the second contact with a one-time code, and only then is the
/// link recorded and honoured.
/// </para>
/// </summary>
public interface IContactLinkService
{
    /// <summary>Contacts the caller could add, discovered from guest rows and not yet linked.</summary>
    Task<IReadOnlyList<LinkableContact>> GetLinkableAsync(CancellationToken ct = default);

    /// <summary>
    /// Sends a code to a contact the caller may link. The contact is named by its masked form so an
    /// arbitrary address can't be probed: only pairings already offered by
    /// <see cref="GetLinkableAsync"/> are accepted.
    /// </summary>
    Task<Guid> RequestLinkCodeAsync(string maskedContact, CancellationToken ct = default);

    /// <summary>Consumes the code and records the pairing. Rejects anything not currently offered.</summary>
    Task<ContactLinkResult> VerifyLinkAsync(Guid challengeId, string code, CancellationToken ct = default);
}
