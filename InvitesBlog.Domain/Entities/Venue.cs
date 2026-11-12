using InvitesBlog.Domain.Enums;

namespace InvitesBlog.Domain.Entities;

/// <summary>
/// LEGACY (owner, 2026-11-12): the old venue page's record (name, logo, code). Nothing reads it any more:
/// Studio (tier Venue) is a plan on the account, and an event's badge is its organiser's own name and
/// picture. The table is kept rather than dropped; only account deletion still removes a person's rows.
/// </summary>
public sealed class Venue
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public string Name { get; set; } = default!;

    /// <summary>Where it is, as guests would say it: "Maafushi", "Hulhumalé".</summary>
    public string? Place { get; set; }

    /// <summary>The venue's logo, shown on its QR cards and albums. Null shows the name alone.</summary>
    public string? LogoUrl { get; set; }

    /// <summary>
    /// What a couple types on their own event to hold it at this venue — so an invitation they made
    /// themselves gets the venue's albums too. Short, uppercase, unique.
    /// </summary>
    public string? Code { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// LEGACY (owner, 2026-11-12): venue staff no longer exist and give no access. The table is kept rather
/// than dropped; only account deletion still removes a person's rows.
/// </summary>
public sealed class VenueStaff
{
    public Guid Id { get; set; }
    public Guid VenueId { get; set; }

    /// <summary>Lowercased.</summary>
    public string Email { get; set; } = default!;

    public string? Name { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// RETIRED: from when Premium was called Studio (not today's Studio plan). An event pass that account held to give to a client's event: bought at the Studio price and
/// used once. <see cref="UsedOnCampaignId"/> is null until it is given.
/// </summary>
public sealed class PassCredit
{
    public Guid Id { get; set; }
    public Guid OwnerUserId { get; set; }
    public EventPassKind Kind { get; set; }

    /// <summary>What was paid for it, in the catalogue's currency; 0 when an admin gave it.</summary>
    public decimal Price { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public Guid? UsedOnCampaignId { get; set; }
    public DateTimeOffset? UsedAt { get; set; }
}
