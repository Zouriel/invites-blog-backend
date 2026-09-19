using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Exceptions;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Plans;

/// <summary>
/// How many guests invites.blog may still email for an event. Sharing links is always free; emailing
/// each guest their own link is what's counted: the event's pass includes some (none on Free, 100
/// with a Party pass, 500 with a Wedding pass), and more are added a block at a time.
/// </summary>
/// <param name="Included">What the event's plan includes.</param>
/// <param name="Extra">Added on top: bought, or given by an admin (<c>Campaign.PaidInviteCapacity</c>).</param>
/// <param name="Used">Guests emailed so far. Each counts once; a resend is free.</param>
public sealed record SendingAllowanceDto(int Included, int Extra, int Used)
{
    public int Total => Included + Extra;
    public int Left => Math.Max(0, Total - Used);
}

public interface ISendingAllowanceService
{
    Task<SendingAllowanceDto> ForCampaignAsync(Guid campaignId, CancellationToken ct = default);
}

public sealed class SendingAllowanceService(
    ICampaignRepository campaigns,
    IInviteRepository invites,
    IPlanService plans) : ISendingAllowanceService
{
    public async Task<SendingAllowanceDto> ForCampaignAsync(Guid campaignId, CancellationToken ct = default)
    {
        var campaign = await campaigns.GetByIdAsync(campaignId, ct)
                       ?? throw new NotFoundException("That event no longer exists.");
        var plan = await plans.ForCampaignAsync(campaignId, ct);
        var used = await invites.Query().CountAsync(i => i.CampaignId == campaignId && i.FirstEmailedAt != null, ct);
        return new SendingAllowanceDto(plan.IncludedInvites, Math.Max(0, campaign.PaidInviteCapacity), used);
    }
}
