using InvitesBlog.Application.Dtos.Privacy;

namespace InvitesBlog.Application.Services.Privacy;

/// <summary>Guest self-service data removal (§15.3): view removal info, then anonymize + suppress.</summary>
public interface IPrivacyService
{
    Task<PrivacyRemovalInfoDto> GetRemovalInfoAsync(string token, CancellationToken ct = default);
    Task<PrivacyRemovalResultDto> RemoveAsync(string token, CancellationToken ct = default);
}
