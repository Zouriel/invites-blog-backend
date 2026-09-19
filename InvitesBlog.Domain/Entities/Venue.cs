using InvitesBlog.Domain.Enums;

namespace InvitesBlog.Domain.Entities;

/// <summary>
/// A resort or hall on the Venue plan. Every event at the property gets its photo albums under the
/// venue's name, and its staff run them.
///
/// <para>One per account: the plan is priced per property, so a group with several properties has an
/// account for each. The plan itself is the owner's <see cref="AppUser.SubscriptionTier"/>.</para>
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
/// Someone who runs a venue's events alongside its owner. Matched to an account by email, the way
/// celebrants are, so staff can be added before they have signed up.
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
/// An event pass a Studio account holds to give to a client's event: bought at the Studio price and
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
