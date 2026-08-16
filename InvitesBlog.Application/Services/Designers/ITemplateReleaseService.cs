using InvitesBlog.Application.Dtos.Designers;

namespace InvitesBlog.Application.Services.Designers;

/// <summary>
/// The two-party release of a commissioned template into the public gallery (§Phase 5). A dedicated
/// template only becomes public — and only starts charging its per-use fee to other inviters — once
/// BOTH the person who commissioned it and the designer who made it have said yes. Each party can
/// only ever set their own flag.
/// </summary>
public interface ITemplateReleaseService
{
    /// <summary>What a party sees before deciding: who has agreed so far, and what releasing means.</summary>
    Task<TemplateReleaseDto> GetAsync(Guid templateId, CancellationToken ct = default);

    /// <summary>Records the DESIGNER's consent — authorized by the signed-in designer who made it.</summary>
    Task<TemplateReleaseDto> ConsentAsDesignerAsync(Guid templateId, CancellationToken ct = default);

    /// <summary>
    /// Records the REQUESTER's consent. The requester has no account, so they're identified the same
    /// way they claim a dedicated template today: an OTP-verified email that matches the one the
    /// template is assigned to.
    /// </summary>
    Task<TemplateReleaseDto> ConsentAsRequesterAsync(Guid templateId, CancellationToken ct = default);

    /// <summary>Commissioned templates awaiting the signed-in requester's decision, for their dashboard.</summary>
    Task<IReadOnlyList<TemplateReleaseDto>> ListForRequesterAsync(CancellationToken ct = default);
}
