using System.Net;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Domain.Enums;

namespace InvitesBlog.Application.Services.Billing;

/// <summary>One thing sold through Google Play: its id there, and the billing item it is.</summary>
public sealed record PlayProduct(string Id, PaymentKind Kind, bool IsSubscription);

/// <summary>
/// Everything sold through Google Play. The ids are permanent once created in Play Console; the same
/// table is in the app (<c>play_products.dart</c>). The App Store sells the same list (<see cref="AppStoreCatalog"/>),
/// venue_monthly (Studio) included since 2026-11-12. Not here: the retired passes held for clients (from when
/// Premium was called Studio). See the app's docs/STORE-RULES.md.
/// </summary>
public static class GooglePlayCatalog
{
    public static readonly IReadOnlyList<PlayProduct> All =
    [
        new("gold_pass", PaymentKind.GoldPass, false),
        new("platinum_pass", PaymentKind.PlatinumPass, false),
        new("gold_pass_extend", PaymentKind.GoldExtension, false),
        new("platinum_pass_extend", PaymentKind.PlatinumExtension, false),
        new("keep_photos_year", PaymentKind.KeepPhotos, false),
        new("extra_invites_100", PaymentKind.Sending, false),
        new("premium_monthly", PaymentKind.PremiumMonthly, true),
        new("venue_monthly", PaymentKind.VenueMonthly, true),
    ];

    public static PlayProduct? Find(string? id) => All.FirstOrDefault(p => p.Id == id);
}

/// <summary>What Google says about a one-time purchase (<c>purchases.products.get</c>).</summary>
/// <param name="PurchaseState">0 purchased, 1 cancelled, 2 pending.</param>
/// <param name="ConsumptionState">0 not yet, 1 consumed.</param>
/// <param name="PurchaseType">Null for an ordinary purchase; 0 test, 1 promo, 2 rewarded.</param>
public sealed record PlayProductPurchase(
    int PurchaseState, int ConsumptionState, int AcknowledgementState, int? PurchaseType,
    string? OrderId, string? ProductId, string? ObfuscatedAccountId, string? ObfuscatedProfileId);

/// <summary>One product in a subscription (<c>lineItems[]</c>).</summary>
public sealed record PlaySubscriptionLine(string ProductId, DateTimeOffset? ExpiryTime, string? LatestSuccessfulOrderId, bool AutoRenewEnabled);

/// <summary>What Google says about a subscription (<c>purchases.subscriptionsv2.get</c>).</summary>
public sealed record PlaySubscriptionPurchase(
    string State, bool Acknowledged, bool IsTest, string? LinkedPurchaseToken,
    string? ObfuscatedAccountId, IReadOnlyList<PlaySubscriptionLine> Lines);

/// <summary>What an order came to, in the buyer's currency, as Google reports it (<c>orders.get</c>).</summary>
public sealed record PlayOrderTotal(decimal Amount, string Currency);

/// <summary>One refunded or revoked purchase (<c>purchases.voidedpurchases.list</c>).</summary>
public sealed record PlayVoidedPurchase(string PurchaseToken, string? OrderId);

/// <summary>
/// The Android Publisher API. Null from a read means Google doesn't know that token for this package
/// and product. <see cref="GooglePlayUnavailableException"/> means it couldn't be asked: not an answer,
/// so whatever depended on it is left to be tried again.
/// </summary>
public interface IGooglePlayPublisher
{
    bool IsConfigured { get; }
    Task<PlayProductPurchase?> GetProductAsync(string productId, string token, CancellationToken ct);
    Task<PlaySubscriptionPurchase?> GetSubscriptionAsync(string token, CancellationToken ct);
    /// <summary>False when Google refused it, which it does for one already consumed.</summary>
    Task<bool> ConsumeAsync(string productId, string token, CancellationToken ct);
    /// <summary>False when Google refused it, which it does for one already acknowledged.</summary>
    Task<bool> AcknowledgeSubscriptionAsync(string productId, string token, CancellationToken ct);
    /// <summary>Stops a subscription renewing. It runs to the end of what's paid.</summary>
    Task<bool> CancelSubscriptionAsync(string token, CancellationToken ct);
    /// <summary>Null when the order can't be read (no financial permission, a test order).</summary>
    Task<PlayOrderTotal?> GetOrderTotalAsync(string orderId, CancellationToken ct);
    /// <summary>Everything Google has recorded as voided in its last 30 days.</summary>
    Task<IReadOnlyList<PlayVoidedPurchase>> ListVoidedAsync(CancellationToken ct);
}

/// <summary>Google Play can't be used right now: not set up here, or not answering. Nothing was decided (503).</summary>
public sealed class GooglePlayUnavailableException(string message, string errorCode = "google_play_unavailable")
    : AppException(message, HttpStatusCode.ServiceUnavailable, errorCode, [new InvitesBlog.Application.Common.ApiError(message, null, errorCode)]);

public sealed record GooglePlayPurchaseRequest(string? ProductId, string? PurchaseToken, Guid? EventId);

/// <summary>What's on offer through Play for this account, or for one of its events.</summary>
/// <param name="Available">Purchases can be checked here. Off means nothing should be offered.</param>
/// <param name="Products">Product ids this account may buy now. The app shows those Play also returns.</param>
/// <param name="AccountId">Goes into the purchase as its obfuscated account id. Holds nothing personal.</param>
/// <param name="ProfileId">Goes into the purchase as its obfuscated profile id: the event.</param>
public sealed record GooglePlayOffersDto(bool Available, IReadOnlyList<string> Products, string? AccountId, string? ProfileId);

/// <param name="State">"applied": it is on, keep nothing back. "pending": Google is still waiting for the money.</param>
public sealed record GooglePlayResultDto(string State, string ProductId, Guid? EventId, bool Test);

public interface IGooglePlayPurchases
{
    Task<GooglePlayOffersDto> OffersAsync(Guid? eventId, CancellationToken ct = default);
    /// <summary>Checks a purchase with Google and applies it, once. Safe to repeat and to race.</summary>
    Task<GooglePlayResultDto> RedeemAsync(GooglePlayPurchaseRequest request, CancellationToken ct = default);
    /// <summary>A Real-time Developer Notification. Only says what to ask Google about.</summary>
    Task HandleNotificationAsync(string rawBody, CancellationToken ct = default);
    /// <summary>The retries and re-checks that don't depend on a notification arriving.</summary>
    Task SweepAsync(DateTimeOffset now, bool full, CancellationToken ct = default);
}
