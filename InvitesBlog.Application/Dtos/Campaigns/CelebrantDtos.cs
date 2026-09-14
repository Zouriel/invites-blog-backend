namespace InvitesBlog.Application.Dtos.Campaigns;

public sealed record CelebrantDto(
    Guid Id, string Name, string? Email, string? Phone, bool CanManage, DateTimeOffset? NotifiedAt);

/// <param name="Notify">Send them a short "you've been added" email. Off by default in the UI.</param>
public sealed record AddCelebrantRequest(string Name, string? Email, string? Phone, bool Notify = false);

public sealed record SetCelebrantAccessRequest(bool CanManage);
