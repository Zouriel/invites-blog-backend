using InvitesBlog.Application.Dtos.Designers;

namespace InvitesBlog.Application.Services.Designers;

/// <summary>
/// The templates the signed-in person published ("My designs"). Everyone sees only their own; an admin
/// finds the platform's whole catalogue on the admin screen (System templates).
/// </summary>
public interface IMyTemplatesService
{
    Task<MyTemplatesPageDto> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// Removes a template. One already used by campaigns — or made for one customer (Dedicated with
    /// an AssignedEmail) — is UNLISTED rather than deleted, so every invitation built from it keeps
    /// rendering; anything else is deleted outright. An admin may remove any template, and the admin
    /// screen's delete (DELETE /api/admin/templates/{id}) calls this too, so the rule lives once.
    /// </summary>
    Task<DeleteTemplateResultDto> DeleteAsync(Guid templateId, CancellationToken ct = default);
}
