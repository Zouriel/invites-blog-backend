using System.Text.Json;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Plans;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Services.Studio;

/// <summary>A Studio account's page: the passes it holds, what they cost it, and its clients' events.</summary>
public sealed record StudioOverviewDto(
    int PartyCredits, int WeddingCredits, decimal PartyPassPrice, decimal WeddingPassPrice,
    IReadOnlyList<StudioClientDto> Clients);

/// <summary>
/// One client's event. A client is someone the designer published a template FOR, or an event the
/// planner organised themselves — never a stranger who used a public design.
/// </summary>
/// <param name="Pass">The pass in force now: None, Party or Wedding.</param>
/// <param name="Mine">Organised by this account, so its dashboard opens for them.</param>
public sealed record StudioClientDto(
    Guid CampaignId, string Title, DateTimeOffset EventStartAt, string Status,
    string? HostName, string? HostEmail, string? TemplateName,
    int GuestCount, int Going, string Pass, DateTimeOffset? PassUntil, bool Mine);

/// <summary>Giving one of the Studio's passes to a client's event.</summary>
public sealed record GivePassRequest(string Kind);

public interface IStudioService
{
    Task<StudioOverviewDto> OverviewAsync(CancellationToken ct = default);

    /// <summary>Uses one of the Studio's passes on a client's event.</summary>
    Task<StudioClientDto> GivePassAsync(Guid campaignId, GivePassRequest req, CancellationToken ct = default);
}

public sealed class StudioService(
    ICurrentUser currentUser,
    IPlanService plans,
    ICampaignRepository campaigns,
    ITemplateRepository templates,
    IInviterRepository inviters,
    IRepository<Guest> guests,
    IRepository<Invite> invites,
    IRepository<PassCredit> credits,
    IRepository<AuditLog> auditLogs,
    IUnitOfWork uow,
    MediaBuckets.IMediaBucketService buckets) : IStudioService
{
    public async Task<StudioOverviewDto> OverviewAsync(CancellationToken ct = default)
    {
        var me = await RequireStudioAsync(ct);
        var held = await credits.Query()
            .Where(c => c.OwnerUserId == me && c.UsedOnCampaignId == null)
            .GroupBy(c => c.Kind)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        return new StudioOverviewDto(
            held.GetValueOrDefault(EventPassKind.Party),
            held.GetValueOrDefault(EventPassKind.Wedding),
            PlanCatalog.StudioPassPrice(EventPassKind.Party),
            PlanCatalog.StudioPassPrice(EventPassKind.Wedding),
            await DescribeAsync(me, await ClientCampaigns(me).ToListAsync(ct), ct));
    }

    public async Task<StudioClientDto> GivePassAsync(Guid campaignId, GivePassRequest req, CancellationToken ct = default)
    {
        var me = await RequireStudioAsync(ct);
        if (!Enum.TryParse<EventPassKind>(req.Kind?.Trim(), ignoreCase: true, out var kind) || kind == EventPassKind.None)
            throw new BusinessRuleException("Choose a Party or Wedding pass.", "pass_kind_unknown");

        // Only a client's event: giving a pass is harmless, but the list is what proves who they are.
        var campaign = await ClientCampaigns(me).FirstOrDefaultAsync(c => c.Id == campaignId, ct)
                       ?? throw new NotFoundException("That isn't one of your clients' events.");
        var tracked = await campaigns.Query(tracking: true).FirstAsync(c => c.Id == campaign.Id, ct);
        var now = DateTimeOffset.UtcNow;

        if (EventPasses.Active(tracked, now) > kind)
            throw new BusinessRuleException("That event already has a Wedding pass.", "pass_already_bigger");

        var credit = await credits.Query(tracking: true)
            .Where(c => c.OwnerUserId == me && c.Kind == kind && c.UsedOnCampaignId == null)
            .OrderBy(c => c.CreatedAt)
            .FirstOrDefaultAsync(ct)
            ?? throw new BusinessRuleException(
                $"You have no {kind} passes left. Get more from your Studio page.", "no_pass_credits");

        EventPasses.Apply(tracked, kind, now);
        credit.UsedOnCampaignId = tracked.Id;
        credit.UsedAt = now;
        campaigns.Update(tracked);
        credits.Update(credit);

        await auditLogs.AddAsync(new AuditLog
        {
            Id = Guid.NewGuid(),
            Action = "studio.pass.give",
            Actor = me.ToString(),
            CampaignId = tracked.Id,
            DataJson = JsonSerializer.Serialize(new { kind = kind.ToString(), credit = credit.Id, until = tracked.EventPassUntil }),
            CreatedAt = now,
        }, ct);
        await uow.SaveChangesAsync(ct);
        await buckets.RaiseWindowsToPlanAsync(tracked.Id, ct);

        return (await DescribeAsync(me, [tracked], ct))[0];
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
                c.CreatedByUserId == me);
        }).ToList();
    }
}
