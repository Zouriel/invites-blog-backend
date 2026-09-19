namespace InvitesBlog.Application.Dtos.Admin;

/// <summary>An application user with the names of the roles assigned to them.</summary>
public sealed record AdminUserDto(
    Guid Id, string? Email, string DisplayName, bool IsActive, IReadOnlyList<string> Roles,
    /// <summary>None, Studio or Venue, as set, whether or not it has ended.</summary>
    string SubscriptionTier = "None",
    DateTimeOffset? SubscriptionEndsAt = null,
    bool SubscriptionActive = false,
    /// <summary>Passes a Studio account holds and hasn't given to a client yet, and of which kind.</summary>
    int PassCredits = 0,
    int PartyCredits = 0,
    int WeddingCredits = 0);

/// <summary>Sets an account's professional plan: None, Studio or Venue. <c>None</c> ends it now.</summary>
public sealed record SetSubscriptionRequest(string Tier, DateTimeOffset? EndsAt);

/// <summary>An event an account organised, with its pass and how long its photos are kept.</summary>
/// <param name="Pass">None, Party or Wedding.</param>
/// <param name="Plan">What covers it now: Free, PartyPass, WeddingPass or Venue.</param>
/// <param name="CoveredUntil">When its photos start to lapse; null while a venue covers it.</param>
/// <param name="Phase">Active, UploadsClosed, OrganiserOnly or Deleted.</param>
/// <param name="Sending">Emailed invitations: included, added, used.</param>
public sealed record AdminUserEventDto(
    Guid Id, string Title, DateTimeOffset EventStartAt, string Pass, DateTimeOffset? EventPassUntil, bool PassActive,
    DateTimeOffset? KeepPhotosUntil, string Plan = "Free", DateTimeOffset? CoveredUntil = null, string Phase = "Active",
    InvitesBlog.Application.Plans.SendingAllowanceDto? Sending = null);

/// <summary>Emailed invitations to add to an event (negative takes unused ones back).</summary>
public sealed record AddSendingRequest(int Invitations);

/// <summary>Gives an event a pass (<c>Party</c> or <c>Wedding</c>) or takes it away (<c>None</c>).</summary>
public sealed record SetEventPassRequest(string Kind);

/// <summary>"Keep your photos" for this many years; 0 takes it away.</summary>
public sealed record KeepPhotosRequest(int Years);

/// <summary>Passes to add to a Studio account (a positive count) or unused ones to take away (negative).</summary>
public sealed record AdjustPassCreditsRequest(string Kind, int Count);

/// <summary>A role with the names of the permissions it grants.</summary>
public sealed record AdminRoleDto(
    Guid Id, string Name, string Description, bool IsSystem, IReadOnlyList<string> Permissions);

/// <summary>A single permission definition.</summary>
public sealed record AdminPermissionDto(Guid Id, string Name, string Group, string Description);

/// <summary>A hashed suppression-list entry (§15.3).</summary>
public sealed record SuppressionEntryDto(
    Guid Id, string ContactHash, string ContactType, DateTimeOffset CreatedAt);

/// <summary>An audit-log record (§15.6).</summary>
public sealed record AuditLogDto(
    Guid Id, string Action, string? Actor, Guid? CampaignId, string DataJson, DateTimeOffset CreatedAt);

/// <summary>Granting or taking away one role from one account.</summary>
/// <param name="Role">The role's NAME, which is what the seeder and every check use.</param>
/// <param name="Granted">True to give it, false to take it away.</param>
public sealed record SetUserRoleRequest(string Role, bool Granted);
