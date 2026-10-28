using InvitesBlog.Application.Dtos.Guests;

namespace InvitesBlog.Application.Services.Guests;

/// <summary>§10.4 Guest upload + add/fix/resend business logic.</summary>
public interface IGuestService
{
    Task<GuestUploadSummaryDto> UploadAsync(
        Guid campaignId, Stream fileStream, string fileName, string defaultCountry, CancellationToken ct = default);

    Task<ConfirmUploadResultDto> ConfirmUploadAsync(Guid campaignId, ConfirmUploadRequest req, CancellationToken ct = default);

    Task<AddGuestOutcome> AddGuestAsync(Guid campaignId, AddGuestRequest req, CancellationToken ct = default);

    Task UpdateGuestAsync(Guid campaignId, Guid guestId, UpdateGuestRequest req, CancellationToken ct = default);

    /// <summary>
    /// Verifies ownership + guest existence and enforces the free-resend rule (max 3 per 24h, §4.7.4).
    /// Throws <see cref="Exceptions.Guests.ResendLimitExceededException"/> when exceeded. The actual
    /// send is performed by the caller via the dispatch service (Infrastructure).
    /// </summary>
    Task PrepareResendAsync(Guid campaignId, Guid guestId, CancellationToken ct = default);

    /// <summary>
    /// Takes somebody who added themselves from the open guest list off the list, with their invite
    /// and replies. Refuses guests the host added.
    /// </summary>
    Task RemoveSelfAddedAsync(Guid campaignId, Guid guestId, CancellationToken ct = default);
}
