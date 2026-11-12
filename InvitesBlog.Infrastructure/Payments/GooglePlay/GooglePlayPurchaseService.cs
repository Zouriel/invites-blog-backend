using System.Net;
using System.Text;
using System.Text.Json;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Common;
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
using Npgsql;

namespace InvitesBlog.Infrastructure.Payments.GooglePlay;

/// <summary>A refusal of a store purchase that a retry won't change: nothing was applied, nothing consumed.</summary>
public sealed class GooglePlayRefusedException(string message, string errorCode, HttpStatusCode status = HttpStatusCode.Conflict)
    : AppException(message, status, errorCode, [new ApiError(message, null, errorCode)]);

/// <summary>
/// Google Play purchases: only Google's word for what was bought counts, and what it bought is applied
/// by <see cref="IBillingService.FulfilAsync"/>, exactly as a card payment is.
///
/// <para><b>Once.</b> One row per token (unique index). Applying takes that row's lock, then records the
/// payment and applies it in one transaction, so a retry or a race finds it done, and a crash leaves
/// nothing half way.</para>
///
/// <para><b>Whose.</b> The account and event go into the purchase when it starts (obfuscated ids) and
/// come back from Google. A token can't be redeemed by another account or moved to another event.</para>
///
/// <para><b>Consumed last.</b> Consuming or acknowledging happens after the commit. If anything fails
/// before it, Google still has the purchase open, the app sends it again, a notification
/// can finish it, and failing all that Google refunds it after three days. Nobody pays and gets nothing.</para>
/// </summary>
public sealed class GooglePlayPurchaseService(
    AppDbContext db,
    ICurrentUser currentUser,
    IGooglePlayPublisher publisher,
    IBillingService billing,
    ICampaignOwnershipService ownership,
    IPassOfferService offers,
    IConfiguration config,
    ILogger<GooglePlayPurchaseService> logger) : IGooglePlayPurchases
{
    private const string Pending = "pending";
    private const string Applied = "applied";

    /// <summary>States in which a subscriber still has what they paid for.</summary>
    private static readonly HashSet<string> Entitled = ["SUBSCRIPTION_STATE_ACTIVE", "SUBSCRIPTION_STATE_IN_GRACE_PERIOD", "SUBSCRIPTION_STATE_CANCELED"];

    private bool AllowTest => bool.TryParse(config["GooglePlay:AllowTestPurchases"], out var on) && on;

    // ----- What may be bought --------------------------------------------------------------------

    public async Task<GooglePlayOffersDto> OffersAsync(Guid? eventId, CancellationToken ct = default)
    {
        var me = currentUser.UserId ?? throw new ForbiddenException("Sign in to see this.");
        if (!publisher.IsConfigured) return new GooglePlayOffersDto(false, [], null, null);
        var now = DateTimeOffset.UtcNow;
        var account = Ids.Of(me);

        if (eventId is not { } id)
        {
            var user = await db.Users.AsNoTracking().FirstOrDefaultAsync(u => u.Id == me, ct);
            var hasPlan = user is not null && PlanRules.IsActive(user.SubscriptionTier, user.SubscriptionEndsAt, now)
                          && user.SubscriptionTier != SubscriptionTier.None;
            // Venue is sold here too (not on the iPhone): either plan, while the account has none.
            return new GooglePlayOffersDto(true, user is null || hasPlan ? [] : ["premium_monthly", "venue_monthly"], account, null);
        }

        var campaign = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct);
        var access = campaign is null ? CampaignAccess.None : await ownership.AccessOfAsync(me, id, ct);
        if (campaign is null || campaign.Status == CampaignStatus.Cancelled || access < CampaignAccess.Celebrant)
            return new GooglePlayOffersDto(true, [], account, Ids.Of(id));
        return new GooglePlayOffersDto(true, EventProducts(campaign, access, now), account, Ids.Of(id));
    }

    /// <summary>
    /// What a store sells for one event to someone with this access to it. Every buyer pays the same
    /// (there is no venue price any more), and the people an event is for may buy its passes too
    /// (<see cref="BillingService.MayBuy"/>). The App Store offers the same list.
    /// </summary>
    public static List<string> EventProducts(Campaign campaign, CampaignAccess access, DateTimeOffset now)
    {
        var list = new List<string>();
        var active = EventPasses.Active(campaign, now);
        if (active == EventPassKind.None) list.Add("gold_pass");
        if (active != EventPassKind.Platinum) list.Add("platinum_pass");
        if (campaign.EventPass == EventPassKind.Gold) list.Add("gold_pass_extend");
        if (campaign.EventPass == EventPassKind.Platinum) list.Add("platinum_pass_extend");
        // Free events are kept for good (owner, 2026-11-03): keeping is for an event that has had a pass.
        if (campaign.Kind != CampaignKind.SaveTheDate && campaign.EventPass != EventPassKind.None) list.Add("keep_photos_year");
        if (BillingService.MayBuy(PaymentKind.Sending, access)) list.Add("extra_invites_100");
        return list;
    }

    // ----- Redeeming -----------------------------------------------------------------------------

    public async Task<GooglePlayResultDto> RedeemAsync(GooglePlayPurchaseRequest request, CancellationToken ct = default)
    {
        var me = currentUser.UserId ?? throw new ForbiddenException("Sign in to do this.");
        var product = GooglePlayCatalog.Find(request.ProductId)
            ?? throw new GooglePlayRefusedException("That isn't something we sell.", "google_play_wrong_product", HttpStatusCode.BadRequest);
        var token = request.PurchaseToken;
        if (string.IsNullOrWhiteSpace(token) || token.Length > 2048)
            throw new GooglePlayRefusedException("That purchase couldn't be read.", "google_play_bad_token", HttpStatusCode.BadRequest);
        if (!publisher.IsConfigured)
            throw new GooglePlayUnavailableException("Google Play purchases aren't available right now. Your purchase is safe, and we'll try again later.");

        // Past this point the buyer has paid and may be waiting: don't let a closed screen cut it short.
        return product.IsSubscription
            ? await SyncSubscriptionAsync(token, me, product.Id, CancellationToken.None)
            : await RedeemOneTimeAsync(product, token, me, request.EventId, CancellationToken.None);
    }

    private async Task<GooglePlayResultDto> RedeemOneTimeAsync(PlayProduct product, string token, Guid? caller, Guid? requestedEvent, CancellationToken ct)
    {
        var g = await publisher.GetProductAsync(product.Id, token, ct)
            ?? throw new GooglePlayRefusedException("Google Play doesn't know this purchase.", "google_play_not_found", HttpStatusCode.NotFound);

        // Whatever the app claims, this is what was bought, by whom, for what.
        if (g.ProductId is { Length: > 0 } reported && reported != product.Id)
            throw new GooglePlayRefusedException("That purchase is for something else.", "google_play_wrong_product");
        if (g.PurchaseState == 1)
        {
            await VoidOneTimeAsync(token, ct);
            throw new GooglePlayRefusedException("Google Play says this purchase was cancelled.", "google_play_not_purchased");
        }
        if (g.PurchaseState != 0 && g.PurchaseState != 2)
            throw new GooglePlayRefusedException("Google Play says this purchase didn't complete.", "google_play_not_purchased");

        var owner = Ids.Parse(g.ObfuscatedAccountId)
            ?? throw new GooglePlayRefusedException("This purchase isn't tied to an account.", "google_play_wrong_account", HttpStatusCode.Forbidden);
        if (caller is { } c && c != owner)
            throw new GooglePlayRefusedException("This purchase belongs to another account.", "google_play_wrong_account", HttpStatusCode.Forbidden);
        var eventId = Ids.Parse(g.ObfuscatedProfileId)
            ?? throw new GooglePlayRefusedException("This purchase isn't tied to an event.", "google_play_wrong_event", HttpStatusCode.Forbidden);
        if (requestedEvent is { } asked && asked != eventId)
            throw new GooglePlayRefusedException("This purchase is for a different event.", "google_play_wrong_event", HttpStatusCode.Forbidden);

        if (g.PurchaseState == 2) return new GooglePlayResultDto(Pending, product.Id, eventId, false);

        var isTest = g.PurchaseType == 0;
        if (isTest && !AllowTest)
            throw new GooglePlayRefusedException("Test purchases aren't accepted here.", "google_play_test_refused");

        var now = DateTimeOffset.UtcNow;
        var existing = await db.GooglePlayPurchases.AsNoTracking().FirstOrDefaultAsync(p => p.PurchaseToken == token, ct);
        // Asked before the transaction, so no lock is held across a call to Google. Null is fine.
        var total = existing?.FulfilledAt is null && g.OrderId is { Length: > 0 } order && !isTest
            ? await publisher.GetOrderTotalAsync(order, ct) : null;

        GooglePlayPurchase row;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await InsertIfNewAsync(token, product.Id, owner, eventId, isTest, now, ct);
            row = await LockAsync(token, ct) ?? throw new InvalidOperationException("The row just made has gone.");
            if (row.VoidedAt is not null)
                throw new GooglePlayRefusedException("This purchase was refunded.", "google_play_voided");
            if (row.UserId != owner)
                throw new GooglePlayRefusedException("This purchase belongs to another account.", "google_play_wrong_account", HttpStatusCode.Forbidden);
            if (row.CampaignId != eventId || row.ProductId != product.Id)
                throw new GooglePlayRefusedException("This purchase is for a different event.", "google_play_wrong_event", HttpStatusCode.Forbidden);

            if (row.FulfilledAt is null)
            {
                var campaign = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(x => x.Id == eventId, ct)
                    ?? throw new GooglePlayRefusedException("That event no longer exists.", "google_play_event_gone");
                if (!BillingService.MayBuy(product.Kind, await ownership.AccessOfAsync(owner, eventId, ct)))
                    throw new GooglePlayRefusedException("Only the event's host, or the people it is for, can use this purchase.", "google_play_not_host", HttpStatusCode.Forbidden);
                BillingService.EnsureEventCanHave(product.Kind, campaign, now);

                var payment = NewPayment(product, owner, eventId, g.OrderId, total, isTest, now, BillingService.Describe(product.Kind, 1, campaign), null);
                await ApplyPaymentAsync(payment, ct);
                row.PaymentId = payment.Id;
                row.OrderId = g.OrderId;
                row.FulfilledAt = now;
                row.LastCheckedAt = now;
                await db.SaveChangesAsync(ct);
            }
            await tx.CommitAsync(ct);
        }

        // Google still has it open if it already answered "not consumed"; the sweep covers a failure.
        if (row.SettledAt is null) await SettleOneTimeAsync(row, g.ConsumptionState == 1, ct);
        return new GooglePlayResultDto(Applied, product.Id, eventId, row.IsTest);
    }

    private async Task SettleOneTimeAsync(GooglePlayPurchase row, bool alreadyConsumed, CancellationToken ct)
    {
        try
        {
            var done = alreadyConsumed || await publisher.ConsumeAsync(row.ProductId, row.PurchaseToken, ct);
            if (!done)
            {
                // Refused: fine if the app beat us to it. Anything else stays open for the sweep.
                var now = await publisher.GetProductAsync(row.ProductId, row.PurchaseToken, ct);
                done = now?.ConsumptionState == 1;
            }
            if (done)
            {
                row.SettledAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
            }
            else logger.LogWarning("Google Play purchase {PurchaseId} couldn't be consumed yet.", row.Id);
        }
        catch (GooglePlayUnavailableException)
        {
            // Applied and committed: the buyer has it. Consuming is retried by the sweep.
            logger.LogWarning("Google Play purchase {PurchaseId} wasn't consumed; the sweep will retry.", row.Id);
        }
    }

    private static Payment NewPayment(PlayProduct product, Guid? owner, Guid? eventId, string? orderId, PlayOrderTotal? total, bool isTest, DateTimeOffset now, string description, DateTimeOffset? periodEnds)
    {
        // numeric(10,2). A total that doesn't fit is recorded as unknown, not as a failed purchase.
        var usable = total is { Amount: >= 0 and < 100_000_000m } ? total : null;
        return new Payment
        {
            Id = Guid.NewGuid(),
            CampaignId = eventId,
            UserId = owner,
            Kind = product.Kind,
            Quantity = 1,
            InviteCount = product.Kind == PaymentKind.Sending ? PricingCalculator.BlockSize : 0,
            Amount = usable?.Amount ?? 0,
            Currency = usable?.Currency ?? PaymentProviders.UnknownCurrency,
            Description = isTest ? $"{description} · test" : description,
            Status = PaymentStatus.Paid,
            Provider = PaymentProviders.GooglePlay,
            ProviderPaymentId = string.IsNullOrEmpty(orderId) ? null : orderId,
            CreatedAt = now,
            PaidAt = now,
            IsTest = isTest,
            PeriodEndsAt = periodEnds,
        };
    }

    /// <summary>Records the payment and applies it the same way a card payment is applied. In the caller's transaction.</summary>
    private async Task ApplyPaymentAsync(Payment payment, CancellationToken ct)
    {
        db.Payments.Add(payment);
        await db.SaveChangesAsync(ct);
        await billing.FulfilAsync(payment.Id, ct);
    }

    private Task InsertIfNewAsync(string token, string productId, Guid? owner, Guid? eventId, bool isTest, DateTimeOffset now, CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO google_play_purchases (id, purchase_token, product_id, user_id, campaign_id, is_test, created_at)
            VALUES ({Guid.NewGuid()}, {token}, {productId}, {owner}, {eventId}, {isTest}, {now})
            ON CONFLICT (purchase_token) DO NOTHING
            """, ct);

    private async Task<GooglePlayPurchase?> LockAsync(string token, CancellationToken ct) =>
        (await db.GooglePlayPurchases.FromSqlInterpolated($"SELECT * FROM google_play_purchases WHERE purchase_token = {token} FOR UPDATE").ToListAsync(ct))
        .FirstOrDefault();

    // ----- Subscription --------------------------------------------------------------------------

    /// <summary>
    /// Brings our record of a subscription into line with Google's: a new order is a new paid period,
    /// and the end date always follows Google. <paramref name="caller"/> is null for work
    /// done with nobody signed in.
    /// </summary>
    private async Task<GooglePlayResultDto> SyncSubscriptionAsync(string token, Guid? caller, string? requestedProduct, CancellationToken ct)
    {
        var g = await publisher.GetSubscriptionAsync(token, ct)
            ?? throw new GooglePlayRefusedException("Google Play doesn't know this purchase.", "google_play_not_found", HttpStatusCode.NotFound);
        var line = g.Lines.FirstOrDefault(l => GooglePlayCatalog.Find(l.ProductId) is { IsSubscription: true })
            ?? throw new GooglePlayRefusedException("That purchase is for something else.", "google_play_wrong_product");
        if (requestedProduct is not null && line.ProductId != requestedProduct)
            throw new GooglePlayRefusedException("That purchase is for something else.", "google_play_wrong_product");
        var product = GooglePlayCatalog.Find(line.ProductId)!;

        var known = await db.GooglePlayPurchases.AsNoTracking().FirstOrDefaultAsync(p => p.PurchaseToken == token, ct);
        // Whoever cancelled and came back through the Play Store may hold a new token with no ids on it.
        var linked = g.LinkedPurchaseToken is { Length: > 0 } old
            ? await db.GooglePlayPurchases.AsNoTracking().FirstOrDefaultAsync(p => p.PurchaseToken == old, ct) : null;
        var owner = Ids.Parse(g.ObfuscatedAccountId) ?? known?.UserId ?? linked?.UserId;
        if (known is not null && known.UserId is null)
        {
            // Its account was deleted. Nothing to bring up to date, and nobody to bring it up to date for.
            return new GooglePlayResultDto(Applied, product.Id, null, known.IsTest);
        }
        if (owner is not { } account)
            throw new GooglePlayRefusedException("This purchase isn't tied to an account.", "google_play_wrong_account", HttpStatusCode.Forbidden);
        if (caller is { } c && c != account)
            throw new GooglePlayRefusedException("This purchase belongs to another account.", "google_play_wrong_account", HttpStatusCode.Forbidden);

        if (g.State == "SUBSCRIPTION_STATE_PENDING") return new GooglePlayResultDto(Pending, product.Id, null, false);
        if (g.State is "SUBSCRIPTION_STATE_PENDING_PURCHASE_CANCELED" or "SUBSCRIPTION_STATE_UNSPECIFIED" && known is null)
            throw new GooglePlayRefusedException("Google Play says this purchase didn't complete.", "google_play_not_purchased");

        if (g.IsTest && !AllowTest)
            throw new GooglePlayRefusedException("Test purchases aren't accepted here.", "google_play_test_refused");

        var now = DateTimeOffset.UtcNow;
        var entitled = Entitled.Contains(g.State) && line.ExpiryTime > now;
        if (!entitled && known is null)
            throw new GooglePlayRefusedException("This subscription has ended.", "google_play_not_purchased");

        var orderKey = line.LatestSuccessfulOrderId;
        var isNewPeriod = entitled && (known?.FulfilledAt is null || (orderKey is not null && orderKey != known.OrderId));
        var total = isNewPeriod && orderKey is not null && !g.IsTest ? await publisher.GetOrderTotalAsync(orderKey, ct) : null;

        GooglePlayPurchase row;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            await InsertIfNewAsync(token, product.Id, account, null, g.IsTest, now, ct);
            row = await LockAsync(token, ct) ?? throw new InvalidOperationException("The row just made has gone.");
            if (row.UserId != account)
                throw new GooglePlayRefusedException("This purchase belongs to another account.", "google_play_wrong_account", HttpStatusCode.Forbidden);

            var user = await db.Users.FirstOrDefaultAsync(u => u.Id == account, ct)
                ?? throw new GooglePlayRefusedException("That account no longer exists.", "google_play_wrong_account", HttpStatusCode.Forbidden);

            if (entitled)
            {
                var firstTime = row.FulfilledAt is null;
                if (firstTime) BillingService.EnsureAccountCanHave(product.Kind, user, now);
                if (firstTime || (orderKey is not null && orderKey != row.OrderId))
                {
                    var payment = NewPayment(product, account, null, orderKey, total, g.IsTest, now,
                        BillingService.Describe(product.Kind, 1, null) + (firstTime ? "" : " · renewed automatically"), line.ExpiryTime);
                    await ApplyPaymentAsync(payment, ct);
                    row.PaymentId = payment.Id;
                    row.OrderId = orderKey;
                    row.FulfilledAt ??= now;
                }
                else if (user.SubscriptionEndsAt is { } ends && ends == row.ExpiresAt && ends != line.ExpiryTime)
                {
                    user.SubscriptionEndsAt = line.ExpiryTime;   // moved by Google (deferred, grace period)
                }
            }
            else if (row.ExpiresAt is { } mine && user.SubscriptionEndsAt == mine)
            {
                // On hold, paused, ended or revoked: what this subscription gave stops. A plan that came
                // from somewhere else (an admin, time paid another way) has a different date and stays.
                var end = line.ExpiryTime is { } e && e < now ? e : now;
                user.SubscriptionEndsAt = end;
                logger.LogInformation("Google Play subscription {PurchaseId} no longer covers its account ({State}).", row.Id, g.State);
            }

            row.ExpiresAt = line.ExpiryTime ?? row.ExpiresAt;
            row.SubscriptionState = g.State;
            row.AutoRenewing = entitled && line.AutoRenewEnabled;
            row.LastCheckedAt = now;
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }

        if (row.SettledAt is null && row.FulfilledAt is not null) await SettleSubscriptionAsync(row, g.Acknowledged, ct);
        return new GooglePlayResultDto(Applied, product.Id, null, row.IsTest);
    }

    private async Task SettleSubscriptionAsync(GooglePlayPurchase row, bool alreadyAcknowledged, CancellationToken ct)
    {
        try
        {
            var done = alreadyAcknowledged || await publisher.AcknowledgeSubscriptionAsync(row.ProductId, row.PurchaseToken, ct);
            if (!done) done = (await publisher.GetSubscriptionAsync(row.PurchaseToken, ct))?.Acknowledged == true;
            if (done)
            {
                row.SettledAt = DateTimeOffset.UtcNow;
                await db.SaveChangesAsync(ct);
            }
        }
        catch (GooglePlayUnavailableException)
        {
            logger.LogWarning("Google Play subscription {PurchaseId} wasn't acknowledged; the sweep will retry.", row.Id);
        }
    }

    // ----- Voided --------------------------------------------------------------------------------

    /// <summary>Refunded or revoked: what it bought comes back, the way an admin would take it.</summary>
    private async Task VoidOneTimeAsync(string token, CancellationToken ct)
    {
        var now = DateTimeOffset.UtcNow;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var row = await LockAsync(token, ct);
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
            Action = "billing.google_play.voided",
            Actor = "google_play",
            CampaignId = row.CampaignId,
            DataJson = JsonSerializer.Serialize(new { purchase = row.Id, payment = row.PaymentId, product = row.ProductId }),
            CreatedAt = now,
        });
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        logger.LogInformation("Google Play purchase {PurchaseId} was voided; what it bought is taken back.", row.Id);
    }

    private async Task HandleVoidedAsync(string token, string? orderId, CancellationToken ct)
    {
        var row = await db.GooglePlayPurchases.AsNoTracking().FirstOrDefaultAsync(p => p.PurchaseToken == token, ct);
        if (row is null) return;   // Never applied, so nothing to take back.
        if (GooglePlayCatalog.Find(row.ProductId) is { IsSubscription: true })
        {
            // One period refunded; Google's state says whether the subscription itself still stands.
            if (orderId is not null)
                await db.Payments.Where(p => p.Provider == PaymentProviders.GooglePlay && p.ProviderPaymentId == orderId && p.Status == PaymentStatus.Paid)
                    .ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PaymentStatus.Refunded), ct);
            await SyncSubscriptionAsync(token, null, null, ct);
            return;
        }
        await VoidOneTimeAsync(token, ct);
    }

    // ----- Notifications -------------------------------------------------------------------------

    public async Task HandleNotificationAsync(string rawBody, CancellationToken ct = default)
    {
        // Past the push's own failure mode, a bad message must be acknowledged or Pub/Sub repeats it for a week.
        JsonElement n;
        try
        {
            using var envelope = JsonDocument.Parse(rawBody);
            var data = envelope.RootElement.GetProperty("message").GetProperty("data").GetString() ?? "";
            using var inner = JsonDocument.Parse(Encoding.UTF8.GetString(Convert.FromBase64String(data)));
            n = inner.RootElement.Clone();
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            logger.LogWarning("Ignored an unreadable Google Play notification ({Reason}).", ex.GetType().Name);
            return;
        }

        if (n.TryGetProperty("testNotification", out _))
        {
            logger.LogInformation("Google Play test notification received.");
            return;
        }
        var expected = config["GooglePlay:PackageName"];
        if (!n.TryGetProperty("packageName", out var pkg) || pkg.GetString() != expected)
        {
            logger.LogWarning("Ignored a Google Play notification for another package.");
            return;
        }

        // Each only says what to ask Google about; only what Google answers counts.
        try
        {
            if (n.TryGetProperty("subscriptionNotification", out var sub) && Text(sub, "purchaseToken") is { } subToken)
                await SyncSubscriptionAsync(subToken, null, null, CancellationToken.None);
            else if (n.TryGetProperty("oneTimeProductNotification", out var one) && Text(one, "purchaseToken") is { } oneToken
                     && GooglePlayCatalog.Find(Text(one, "sku")) is { IsSubscription: false } product)
                await RedeemOneTimeAsync(product, oneToken, null, null, CancellationToken.None);
            else if (n.TryGetProperty("voidedPurchaseNotification", out var voided) && Text(voided, "purchaseToken") is { } voidedToken)
                await HandleVoidedAsync(voidedToken, Text(voided, "orderId"), CancellationToken.None);
        }
        catch (AppException ex) when (ex is not GooglePlayUnavailableException)
        {
            // A settled "no" (test refused, event gone, another account). A retry would say the same,
            // and the purchase stays open: the app can still redeem it, or Google refunds it.
            logger.LogInformation("Google Play notification not applied: {Code}.", ex.ErrorCode);
        }
    }

    private static string? Text(JsonElement j, string name) =>
        j.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // ----- Sweep ---------------------------------------------------------------------------------

    public async Task SweepAsync(DateTimeOffset now, bool full, CancellationToken ct = default)
    {
        if (!publisher.IsConfigured) return;

        // Applied but not yet closed at Google. Past ten days Google has refunded it and the voided list says so.
        var since = now.AddDays(-10);
        var open = await db.GooglePlayPurchases
            .Where(p => p.FulfilledAt != null && p.SettledAt == null && p.VoidedAt == null && p.UserId != null && p.CreatedAt > since)
            .OrderBy(p => p.CreatedAt).Take(100).ToListAsync(ct);
        foreach (var row in open)
        {
            if (GooglePlayCatalog.Find(row.ProductId) is { IsSubscription: true }) await SettleSubscriptionAsync(row, false, ct);
            else await SettleOneTimeAsync(row, false, ct);
        }
        if (!full) return;

        // Without notifications this is what notices a renewal, a lapse or a cancellation. Near or past the
        // end of the period, and for a month after in case of an account hold that recovers.
        var due = now.AddDays(1);
        var from = now.AddDays(-35);
        var checkedBefore = now.AddHours(-6);
        var subscriptions = await db.GooglePlayPurchases.AsNoTracking()
            .Where(p => p.CampaignId == null && p.UserId != null && p.VoidedAt == null && p.ExpiresAt != null
                        && p.ExpiresAt < due && p.ExpiresAt > from && (p.LastCheckedAt == null || p.LastCheckedAt < checkedBefore))
            .Select(p => p.PurchaseToken).Take(200).ToListAsync(ct);
        foreach (var token in subscriptions)
        {
            try { await SyncSubscriptionAsync(token, null, null, ct); }
            catch (AppException ex) when (ex is not GooglePlayUnavailableException)
            {
                logger.LogInformation("A Google Play subscription re-check was refused: {Code}.", ex.ErrorCode);
            }
            db.ChangeTracker.Clear();
        }

        foreach (var v in await publisher.ListVoidedAsync(ct))
        {
            await HandleVoidedAsync(v.PurchaseToken, v.OrderId, ct);
            db.ChangeTracker.Clear();
        }

        // Amounts Google hadn't told us. A real order's amount is worth asking for until it's a month old.
        var month = now.AddDays(-30);
        var unknown = await db.Payments
            .Where(p => p.Provider == PaymentProviders.GooglePlay && p.Currency == PaymentProviders.UnknownCurrency
                        && !p.IsTest && p.ProviderPaymentId != null && p.CreatedAt > month)
            .OrderBy(p => p.CreatedAt).Take(50).ToListAsync(ct);
        foreach (var p in unknown)
        {
            if (await publisher.GetOrderTotalAsync(p.ProviderPaymentId!, ct) is { Amount: >= 0 and < 100_000_000m } total)
            {
                p.Amount = total.Amount;
                p.Currency = total.Currency;
            }
        }
        await db.SaveChangesAsync(ct);
    }
}

/// <summary>The ids that go into a purchase. Random, and meaning nothing outside this system.</summary>
internal static class Ids
{
    public static string Of(Guid id) => id.ToString("N");
    public static Guid? Parse(string? value) => Guid.TryParseExact(value, "N", out var id) ? id : null;
}
