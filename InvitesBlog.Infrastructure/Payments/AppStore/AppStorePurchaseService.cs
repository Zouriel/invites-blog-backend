using System.Net;
using System.Text.Json;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Plans;
using InvitesBlog.Application.Pricing;
using InvitesBlog.Application.Services.Billing;
using InvitesBlog.Application.Services.Campaigns;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using InvitesBlog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Payments.AppStore;

/// <summary>
/// App Store purchases, the same way as Google Play's: only what Apple signed counts, and what it bought
/// is applied by <see cref="IBillingService.FulfilAsync"/>, exactly as a card payment is.
///
/// <para><b>Whose and for what.</b> StoreKit carries one id of ours, <c>appAccountToken</c>. Before a
/// purchase the app asks for an intent (account, event, product) and puts its id there; Apple signs it into
/// the transaction. A transaction can't be redeemed by another account, moved to another event or swapped
/// for another product.</para>
///
/// <para><b>Signed, then asked.</b> What the app hands in is a JWS checked against Apple's root; then
/// Apple's server is asked for the same transaction, so a refund since is seen and the server's answer, not
/// the app's copy, decides.</para>
///
/// <para><b>Once.</b> One row per original transaction id (unique index); applying takes that row's lock
/// and records and applies the payment in one transaction. The app finishes the StoreKit transaction only
/// after we answer "applied"; until then StoreKit hands it to the app again on every start.</para>
/// </summary>
public sealed class AppStorePurchaseService(
    AppDbContext db,
    ICurrentUser currentUser,
    IAppStoreServerApi api,
    IAppStoreSignedData signed,
    IBillingService billing,
    ICampaignOwnershipService ownership,
    IPassOfferService offers,
    IConfiguration config,
    ILogger<AppStorePurchaseService> logger) : IAppStorePurchases
{
    private const string Applied = "applied";
    private const int MaxJws = 64 * 1024;

    /// <summary>Apple's statuses in which a subscriber still has what they paid for: active, billing grace period.</summary>
    private static readonly HashSet<int> Entitled = [1, 4];

    private bool AllowTest => bool.TryParse(config["AppStore:AllowTestPurchases"], out var on) && on;

    /// <summary>
    /// Accounts whose sandbox purchases count even with test purchases off: App Review's demo account and our
    /// own testers. App Review buys in the sandbox against the production server, so without this a reviewer's
    /// purchase would be refused. Everyone else's sandbox purchases still are.
    /// </summary>
    private HashSet<Guid> TestAccounts =>
        [.. (config["AppStore:TestAccountIds"] ?? "").Split([',', ' ', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(v => Guid.TryParse(v, out var id) ? id : Guid.Empty).Where(id => id != Guid.Empty)];

    private bool MayTest(Guid? account) => AllowTest || (account is { } a && TestAccounts.Contains(a));
    private bool AnyTesting => AllowTest || TestAccounts.Count > 0;

    // ----- What may be bought --------------------------------------------------------------------

    /// <summary>The same rules as Google Play's offers (a test checks they agree).</summary>
    public async Task<AppStoreOffersDto> OffersAsync(Guid? eventId, CancellationToken ct = default)
    {
        var me = currentUser.UserId ?? throw new ForbiddenException("Sign in to see this.");
        if (!api.IsConfigured) return new AppStoreOffersDto(false, []);
        var now = DateTimeOffset.UtcNow;

        if (eventId is not { } id)
        {
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == me, ct);
            if (user is null) return new AppStoreOffersDto(true, []);
            var hasPlan = PlanRules.IsActive(user.SubscriptionTier, user.SubscriptionEndsAt, now)
                          && user.SubscriptionTier != SubscriptionTier.None;
            // No plan: Premium or Studio. A plan held through an App Store subscription: the other one in the
            // group (Apple moves the same subscription up to Studio at once, or down to Premium at renewal).
            // A plan from anywhere else: nothing here (the website or Play renews it).
            if (!hasPlan) return new AppStoreOffersDto(true, [.. AppStoreCatalog.Subscriptions]);
            var current = AppStoreCatalog.SubscriptionFor(user.SubscriptionTier);
            var throughApple = current is not null && await db.AppStorePurchases.AsNoTracking().AnyAsync(p =>
                p.UserId == me && p.CampaignId == null && p.FulfilledAt != null && p.VoidedAt == null
                && p.ExpiresAt > now && p.ProductId == current, ct);
            return new AppStoreOffersDto(true, throughApple ? AppStoreCatalog.Subscriptions.Where(p => p != current).ToList() : []);
        }

        var campaign = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        var access = campaign is null ? CampaignAccess.None : await ownership.AccessOfAsync(me, id, ct);
        if (campaign is null || campaign.Status == CampaignStatus.Cancelled || access < CampaignAccess.Celebrant)
            return new AppStoreOffersDto(true, []);
        return new AppStoreOffersDto(true, GooglePlay.GooglePlayPurchaseService.EventProducts(campaign, access, now)
            .Where(p => AppStoreCatalog.Find(p) is not null).ToList());
    }

    public async Task<AppStoreIntentDto> IntentAsync(AppStoreIntentRequest request, CancellationToken ct = default)
    {
        var me = currentUser.UserId ?? throw new ForbiddenException("Sign in to do this.");
        var product = AppStoreCatalog.Find(request.ProductId)
            ?? throw new AppStoreRefusedException("That isn't something we sell.", "app_store_wrong_product", HttpStatusCode.BadRequest);
        if (!api.IsConfigured)
            throw new AppStoreUnavailableException("App Store purchases aren't available right now.");
        Guid? eventId = product.IsSubscription ? null : request.EventId
            ?? throw new AppStoreRefusedException("Say which event this is for.", "app_store_wrong_event", HttpStatusCode.BadRequest);
        if (product.IsSubscription && request.EventId is not null)
            throw new AppStoreRefusedException("Premium and Studio are for the account, not an event.", "app_store_wrong_event", HttpStatusCode.BadRequest);

        var now = await OffersAsync(eventId, ct);
        if (!now.Products.Contains(product.Id))
            throw new AppStoreRefusedException("That isn't on offer for this right now.", "app_store_not_offered");

        var intent = new AppStoreIntent { Id = Guid.NewGuid(), UserId = me, CampaignId = eventId, ProductId = product.Id, CreatedAt = DateTimeOffset.UtcNow };
        db.AppStoreIntents.Add(intent);
        await db.SaveChangesAsync(ct);
        return new AppStoreIntentDto(intent.Id, product.Id, eventId);
    }

    // ----- Redeeming -----------------------------------------------------------------------------

    public async Task<AppStoreResultDto> RedeemAsync(AppStoreTransactionRequest request, CancellationToken ct = default)
    {
        var me = currentUser.UserId ?? throw new ForbiddenException("Sign in to do this.");
        var jws = request.SignedTransaction;
        if (string.IsNullOrWhiteSpace(jws) || jws.Length > MaxJws)
            throw new AppStoreRefusedException("That purchase couldn't be read.", "app_store_bad_transaction", HttpStatusCode.BadRequest);
        if (!api.IsConfigured)
            throw new AppStoreUnavailableException("App Store purchases aren't available right now. Your purchase is safe, and we'll try again later.");

        var claimed = signed.Transaction(jws);
        if (claimed.Environment is not ("Production" or "Sandbox"))
            throw new AppStoreRefusedException("That purchase isn't from the App Store.", "app_store_test_refused");
        if (claimed.IsTest && !MayTest(me))
            throw new AppStoreRefusedException("Test purchases aren't accepted here.", "app_store_test_refused");

        // Past this point the buyer has paid and may be waiting: don't let a closed screen cut it short.
        return await ApplyFromAppleAsync(claimed, me, request.EventId, CancellationToken.None);
    }

    /// <summary>Asks Apple about a transaction we've only been told of, and applies what Apple says.</summary>
    private async Task<AppStoreResultDto> ApplyFromAppleAsync(AppStoreTransaction told, Guid? caller, Guid? requestedEvent, CancellationToken ct)
    {
        var product = AppStoreCatalog.Find(told.ProductId)
            ?? throw new AppStoreRefusedException("That isn't something we sell.", "app_store_wrong_product");
        if (product.IsSubscription)
            return await SyncSubscriptionAsync(told.OriginalTransactionId, told.IsTest, caller, ct);

        var t = await api.GetTransactionAsync(told.TransactionId, told.IsTest, ct)
            ?? throw new AppStoreRefusedException("The App Store doesn't know this purchase.", "app_store_not_found", HttpStatusCode.NotFound);
        if (t.TransactionId != told.TransactionId || t.ProductId != told.ProductId)
            throw new AppStoreRefusedException("That purchase is for something else.", "app_store_wrong_product");
        return await RedeemOneTimeAsync(product, t, caller, requestedEvent, ct);
    }

    private async Task<AppStoreResultDto> RedeemOneTimeAsync(PlayProduct product, AppStoreTransaction t, Guid? caller, Guid? requestedEvent, CancellationToken ct)
    {
        if (t.RevocationDate is not null)
        {
            await VoidOneTimeAsync(t.OriginalTransactionId, ct);
            throw new AppStoreRefusedException("This purchase was refunded.", "app_store_voided");
        }
        var intent = t.AppAccountToken is { } token
            ? await db.AppStoreIntents.AsNoTracking().FirstOrDefaultAsync(i => i.Id == token, ct) : null;
        var owner = intent?.UserId
            ?? throw new AppStoreRefusedException("This purchase isn't tied to an account.", "app_store_wrong_account", HttpStatusCode.Forbidden);
        if (caller is { } c && c != owner)
            throw new AppStoreRefusedException("This purchase belongs to another account.", "app_store_wrong_account", HttpStatusCode.Forbidden);
        if (intent.ProductId != product.Id)
            throw new AppStoreRefusedException("That purchase is for something else.", "app_store_wrong_product");
        var eventId = intent.CampaignId
            ?? throw new AppStoreRefusedException("This purchase isn't tied to an event.", "app_store_wrong_event", HttpStatusCode.Forbidden);
        if (requestedEvent is { } asked && asked != eventId)
            throw new AppStoreRefusedException("This purchase is for a different event.", "app_store_wrong_event", HttpStatusCode.Forbidden);
        if (t.IsTest && !MayTest(owner))
            throw new AppStoreRefusedException("Test purchases aren't accepted here.", "app_store_test_refused");

        var now = DateTimeOffset.UtcNow;
        AppStorePurchase row;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await InsertIfNewAsync(t.OriginalTransactionId, product.Id, owner, eventId, intent.Id, t.IsTest, now, ct);
            row = await LockAsync(t.OriginalTransactionId, ct) ?? throw new InvalidOperationException("The row just made has gone.");
            if (row.VoidedAt is not null)
                throw new AppStoreRefusedException("This purchase was refunded.", "app_store_voided");
            if (row.UserId != owner)
                throw new AppStoreRefusedException("This purchase belongs to another account.", "app_store_wrong_account", HttpStatusCode.Forbidden);
            if (row.CampaignId != eventId || row.ProductId != product.Id)
                throw new AppStoreRefusedException("This purchase is for a different event.", "app_store_wrong_event", HttpStatusCode.Forbidden);

            if (row.FulfilledAt is null)
            {
                var campaign = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == eventId, ct)
                    ?? throw new AppStoreRefusedException("That event no longer exists.", "app_store_event_gone");
                if (!BillingService.MayBuy(product.Kind, await ownership.AccessOfAsync(owner, eventId, ct)))
                    throw new AppStoreRefusedException("Only the event's host, or the people it is for, can use this purchase.", "app_store_not_host", HttpStatusCode.Forbidden);
                BillingService.EnsureEventCanHave(product.Kind, campaign, now);

                var payment = NewPayment(product, owner, eventId, t, now, BillingService.Describe(product.Kind, 1, campaign), null);
                await ApplyPaymentAsync(payment, ct);
                row.PaymentId = payment.Id;
                row.TransactionId = t.TransactionId;
                row.FulfilledAt = now;
                row.LastCheckedAt = now;
                await db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
        }
        return new AppStoreResultDto(Applied, product.Id, eventId, row.IsTest);
    }

    private static Payment NewPayment(PlayProduct product, Guid? owner, Guid? eventId, AppStoreTransaction t, DateTimeOffset now, string description, DateTimeOffset? periodEnds)
    {
        // numeric(10,2). A price that doesn't fit, or none, is recorded as unknown, never as MVR.
        var known = t.Price is >= 0 and < 100_000_000m && t.Currency is { Length: 3 };
        return new Payment
        {
            Id = Guid.NewGuid(),
            CampaignId = eventId,
            UserId = owner,
            Kind = product.Kind,
            Quantity = 1,
            InviteCount = product.Kind == PaymentKind.Sending ? PricingCalculator.BlockSize : 0,
            Amount = known ? Math.Round(t.Price!.Value, 2) : 0,
            Currency = known ? t.Currency! : PaymentProviders.UnknownCurrency,
            Description = t.IsTest ? $"{description} · test" : description,
            Status = PaymentStatus.Paid,
            Provider = PaymentProviders.AppStore,
            ProviderPaymentId = t.TransactionId,
            CreatedAt = now,
            PaidAt = now,
            IsTest = t.IsTest,
            PeriodEndsAt = periodEnds,
        };
    }

    private async Task ApplyPaymentAsync(Payment payment, CancellationToken ct)
    {
        db.Payments.Add(payment);
        await db.SaveChangesAsync(ct);
        await billing.FulfilAsync(payment.Id, ct);
    }

    private Task InsertIfNewAsync(string original, string productId, Guid? owner, Guid? eventId, Guid? intentId, bool isTest, DateTimeOffset now, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO app_store_purchases (id, original_transaction_id, product_id, user_id, campaign_id, intent_id, is_test, created_at)
            VALUES ({Guid.NewGuid()}, {original}, {productId}, {owner}, {eventId}, {intentId}, {isTest}, {now})
            ON CONFLICT (original_transaction_id) DO NOTHING
            """, ct);

    private async Task<AppStorePurchase?> LockAsync(string original, CancellationToken ct) =>
        (await db.AppStorePurchases.FromSqlInterpolated($"SELECT * FROM app_store_purchases WHERE original_transaction_id = {original} FOR UPDATE").ToListAsync(ct))
        .FirstOrDefault();

    // ----- Subscription --------------------------------------------------------------------------

    /// <summary>
    /// Brings our record of a subscription into line with Apple's status: a new transaction is a new paid
    /// period, and the end date always follows Apple. <paramref name="caller"/> is null for work done with
    /// nobody signed in.
    /// </summary>
    private async Task<AppStoreResultDto> SyncSubscriptionAsync(string original, bool sandbox, Guid? caller, CancellationToken ct)
    {
        var status = await api.GetSubscriptionStatusAsync(original, sandbox, ct)
            ?? throw new AppStoreRefusedException("The App Store doesn't know this purchase.", "app_store_not_found", HttpStatusCode.NotFound);
        var t = status.Latest;
        var product = AppStoreCatalog.Find(t.ProductId) is { IsSubscription: true } p ? p
            : throw new AppStoreRefusedException("That purchase is for something else.", "app_store_wrong_product");

        var known = await db.AppStorePurchases.AsNoTracking().FirstOrDefaultAsync(x => x.OriginalTransactionId == original, ct);
        if (known is not null && known.UserId is null)
            return new AppStoreResultDto(Applied, product.Id, null, known.IsTest);   // its account was deleted

        Guid? owner = known?.UserId;
        Guid? intentId = known?.IntentId;
        if (owner is null)
        {
            var intent = t.AppAccountToken is { } token
                ? await db.AppStoreIntents.AsNoTracking().FirstOrDefaultAsync(i => i.Id == token, ct) : null;
            // Within the group the product may have moved since the intent (an upgrade or a downgrade at renewal).
            if (intent is not null && intent.ProductId != product.Id
                && !(AppStoreCatalog.Subscriptions.Contains(intent.ProductId) && AppStoreCatalog.Subscriptions.Contains(product.Id)))
                throw new AppStoreRefusedException("That purchase is for something else.", "app_store_wrong_product");
            owner = intent?.UserId;
            intentId = intent?.Id;
        }
        if (owner is not { } account)
            throw new AppStoreRefusedException("This purchase isn't tied to an account.", "app_store_wrong_account", HttpStatusCode.Forbidden);
        if (caller is { } c && c != account)
            throw new AppStoreRefusedException("This purchase belongs to another account.", "app_store_wrong_account", HttpStatusCode.Forbidden);
        if (t.IsTest && !MayTest(account))
            throw new AppStoreRefusedException("Test purchases aren't accepted here.", "app_store_test_refused");

        var now = DateTimeOffset.UtcNow;
        var expiry = status.Status == 4 && status.Renewal?.GracePeriodExpiresDate is { } grace && grace > (t.ExpiresDate ?? grace)
            ? grace : t.ExpiresDate;
        var entitled = Entitled.Contains(status.Status) && t.RevocationDate is null && expiry > now;
        if (!entitled && known is null)
            throw new AppStoreRefusedException("This subscription has ended.", "app_store_not_purchased");

        var isNewPeriod = entitled && (known?.FulfilledAt is null || t.TransactionId != known.TransactionId);

        AppStorePurchase row;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await InsertIfNewAsync(original, product.Id, account, null, intentId, t.IsTest, now, ct);
            row = await LockAsync(original, ct) ?? throw new InvalidOperationException("The row just made has gone.");
            if (row.UserId != account)
                throw new AppStoreRefusedException("This purchase belongs to another account.", "app_store_wrong_account", HttpStatusCode.Forbidden);

            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == account, ct)
                ?? throw new AppStoreRefusedException("That account no longer exists.", "app_store_wrong_account", HttpStatusCode.Forbidden);

            // Moved within the group (Premium up to Studio, or Studio down to Premium at renewal): the same
            // subscription, now for the other product. Its new transaction is a new paid period below, which
            // sets the account to that tier and nothing else, so the account never holds both.
            var switched = row.ProductId != product.Id;
            if (switched)
            {
                logger.LogInformation("App Store subscription {PurchaseId} moved from {From} to {To}.", row.Id, row.ProductId, product.Id);
                row.ProductId = product.Id;
            }

            if (entitled)
            {
                var firstTime = row.FulfilledAt is null;
                if (firstTime) BillingService.EnsureAccountCanHave(product.Kind, user, now);
                if (isNewPeriod && (firstTime || t.TransactionId != row.TransactionId))
                {
                    var payment = NewPayment(product, account, null, t, now,
                        BillingService.Describe(product.Kind, 1, null) + (firstTime ? "" : " · renewed automatically"), expiry);
                    await ApplyPaymentAsync(payment, ct);
                    row.PaymentId = payment.Id;
                    row.TransactionId = t.TransactionId;
                    row.FulfilledAt ??= now;
                }
                else if (user.SubscriptionEndsAt is { } ends && ends == row.ExpiresAt && ends != expiry)
                {
                    user.SubscriptionEndsAt = expiry;   // moved by Apple (grace period, extension)
                }
            }
            else if (row.ExpiresAt is { } mine && user.SubscriptionEndsAt == mine)
            {
                // Expired, in billing retry, or revoked: what this subscription gave stops. A plan that came
                // from somewhere else has a different date and stays.
                user.SubscriptionEndsAt = expiry is { } e && e < now ? e : now;
                logger.LogInformation("App Store subscription {PurchaseId} no longer covers its account ({Status}).", row.Id, status.Status);
            }

            row.ExpiresAt = expiry ?? row.ExpiresAt;
            row.SubscriptionStatus = status.Status;
            row.AutoRenewing = entitled && status.Renewal?.AutoRenew == true;
            row.LastCheckedAt = now;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        return new AppStoreResultDto(Applied, product.Id, null, row.IsTest);
    }

    // ----- Refunded or revoked -------------------------------------------------------------------

    private async Task VoidOneTimeAsync(string original, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await LockAsync(original, ct);
        if (row is null || row.VoidedAt is not null) return;

        row.VoidedAt = now;
        if (row.FulfilledAt is not null && row.PaymentId is { } paymentId
            && await db.Payments.FirstOrDefaultAsync(p => p.Id == paymentId, ct) is { } payment)
        {
            payment.Status = PaymentStatus.Refunded;
            if (row.CampaignId is { } id && await db.Campaigns.FirstOrDefaultAsync(c => c.Id == id, ct) is { } campaign)
                await StoreRefunds.TakeBackAsync(db, payment, campaign, now, ct);
        }
        db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            Action = "billing.app_store.voided",
            Actor = "app_store",
            CampaignId = row.CampaignId,
            DataJson = JsonSerializer.Serialize(new { purchase = row.Id, payment = row.PaymentId, product = row.ProductId }),
            CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        logger.LogInformation("App Store purchase {PurchaseId} was refunded or revoked; what it bought is taken back.", row.Id);
    }

    // ----- Notifications -------------------------------------------------------------------------

    public async Task HandleNotificationAsync(string? signedPayload, CancellationToken ct = default)
    {
        if (!api.IsConfigured) throw new AppStoreUnavailableException("App Store notifications aren't set up here.");
        if (string.IsNullOrWhiteSpace(signedPayload) || signedPayload.Length > MaxJws)
            throw new AppStoreRefusedException("That notification can't be read.", "app_store_bad_notification", HttpStatusCode.BadRequest);
        // A forged or broken one is refused (non-2xx); Apple's own always verify.
        var n = signed.Notification(signedPayload);
        await ActOnAsync(n, ct);
    }

    private async Task ActOnAsync(AppStoreNotification n, CancellationToken ct)
    {
        if (n.NotificationType == "TEST")
        {
            logger.LogInformation("App Store test notification received.");
            return;
        }
        if (n.Transaction is not { } t) return;   // a summary, or nothing about a transaction
        if (n.Environment == "Sandbox" && !AnyTesting)
        {
            logger.LogInformation("Ignored a sandbox App Store notification.");
            return;
        }
        if (n.Environment == "Production" && long.TryParse(config["AppStore:AppAppleId"], out var appId) && n.AppAppleId != appId)
        {
            logger.LogWarning("Ignored an App Store notification for another app.");
            return;
        }
        var product = AppStoreCatalog.Find(t.ProductId);
        if (product is null) return;

        // Each only says what to ask Apple about; only what Apple answers counts.
        try
        {
            if (product.IsSubscription)
            {
                if (n.NotificationType is "REFUND" or "REVOKE")
                {
                    // One period refunded; Apple's status says whether the subscription itself still stands.
                    var fresh = await api.GetTransactionAsync(t.TransactionId, t.IsTest, CancellationToken.None);
                    if (fresh?.RevocationDate is not null)
                        await db.Payments.Where(p => p.Provider == PaymentProviders.AppStore && p.ProviderPaymentId == fresh.TransactionId && p.Status == PaymentStatus.Paid)
                            .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PaymentStatus.Refunded), CancellationToken.None);
                }
                await SyncSubscriptionAsync(t.OriginalTransactionId, t.IsTest, null, CancellationToken.None);
            }
            else
            {
                await ApplyFromAppleAsync(t, null, null, CancellationToken.None);
            }
        }
        catch (AppException ex) when (ex is not AppStoreUnavailableException)
        {
            // A settled "no" (refunded, test refused, event gone, no account). A retry would say the same.
            logger.LogInformation("App Store notification {Type} not applied: {Code}.", n.NotificationType, ex.ErrorCode);
        }
    }

    // ----- Sweep ---------------------------------------------------------------------------------

    public async Task SweepAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        if (!api.IsConfigured) return;

        // Without notifications this is what notices a renewal, a lapse or a revocation: near or past the
        // end of the period, and for a while after, in case billing retry recovers.
        var due = now.AddDays(1);
        var from = now.AddDays(-65);
        var checkedBefore = now.AddHours(-6);
        var subscriptions = await db.AppStorePurchases.AsNoTracking()
            .Where(p => p.CampaignId == null && p.UserId != null && p.VoidedAt == null && p.ExpiresAt != null
                        && p.ExpiresAt < due && p.ExpiresAt > from && (p.LastCheckedAt == null || p.LastCheckedAt < checkedBefore))
            .Select(p => new { p.OriginalTransactionId, p.IsTest }).Take(200).ToListAsync(ct);
        foreach (var s in subscriptions)
        {
            if (s.IsTest && !AnyTesting) continue;
            try { await SyncSubscriptionAsync(s.OriginalTransactionId, s.IsTest, null, ct); }
            catch (AppStoreUnavailableException)
            {
                // One environment not answering (production refuses until the app is on sale) mustn't stop
                // the other's re-checks; this one is tried again next sweep.
                logger.LogWarning("An App Store subscription re-check was skipped: Apple's {Environment} server isn't answering.", s.IsTest ? "sandbox" : "production");
            }
            catch (AppException ex)
            {
                logger.LogInformation("An App Store subscription re-check was refused: {Code}.", ex.ErrorCode);
            }
            db.ChangeTracker.Clear();
        }

        // Refunds, revocations and purchases whose app never came back, from Apple's own record of what it
        // notified, whether or not the notification reached us. Three days back; each is safe to repeat.
        foreach (var sandbox in AnyTesting ? new[] { false, true } : [false])
        {
            IReadOnlyList<string> history;
            try { history = await api.NotificationHistoryAsync(now.AddDays(-3), now, sandbox, ct); }
            catch (AppStoreUnavailableException)
            {
                // Each environment on its own: production answers 401 until the app is on sale, and that
                // mustn't hide the sandbox's refunds (TestFlight, App Review), nor the other way round.
                logger.LogWarning("App Store notification history skipped: Apple's {Environment} server isn't answering.", sandbox ? "sandbox" : "production");
                continue;
            }
            foreach (var payload in history)
            {
                try { await ActOnAsync(signed.Notification(payload), ct); }
                catch (AppStoreRefusedException ex)
                {
                    logger.LogWarning("A notification in Apple's history was refused: {Code}.", ex.ErrorCode);
                }
                db.ChangeTracker.Clear();
            }
        }
    }
}
