using System.Net;
using InvitesBlog.Application.Exceptions;

namespace InvitesBlog.Application.Services.Billing;

/// <summary>
/// Everything sold through the App Store. The same ids and the same items as Google Play (Apple allows
/// letters, digits, underscores and periods, and ids are permanent once created in App Store Connect).
/// The app has the same table. Event-bound items are consumables, bought again for each event;
/// premium_monthly is an auto-renewable subscription. See the app's docs/APPLE-PURCHASES.md.
/// </summary>
public static class AppStoreCatalog
{
    public static IReadOnlyList<PlayProduct> All => GooglePlayCatalog.All;
    public static PlayProduct? Find(string? id) => GooglePlayCatalog.Find(id);
}

/// <summary>
/// One transaction as Apple signed it (<c>JWSTransactionDecodedPayload</c>), after its signature and
/// certificate chain were checked. Only fields we use.
/// </summary>
/// <param name="AppAccountToken">The UUID the app set on the purchase: our purchase intent's id.</param>
/// <param name="Environment">"Production" or "Sandbox". ("Xcode" never verifies: it isn't signed by Apple.)</param>
/// <param name="Price">What the buyer paid, in the storefront currency, if Apple says.</param>
/// <param name="RevocationDate">Set when Apple refunded or revoked it.</param>
public sealed record AppStoreTransaction(
    string TransactionId,
    string OriginalTransactionId,
    string ProductId,
    string BundleId,
    Guid? AppAccountToken,
    string Environment,
    string? Type,
    DateTimeOffset? PurchaseDate,
    DateTimeOffset? ExpiresDate,
    DateTimeOffset? RevocationDate,
    decimal? Price,
    string? Currency)
{
    public bool IsTest => Environment != "Production";
}

/// <summary>A subscription's renewal state (<c>JWSRenewalInfoDecodedPayload</c>), verified.</summary>
public sealed record AppStoreRenewalInfo(bool AutoRenew, DateTimeOffset? GracePeriodExpiresDate);

/// <summary>
/// One subscription's status from Get All Subscription Statuses: 1 active, 2 expired, 3 billing retry,
/// 4 billing grace period, 5 revoked.
/// </summary>
public sealed record AppStoreSubscriptionStatus(int Status, AppStoreTransaction Latest, AppStoreRenewalInfo? Renewal);

/// <summary>A verified App Store Server Notification V2. Its transaction is only a hint of what to ask about.</summary>
public sealed record AppStoreNotification(
    string NotificationType, string? Subtype, string? BundleId, string? Environment, long? AppAppleId,
    AppStoreTransaction? Transaction);

/// <summary>
/// The App Store Server API. Null from a read means Apple doesn't know that transaction for this app.
/// <see cref="AppStoreUnavailableException"/> means it couldn't be asked, which decides nothing.
/// </summary>
public interface IAppStoreServerApi
{
    bool IsConfigured { get; }
    Task<AppStoreTransaction?> GetTransactionAsync(string transactionId, bool sandbox, CancellationToken ct);
    /// <summary>The status of the subscription with this original transaction id; null if Apple doesn't know it.</summary>
    Task<AppStoreSubscriptionStatus?> GetSubscriptionStatusAsync(string originalTransactionId, bool sandbox, CancellationToken ct);
    /// <summary>Notifications Apple sent (or tried to) in a window, signed as they were sent.</summary>
    Task<IReadOnlyList<string>> NotificationHistoryAsync(DateTimeOffset from, DateTimeOffset to, bool sandbox, CancellationToken ct);
}

/// <summary>Checks Apple's signature and certificate chain on a JWS, and reads it.</summary>
public interface IAppStoreSignedData
{
    /// <summary>Throws <see cref="AppStoreRefusedException"/> unless Apple signed it.</summary>
    AppStoreTransaction Transaction(string jws);
    AppStoreRenewalInfo RenewalInfo(string jws);
    AppStoreNotification Notification(string jws);
}

/// <summary>The App Store can't be used right now: not set up here, or not answering. Nothing was decided (503).</summary>
public sealed class AppStoreUnavailableException(string message, string errorCode = "app_store_unavailable")
    : AppException(message, HttpStatusCode.ServiceUnavailable, errorCode, [new Common.ApiError(message, null, errorCode)]);

/// <summary>A refusal a retry won't change: nothing was applied.</summary>
public sealed class AppStoreRefusedException(string message, string errorCode, HttpStatusCode status = HttpStatusCode.Conflict)
    : AppException(message, status, errorCode, [new Common.ApiError(message, null, errorCode)]);

public sealed record AppStoreIntentRequest(string? ProductId, Guid? EventId);

/// <summary>Hands in what StoreKit returned: the signed transaction (<c>Transaction.jwsRepresentation</c>).</summary>
public sealed record AppStoreTransactionRequest(string? SignedTransaction, Guid? EventId);

/// <param name="Available">Purchases can be checked here. Off means nothing should be offered.</param>
/// <param name="Products">Product ids this account may buy now. The app shows those the App Store also returns.</param>
public sealed record AppStoreOffersDto(bool Available, IReadOnlyList<string> Products);

/// <param name="AppAccountToken">Goes into the purchase (<c>appAccountToken</c>). Says who and, for an event item, which event.</param>
public sealed record AppStoreIntentDto(Guid AppAccountToken, string ProductId, Guid? EventId);

/// <param name="State">"applied": it is on, the app may finish the transaction.</param>
public sealed record AppStoreResultDto(string State, string ProductId, Guid? EventId, bool Test);

public interface IAppStorePurchases
{
    Task<AppStoreOffersDto> OffersAsync(Guid? eventId, CancellationToken ct = default);
    /// <summary>Before buying: what this purchase will be for. Refused unless it's on offer now.</summary>
    Task<AppStoreIntentDto> IntentAsync(AppStoreIntentRequest request, CancellationToken ct = default);
    /// <summary>Checks a transaction with Apple and applies it, once. Safe to repeat and to race.</summary>
    Task<AppStoreResultDto> RedeemAsync(AppStoreTransactionRequest request, CancellationToken ct = default);
    /// <summary>An App Store Server Notification V2 (<c>signedPayload</c>). Only says what to ask Apple about.</summary>
    Task HandleNotificationAsync(string? signedPayload, CancellationToken ct = default);
    /// <summary>What still works if no notification ever arrives.</summary>
    Task SweepAsync(DateTimeOffset now, CancellationToken ct = default);
}
