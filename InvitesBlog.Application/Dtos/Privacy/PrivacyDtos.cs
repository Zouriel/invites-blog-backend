namespace InvitesBlog.Application.Dtos.Privacy;

/// <summary>Removal-page data shown before a guest confirms self-service deletion (§15.3).</summary>
public sealed record PrivacyRemovalInfoDto(
    string GuestName, string EventTitle, bool HasEmail, bool HasPhone, bool AlreadyRemoved);

/// <summary>Result of a guest self-service removal (§15.3).</summary>
public sealed record PrivacyRemovalResultDto(bool Removed);
