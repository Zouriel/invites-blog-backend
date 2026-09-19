namespace InvitesBlog.Application.Dtos.Campaigns;

// Request DTOs for the campaign builder + no-registration access (§10.3 / §4.6). Field names are
// kept identical to the legacy Minimal-API request records so the Angular inviter app is unaffected.

/// <summary>Create a draft campaign from a template (§10.3).</summary>
/// <param name="Kind">"invitation" or "saveTheDate"; left out, a design from the Save the Date category makes a save the date.</param>
public sealed record CreateCampaignRequest(Guid TemplateId, string Title, string? Kind = null);

/// <summary>Patch the campaign content/theme/rules and event metadata (partial — nulls are ignored).</summary>
/// <summary>The campaign's cover photo. Null clears it, falling back to the template's preview.</summary>
public sealed record SetCoverRequest(string? Url);

/// <summary>
/// Renames the campaign. This is the name the HOST files it under — what they see in their list and
/// on the dashboard — not the title printed inside the invitation, which is a template field the
/// guests read. Campaigns are created named after their template ("Gilded Hour invitation"), so
/// without this every invitation a host makes from one template is indistinguishable from the rest.
/// </summary>
public sealed record RenameCampaignRequest(string Title);

public sealed record UpdateContentRequest(
    string? CustomContentJson,
    string? ThemeOverridesJson,
    string? RulesJson,
    bool? IsSensitive,
    DateTimeOffset? EventStartAt,
    DateTimeOffset? EventEndAt,
    string? EventType,
    bool? AllDay = null);

/// <summary>Set the venue block (stored inside CustomContentJson.venue).</summary>
public sealed record UpdateVenueRequest(
    // Nullable so an optional/blank venue step binds — non-nullable ref-type params are implicitly required.
    string? VenueType,
    string? VenueName,
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
    // Nullable so a phone-less (email-only) host binds — non-nullable ref-type params are implicitly required.
    string? Phone,
    string Email,
    string? Organization,
    string? BillingName,
    string? BillingCountry,
    string? DefaultCountry);

/// <summary>Replace the campaign delivery settings JSON.</summary>
public sealed record UpdateDeliverySettingsRequest(string DeliverySettingsJson);

/// <summary>One guest role and the template content blocks it unlocks (dress code, message, etc.).</summary>
/// <param name="Palette">
/// The dress colours for guests holding this role: hex values like <c>#8a1c2b</c>, usually four shades
/// of one colour. Empty when the host set none.
/// </param>
public sealed record RoleDefinitionDto(
    string Name, IReadOnlyList<string> ContentBlocks, IReadOnlyList<string>? Palette = null);

/// <summary>Set the campaign's guest roles (§roles step). The server also regenerates RulesJson so
/// each role's content blocks are shown to guests holding that role.</summary>
public sealed record SetRolesRequest(IReadOnlyList<RoleDefinitionDto> Roles);

