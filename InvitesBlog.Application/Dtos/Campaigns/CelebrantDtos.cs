namespace InvitesBlog.Application.Dtos.Campaigns;

public sealed record CelebrantDto(
    Guid Id, string Name, string? Email, string? Phone, bool CanManage, DateTimeOffset? NotifiedAt);

/// <param name="Notify">Send them a short "you've been added" email. Off by default in the UI.</param>
/// <param name="PhoneCountry">ISO2 country of <c>Phone</c> when typed without its country code.</param>
public sealed record AddCelebrantRequest(string Name, string? Email, string? Phone, bool Notify = false, string? PhoneCountry = null);

public sealed record SetCelebrantAccessRequest(bool CanManage);
