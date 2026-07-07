namespace InvitesBlog.Application.Abstractions;

/// <summary>
/// Dispatches invites for a campaign / resends a single guest's invite. Implemented by the
/// Infrastructure dispatch service; exposed as a port so Application services can depend on it
/// without an Application→Infrastructure reference cycle.
/// </summary>
public interface IInviteDispatcher
{
    Task DispatchCampaignAsync(Guid campaignId, CancellationToken ct = default);
    Task<bool> ResendAsync(Guid guestId, CancellationToken ct = default);
}
