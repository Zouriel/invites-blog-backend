namespace InvitesBlog.Application.Dtos.Campaigns;

// Request DTOs for the campaign builder + no-registration access (§10.3 / §4.6). Field names are
// kept identical to the legacy Minimal-API request records so the Angular inviter app is unaffected.

/// <summary>Create a draft campaign from a template (§10.3).</summary>
public sealed record CreateCampaignRequest(Guid TemplateId, string Title);

/// <summary>Patch the campaign content/theme/rules and event metadata (partial — nulls are ignored).</summary>
public sealed record UpdateContentRequest(
    string? CustomContentJson,
    string? ThemeOverridesJson,
    string? RulesJson,
    bool? IsSensitive,
    DateTimeOffset? EventStartAt,
    DateTimeOffset? EventEndAt,
    string? EventType);

/// <summary>Set the venue block (stored inside CustomContentJson.venue).</summary>
public sealed record UpdateVenueRequest(
    string VenueType,
    string VenueName,
    string? Address,
    string? MapLink,
    string? City,
    string? Room,
    string? ArrivalInstructions,
    string? ParkingInstructions,
    string? DressCode);

/// <summary>Inviter details — deduplicated by normalized email (§4.6.2).</summary>
public sealed record UpdateInviterRequest(
    string Name,
    string Phone,
    string Email,
    string? Organization,
    string? BillingName,
    string? BillingCountry,
    string? DefaultCountry);

/// <summary>Replace the campaign delivery settings JSON.</summary>
public sealed record UpdateDeliverySettingsRequest(string DeliverySettingsJson);

/// <summary>Request the dashboard links for an inviter's email (§4.6 recovery path).</summary>
public sealed record ResendLinkRequest(string Email);
