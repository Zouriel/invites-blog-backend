using InvitesBlog.Application.Dtos.Accounts;

namespace InvitesBlog.Application.Services.Accounts;

/// <summary>What deleting the signed-in account would remove, in the terms it is shown to them.</summary>
/// <param name="EventsDeleted">Events (with invitations or not) with nobody else, deleted with the account.</param>
/// <param name="EventsPassedOn">Events that carry on, passing to another of their celebrants.</param>
/// <param name="Albums">Albums on the events being deleted.</param>
/// <param name="Photos">Photos on the events being deleted. What they added to other people's events stays.</param>
/// <param name="Videos">Videos on the events being deleted.</param>
/// <param name="Paid">What is still running and would be lost, in words, one per line. Empty when nothing is.</param>
/// <param name="AutoRenews">Whether a plan is set to renew by itself; deleting stops it.</param>
/// <param name="Blocked">Why the account can't be deleted right now, or null.</param>
/// <param name="CodeChannel">"email" or "sms": where the code will go.</param>
/// <param name="CodeSentTo">That destination, masked.</param>
/// <param name="StoreNote">What happens to a subscription in the store, in words, or null when there is none.</param>
public sealed record AccountDeletionSummaryDto(
    int EventsDeleted,
    int EventsPassedOn,
    int Albums,
    int Photos,
    int Videos,
    IReadOnlyList<string> Paid,
    bool AutoRenews,
    string? Blocked,
    string CodeChannel,
    string CodeSentTo,
    string? StoreNote = null);

public sealed record AccountDeletedDto(bool Deleted);

/// <summary>Completes an event's handover to a celebrant who had no account when its owner left.</summary>
public interface IEventInheritance
{
    Task ClaimAsync(InvitesBlog.Domain.Entities.AppUser user, CancellationToken ct = default);
}

/// <summary>
/// Self-service account deletion: a code to the account's own contact, then immediate and final.
/// See <c>AccountDeletionService</c> for what happens to each kind of data.
/// </summary>
public interface IAccountDeletionService
{
    Task<AccountDeletionSummaryDto> SummaryAsync(CancellationToken ct = default);
    Task<CodeSentResponse> RequestCodeAsync(CancellationToken ct = default);
    Task<AccountDeletedDto> DeleteAsync(VerifyCodeRequest request, CancellationToken ct = default);
}
