using InvitesBlog.Application.Guests;

namespace InvitesBlog.Application.Dtos.Guests;

// ── Requests ────────────────────────────────────────────────────────────────

/// <summary>
/// Manual guest add after payment (§4.7.4). <paramref name="SendNow"/> chooses whether a guest added
/// to an already-dispatched campaign is emailed immediately or just added for a later, explicit send
/// (the same free resend used for anyone else not-yet-sent) — null defaults to true, the original
/// always-send-if-possible behavior, so any other caller that doesn't know about the flag is unaffected.
/// </summary>
public sealed record AddGuestRequest(
    string? Email, string? Phone, string? Name, string? Role, string? Gender, string? DefaultCountry,
    bool? SendNow = null);

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
public sealed record AddGuestResultDto(
    int Added, int GuestCount, int PaidCapacity, bool NeedsTopUp,
    /// <summary>True only once the controller has actually attempted (and the provider accepted)
    /// an immediate send — see <see cref="AddGuestOutcome"/>. False means the guest was added but
    /// not sent, whether because SendNow was false, capacity/status didn't allow it, or the send
    /// itself failed.</summary>
    bool Sent = false);

/// <summary>Result of a free resend.</summary>
public sealed record ResendResultDto(bool Sent);

/// <summary>
/// Service-to-controller outcome for a manual add. Carries the response payload plus, when the
/// campaign is already dispatched and the new guest fits within paid capacity, the id of the guest
/// the controller should dispatch immediately (§4.7.4). Not serialized — the controller returns
/// only <see cref="Response"/>.
/// </summary>
public sealed record AddGuestOutcome(AddGuestResultDto Response, Guid? DispatchGuestId);
