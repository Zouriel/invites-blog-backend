using InvitesBlog.Application.Common;
using InvitesBlog.Application.Dtos.Templates;
using InvitesBlog.Application.Filters.Templates;

namespace InvitesBlog.Application.Services.Templates;

public interface ITemplateService
{
    Task<PagedResult<TemplateListItemDto>> ListAsync(TemplateFilter filter, CancellationToken ct = default);
    Task<TemplateDetailDto> GetBySlugAsync(string slug, CancellationToken ct = default);

    /// <summary>Active dedicated templates reserved for this (OTP-verified) email — the "did you
    /// request a template?" flow. Empty when nothing is ready yet.</summary>
    Task<IReadOnlyList<TemplateListItemDto>> GetDedicatedForAsync(string email, CancellationToken ct = default);

    /// <summary>Templates the signed-in caller published themselves, whatever their visibility.
    ///
    /// <para>The gallery only lists Public ones, so without this a design published Private was
    /// unreachable from event creation and its author had to make it public to use their own work —
    /// even though attaching it was always allowed (see CampaignService.CanUsePrivate).</para></summary>
    Task<IReadOnlyList<TemplateListItemDto>> GetMineAsync(CancellationToken ct = default);
}
