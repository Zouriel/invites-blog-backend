using InvitesBlog.Application.Guests;

namespace InvitesBlog.Application.Dtos.Guests;

// ── Requests ────────────────────────────────────────────────────────────────

/// <summary>Manual guest add after payment (§4.7.4).</summary>
public sealed record AddGuestRequest(
    string? Email, string? Phone, string? Name, string? Role, string? Gender, string? DefaultCountry);

/// <summary>Fix a guest's contact details (§4.7.4).</summary>
public sealed record UpdateGuestRequest(
    string? Email, string? Phone, string? Name, string? Role, string? Gender, string? DefaultCountry);

/// <summary>Confirm a previously parsed upload, materializing its guests (§15.3).</summary>
public sealed record ConfirmUploadRequest(Guid UploadId);

// ── Responses ───────────────────────────────────────────────────────────────

/// <summary>Upload review summary (§4.4.6). Field names mirror the legacy JSON exactly.</summary>
public sealed record GuestUploadSummaryDto(
    Guid UploadId,
    int TotalRows,
    int ValidRows,
    int InvalidRows,
    int Duplicates,
    int MissingPhone,
    int MissingEmail,
    IReadOnlyDictionary<string, int> RoleDistribution,
    IReadOnlyDictionary<string, int> GenderDistribution,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<GuestUploadError> Errors,
    bool CanContinue);

/// <summary>Result of materializing an upload's guests.</summary>
public sealed record ConfirmUploadResultDto(int Added, int Suppressed);

/// <summary>Result of a manual guest add, including prepaid-capacity accounting (§4.7.4).</summary>
public sealed record AddGuestResultDto(int Added, int GuestCount, int PaidCapacity, bool NeedsTopUp);

/// <summary>Result of a free resend.</summary>
public sealed record ResendResultDto(bool Sent);

/// <summary>
/// Service-to-controller outcome for a manual add. Carries the response payload plus, when the
/// campaign is already dispatched and the new guest fits within paid capacity, the id of the guest
/// the controller should dispatch immediately (§4.7.4). Not serialized — the controller returns
/// only <see cref="Response"/>.
/// </summary>
public sealed record AddGuestOutcome(AddGuestResultDto Response, Guid? DispatchGuestId);
