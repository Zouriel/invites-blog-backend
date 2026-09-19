using System.Text.Json;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Plans;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Services.Studio;

/// <summary>
/// A Studio account's page: its clients' events, and what its clients pay for a pass — the Studio
/// discount comes off automatically on a design it made for them.
/// </summary>
public sealed record StudioOverviewDto(
    int DiscountPercent, decimal PartyPassPrice, decimal WeddingPassPrice, IReadOnlyList<StudioClientDto> Clients);

/// <summary>
/// One client's event. A client is someone the designer published a template FOR, or an event the
/// planner organised themselves — never a stranger who used a public design.
/// </summary>
/// <param name="Pass">The pass in force now: None, Party or Wedding.</param>
/// <param name="Mine">Organised by this account, so its dashboard opens for them.</param>
/// <param name="Discounted">Made from a design this Studio published for the client: their pass is discounted.</param>
public sealed record StudioClientDto(
    Guid CampaignId, string Title, DateTimeOffset EventStartAt, string Status,
    string? HostName, string? HostEmail, string? TemplateName,
    int GuestCount, int Going, string Pass, DateTimeOffset? PassUntil, bool Mine, bool Discounted = false);

public interface IStudioService
{
    Task<StudioOverviewDto> OverviewAsync(CancellationToken ct = default);
}

public sealed class StudioService(
    ICurrentUser currentUser,
    IPlanService plans,
    ICampaignRepository campaigns,
    ITemplateRepository templates,
    IInviterRepository inviters,
    IRepository<Guest> guests,
    IRepository<Invite> invites,
    IPriceBook prices,
    IPassOfferService offers) : IStudioService
{
    public async Task<StudioOverviewDto> OverviewAsync(CancellationToken ct = default)
    {
        var me = await RequireStudioAsync(ct);
        var p = await prices.CurrentAsync(ct);
        return new StudioOverviewDto(
            p.StudioDiscountPercent,
            p.StudioPassPrice(EventPassKind.Party),
            p.StudioPassPrice(EventPassKind.Wedding),
            await DescribeAsync(me, await ClientCampaigns(me).ToListAsync(ct), ct));
    }

    private async Task<Guid> RequireStudioAsync(CancellationToken ct)
    {
        var me = currentUser.UserId ?? throw new ForbiddenException("Sign in to open your Studio.");
        if (!await plans.IsStudioAsync(me, ct))
            throw new ForbiddenException("This page comes with the Studio plan.", "studio_required");
        return me;
    }

    /// <summary>Events made from templates this account published FOR someone, and events it organised.</summary>
    private IQueryable<Campaign> ClientCampaigns(Guid me)
    {
        var madeFor = templates.Query()
            .Where(t => t.DesignerUserId == me && t.AssignedEmail != null)
            .Select(t => t.Id);
        return campaigns.Query()
            .Where(c => c.Status != CampaignStatus.Cancelled
                        && (madeFor.Contains(c.TemplateId) || c.CreatedByUserId == me))
            .OrderBy(c => c.EventStartAt);
    }

    private async Task<IReadOnlyList<StudioClientDto>> DescribeAsync(Guid me, IReadOnlyList<Campaign> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];
        var ids = rows.Select(c => c.Id).ToList();
        var templateIds = rows.Select(c => c.TemplateId).Distinct().ToList();
        var inviterIds = rows.Select(c => c.InviterId).OfType<Guid>().Distinct().ToList();

        var names = await templates.Query()
            .Where(t => templateIds.Contains(t.Id) && t.Visibility != TemplateVisibility.Imported)
            .ToDictionaryAsync(t => t.Id, t => t.Name, ct);
        // The same rule the host's checkout uses: made for them, first use, Studio still active.
        var discounted = new HashSet<Guid>();
        foreach (var c in rows)
            if ((await offers.ForCampaignAsync(c.Id, ct)).DiscountPercent > 0) discounted.Add(c.Id);
        var hosts = await inviters.Query()
            .Where(i => inviterIds.Contains(i.Id))
            .ToDictionaryAsync(i => i.Id, ct);
        var guestCounts = await guests.Query()
            .Where(g => ids.Contains(g.CampaignId))
            .GroupBy(g => g.CampaignId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);
        var going = await invites.Query()
            .Where(i => ids.Contains(i.CampaignId) && i.RsvpStatus == RsvpStatus.Going)
            .GroupBy(i => i.CampaignId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var now = DateTimeOffset.UtcNow;
        return rows.Select(c =>
        {
            var host = c.InviterId is { } h ? hosts.GetValueOrDefault(h) : null;
            var pass = EventPasses.Active(c, now);
            return new StudioClientDto(
                c.Id, c.Title, c.EventStartAt, c.Status.ToString(),
                host?.Name, host?.Email, names.GetValueOrDefault(c.TemplateId),
                guestCounts.GetValueOrDefault(c.Id), going.GetValueOrDefault(c.Id),
                pass.ToString(), pass == EventPassKind.None ? null : c.EventPassUntil,
                c.CreatedByUserId == me,
                discounted.Contains(c.Id));
        }).ToList();
    }
}
