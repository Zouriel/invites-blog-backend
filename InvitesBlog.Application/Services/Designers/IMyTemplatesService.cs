using InvitesBlog.Application.Dtos.Designers;

namespace InvitesBlog.Application.Services.Designers;

/// <summary>
/// The templates the signed-in person is responsible for. The SAME screen serves both audiences and
/// the role decides its scope: an admin sees every template on the platform ("System templates"), a
/// designer sees only their own ("My templates").
/// </summary>
public interface IMyTemplatesService
{
    Task<MyTemplatesPageDto> ListAsync(CancellationToken ct = default);

    /// <summary>
    /// Sets the per-use fee. It applies to campaigns started AFTERWARDS — anyone mid-campaign keeps
    /// the price they were quoted, which the per-campaign freeze already guarantees.
    /// </summary>
    Task<MyTemplateRowDto> SetPricingAsync(Guid templateId, SetTemplatePricingRequest request, CancellationToken ct = default);

    /// <summary>
    /// Removes a template. One already used by campaigns is UNLISTED rather than deleted, so every
    /// invitation built from it keeps rendering; an unused one is deleted outright.
    /// </summary>
    Task<DeleteTemplateResultDto> DeleteAsync(Guid templateId, CancellationToken ct = default);

    /// <summary>The stored source, so the edit screen can start from what's live rather than a blank file.</summary>
    Task<string> GetSourceAsync(Guid templateId, CancellationToken ct = default);
}
