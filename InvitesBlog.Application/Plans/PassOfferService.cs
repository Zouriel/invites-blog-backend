using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Plans;

/// <summary>
/// What an event's passes cost its host, and why. The one discount is automatic: a design a Studio
/// account published FOR this client gets the Studio discount on a Party or Wedding pass — no code
/// to enter, and the price says who it's from. Only when the event's host is the person it was made
/// for, and only on their FIRST event from it: used again, or by anyone else, it's full price. Extensions and everything else are full price.
/// </summary>
/// <param name="DiscountPercent">0 without a Studio design.</param>
/// <param name="DesignedBy">The Studio designer the discount comes from, for "30% off: designed for you by …".</param>
public sealed record PassOfferDto(
    decimal PartyPass, decimal WeddingPass, decimal FullPartyPass, decimal FullWeddingPass,
    int DiscountPercent, string? DesignedBy, decimal PartyExtension, decimal WeddingExtension)
{
    public decimal PassPrice(EventPassKind kind) => kind == EventPassKind.Wedding ? WeddingPass : PartyPass;
    public decimal ExtensionPrice(EventPassKind kind) => kind == EventPassKind.Wedding ? WeddingExtension : PartyExtension;
}

public interface IPassOfferService
{
    Task<PassOfferDto> ForCampaignAsync(Guid campaignId, CancellationToken ct = default);
}

public sealed class PassOfferService(
    ICampaignRepository campaigns,
    ITemplateRepository templates,
    Abstractions.Persistence.IRepository<Domain.Entities.AppUser> users,
    IPlanService plans,
    IPriceBook priceBook) : IPassOfferService
{
    public async Task<PassOfferDto> ForCampaignAsync(Guid campaignId, CancellationToken ct = default)
    {
        var p = await priceBook.CurrentAsync(ct);
        var campaign = await campaigns.GetByIdAsync(campaignId, ct);
        var template = campaign is null ? null : await templates.GetByIdAsync(campaign.TemplateId, ct);

        string? designer = null;
        if (campaign is not null
            && template is { AssignedEmail: { Length: > 0 } assigned, DesignerUserId: { } designerId }
            && await IsForTheHostAsync(campaign, assigned, ct)
            && await IsFirstUseAsync(campaign, ct)
            && await plans.IsStudioAsync(designerId, ct))
            designer = string.IsNullOrWhiteSpace(template.DesignerName) ? "your designer" : template.DesignerName;

        return designer is null
            ? new PassOfferDto(p.PartyPass, p.WeddingPass, p.PartyPass, p.WeddingPass, 0, null, p.PartyExtension, p.WeddingExtension)
            : new PassOfferDto(p.StudioPassPrice(EventPassKind.Party), p.StudioPassPrice(EventPassKind.Wedding),
                p.PartyPass, p.WeddingPass, p.StudioDiscountPercent, designer, p.PartyExtension, p.WeddingExtension);
    }

    /// <summary>The event's host is the person the design was made for (their account's email).</summary>
    private async Task<bool> IsForTheHostAsync(Domain.Entities.Campaign campaign, string assignedEmail, CancellationToken ct)
    {
        if (campaign.CreatedByUserId is not { } hostId) return false;
        var email = await users.Query().Where(u => u.Id == hostId).Select(u => u.Email).FirstOrDefaultAsync(ct);
        return !string.IsNullOrWhiteSpace(email)
               && string.Equals(email.Trim(), assignedEmail.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The host's first event on this design (still standing). Counted per host: a designer trying the
    /// design out, or an admin, doesn't use up the client's discount — and anyone else is already
    /// ruled out by <see cref="IsForTheHostAsync"/>.
    /// </summary>
    private async Task<bool> IsFirstUseAsync(Domain.Entities.Campaign campaign, CancellationToken ct) =>
        !await campaigns.Query().AnyAsync(c => c.TemplateId == campaign.TemplateId && c.Id != campaign.Id
                                               && c.CreatedByUserId == campaign.CreatedByUserId
                                               && c.Status != CampaignStatus.Cancelled
                                               && c.CreatedAt < campaign.CreatedAt, ct);
}
