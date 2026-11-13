using System.Text.Json;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Campaigns;
using InvitesBlog.Application.Common;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Legal;
using InvitesBlog.Application.Plans;
using InvitesBlog.Application.Pricing;
using InvitesBlog.Application.Services.Campaigns;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using System.Text.Json.Serialization;

namespace InvitesBlog.Application.Services.Billing;

public sealed record BillingPricesDto(
    decimal GoldPass, decimal PlatinumPass, decimal KeepPhotos, decimal SendingPerBlock, int SendingBlockSize,
    decimal PremiumMonthly, int VenueDiscountPercent,
    decimal GoldExtension, decimal PlatinumExtension, decimal VenueMonthly, decimal DesignerAiMonthly = PlanCatalog.DesignerAiMonthly,
    decimal PhotographerMonthly = PlanCatalog.PhotographerMonthly)
{
    // The same prices under their names before the rename, read by web tabs still open from before
    // that deploy. Nothing current reads them; remove them a release later.
    [JsonPropertyName("partyPass")] public decimal OldGoldPass => GoldPass;
    [JsonPropertyName("weddingPass")] public decimal OldPlatinumPass => PlatinumPass;
    [JsonPropertyName("partyExtension")] public decimal OldGoldExtension => GoldExtension;
    [JsonPropertyName("weddingExtension")] public decimal OldPlatinumExtension => PlatinumExtension;
}

/// <summary>The account's own plan: None, Premium or Venue.</summary>
/// <param name="AutoRenew">What renews by itself at <paramref name="EndsAt"/> (premium-monthly,
/// venue-monthly), charging the saved card; null when nothing does.</param>
/// <param name="Store">"google_play" or "app_store" when the plan runs on a store subscription: it is managed in the
/// store, and our own "turn off automatic renewal" doesn't reach it.</param>
/// <param name="StoreAutoRenew">That subscription will renew by itself.</param>
/// <param name="AddOns">The plan's add-ons (the AI designer), one row per known add-on (<see cref="Plans.AddOns.ForAccount"/>).</param>
/// <param name="CanDownloadAll">The account itself may download or share any number of photos at once (Premium or Studio).</param>
public sealed record BillingAccountDto(string Tier, DateTimeOffset? EndsAt, bool Active, string? AutoRenew = null,
    string? Store = null, bool StoreAutoRenew = false, IReadOnlyList<AccountAddOnDto>? AddOns = null, bool CanDownloadAll = false);

/// <summary>Where one payment stands, for the page the buyer lands on after the gateway.</summary>
/// <param name="Status">Pending, Paid or Failed.</param>
public sealed record PaymentStatusDto(Guid Id, string Item, string Description, decimal Amount, string Currency, string Status);

/// <summary>One of the account's events, with everything that can be bought for it.</summary>
/// <param name="Pass">The pass it has or last had (None, Gold, Platinum) — what an extension extends.</param>
/// <param name="Offer">What its passes cost: the same for every event and buyer.</param>
/// <param name="IsDraft">Not finished yet: a pass bought now is what it goes out with.</param>
/// <param name="Role">Why the account sees it: "organiser" (it started it), "manager" (someone it is for, given
/// full access) or "celebrant" (someone it is for, read-only). ("venue" is no longer sent: there are no venue
/// owners or staff any more.) Everyone but the organiser may buy a pass, another year of one and keeping the photos, not emails
/// (<see cref="BillingService.MayBuy"/>).</param>
/// <param name="AtVenue">Its organiser's Studio plan (tier Venue) covers it, or did.</param>
/// <param name="VenueEnded">Its organiser's Studio plan covered it and has ended: a Platinum pass is what keeps its photos.</param>
public sealed record BillingEventDto(
    Guid CampaignId, string Title, DateTimeOffset EventStartAt, string Kind, string Plan,
    DateTimeOffset? PassUntil, DateTimeOffset? KeepPhotosUntil, DateTimeOffset? CoveredUntil, string Phase,
    SendingAllowanceDto Sending, bool AtVenue, string Pass, bool PassActive, PassOfferDto Offer, bool IsDraft,
    DateTimeOffset? TrimAt = null, long? TrimBytes = null, string? TimeZone = null,
    string Role = BillingRoles.Organiser, bool VenueEnded = false);

/// <summary>The values of <see cref="BillingEventDto.Role"/>.</summary>
public static class BillingRoles
{
    public const string Organiser = "organiser";
    public const string Manager = "manager";
    public const string Celebrant = "celebrant";
}

public sealed record BillingPaymentDto(
    Guid Id, string Item, string Description, decimal Amount, string Currency, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset? PaidAt, Guid? CampaignId);

/// <param name="Events">The events the account started (unchanged, for older app builds).</param>
/// <param name="ForYou">Events someone else started that the account is in: it is one of the people the event is for
/// (read-only or managing), or it owns or works at the venue the event is at. Same rows as <paramref name="Events"/>,
/// with <see cref="BillingEventDto.Role"/> saying which; never an event that is also in <paramref name="Events"/>.</param>
public sealed record BillingOverviewDto(
    bool PaymentsEnabled, string Currency, decimal MvrPerUsd, BillingPricesDto Prices, BillingAccountDto Account,
    IReadOnlyList<BillingEventDto> Events, IReadOnlyList<BillingPaymentDto> Payments,
    IReadOnlyList<BillingEventDto> ForYou);

/// <param name="Item">gold-pass, platinum-pass, gold-extension, platinum-extension, keep-photos, sending,
/// premium-monthly, premium-ai-monthly (Premium with the AI designer), designer-ai-add (the AI designer added to a
/// running Premium for the rest of its month) or venue-monthly.</param>
/// <param name="Quantity">Blocks of emails. 1 for the rest.</param>
/// <param name="ReturnPath">A path on this site to come back to after paying (the plan step); the billing page by default.</param>
/// <param name="AcceptedTerms">The buyer ticked "I agree" on the review step. Required to pay.</param>
/// <param name="TermsVersion">The version of the terms that review step showed (<see cref="LegalTerms.Version"/>).</param>
public sealed record CheckoutRequest(
    string Item, Guid? CampaignId, int? Quantity, string? ReturnPath = null,
    bool AcceptedTerms = false, string? TermsVersion = null);

/// <summary>
/// What paying for an item would be, for the review step shown before the gateway: the bank's card
/// rules require the total, the currency and the terms to be on screen, and accepted, before paying.
/// </summary>
/// <param name="Available">Online payment is on. When it isn't, <paramref name="Message"/> and
/// <paramref name="InquireTopic"/> say where to go instead.</param>
/// <param name="Amount">The price, in <paramref name="Currency"/> (rufiyaa).</param>
/// <param name="ChargeAmount">What the card is charged, in <paramref name="ChargeCurrency"/>: the same as
/// the price in production; in dollars where the gateway account only takes dollars (<see cref="ChargeCurrency"/>).</param>
/// <param name="RenewsAmount">What renews by itself each month after this (rufiyaa), for a plan or an add-on to one
/// that renews; null when nothing renews.</param>
/// <param name="RenewsItem">What that renewal is: premium-monthly, premium-ai-monthly or venue-monthly.</param>
/// <param name="PeriodEndsAt">For designer-ai-add: the end of the paid month it covers.</param>
public sealed record CheckoutQuoteDto(
    bool Available, string Item, string Description, decimal Amount, string Currency, decimal MvrPerUsd,
    string TermsVersion, string? Message, string? InquireTopic, decimal ChargeAmount, string ChargeCurrency,
    decimal? RenewsAmount = null, string? RenewsItem = null, DateTimeOffset? PeriodEndsAt = null);

/// <summary>
/// Where to pay, or — while online payment isn't switched on — that it isn't yet, and which
/// "Ask us" topic to send the person to instead.
/// </summary>
public sealed record CheckoutResultDto(bool Available, string? CheckoutUrl, string? Message, string? InquireTopic, Guid? PaymentId);

public interface IBillingService
{
    Task<BillingOverviewDto> GetAsync(CancellationToken ct = default);

    /// <summary>One event's plan and what it can have: what the plan step before sending shows.</summary>
    Task<BillingEventDto> EventAsync(Guid campaignId, CancellationToken ct = default);
    /// <summary>What an item costs and what it is, checked the same way paying for it is, without paying.</summary>
    Task<CheckoutQuoteDto> QuoteAsync(CheckoutRequest req, CancellationToken ct = default);
    Task<CheckoutResultDto> CheckoutAsync(CheckoutRequest req, CancellationToken ct = default);

    /// <summary>
    /// One of the buyer's payments, asking the gateway first if it's still pending (and applying it if
    /// it has gone through): the buyer can land back before the webhook does.
    /// </summary>
    Task<PaymentStatusDto> PaymentStatusAsync(Guid paymentId, CancellationToken ct = default);

    /// <summary>Stops the account's plan renewing by itself. It runs to the end of what's paid.</summary>
    Task<BillingAccountDto> StopAutoRenewAsync(CancellationToken ct = default);

    /// <summary>
    /// Whether the AI designer add-on is part of the next card renewal (designer-ai-on / designer-ai-off). Off: it
    /// stays until the paid month ends and Premium renews alone. On: only while it is still in force.
    /// </summary>
    Task<BillingAccountDto> SetDesignerAiRenewalAsync(bool renew, CancellationToken ct = default);

    /// <summary>Applies what a paid payment bought, once. Called after the gateway confirms payment.</summary>
    Task FulfilAsync(Guid paymentId, CancellationToken ct = default);
}

/// <summary>
/// Everything that can be paid for, in one place: per event (a pass, another year of it, keeping the
/// photos, emails) and per account (Premium, Venue). Prices come from the price book, the same for
/// everyone (<see cref="IPassOfferService"/>). A pass, another year of one, or keeping the photos can be
/// bought by anyone the event is for as well as whoever runs it (<see cref="MayBuy"/>).
///
/// <para><b>Ready for the gateway.</b> Checkout records a pending <see cref="Payment"/> and asks the
/// <see cref="IPaymentProvider"/> for a checkout page; the gateway's webhook marks it paid
/// (PaymentService) and <see cref="FulfilAsync"/> applies it — the same changes an admin makes by
/// hand today. Until <c>Payments:Enabled</c> is true, checkout says so and points at "Ask us".</para>
/// </summary>
public sealed class BillingService(
    ICurrentUser currentUser,
    IConfiguration config,
    IPriceBook priceBook,
    IPlanService plans,
    ICampaignOwnershipService ownership,
    ICampaignRepository campaigns,
    IRepository<AppUser> users,
    IPaymentRepository payments,
    IPassOfferService offers,
    IPaymentProvider provider,
    ISendingAllowanceService allowances,
    MediaBuckets.IMediaBucketService buckets,
    IRepository<AuditLog> auditLogs,
    IUnitOfWork uow,
    IRecurringPaymentProvider? recurring = null,
    Payments.IPaymentService? paymentService = null,
    IRepository<GooglePlayPurchase>? playPurchases = null,
    IRepository<AppStorePurchase>? appStorePurchases = null,
    IRepository<CampaignCelebrant>? celebrants = null) : IBillingService
{
    private const int MaxListed = 50;

    private bool Enabled => bool.TryParse(config["Payments:Enabled"], out var on) && on;

    public async Task<BillingOverviewDto> GetAsync(CancellationToken ct = default)
    {
        var me = RequireUser();
        var user = await users.Query().FirstOrDefaultAsync(u => u.Id == me, ct)
                   ?? throw new NotFoundException("That account no longer exists.");
        var p = await priceBook.CurrentAsync(ct);
        var now = DateTimeOffset.UtcNow;

        var mine = await campaigns.Query()
            .Where(c => c.CreatedByUserId == me && c.Status != CampaignStatus.Cancelled)
            .OrderByDescending(c => c.EventStartAt)
            .Take(MaxListed)
            .ToListAsync(ct);
        var theirs = await ForYouAsync(user, ct);

        // Every row's plan, emails and offer in a fixed number of queries, however many events there are.
        var all = mine.Concat(theirs.Select(t => t.Campaign)).ToList();
        var planOf = await plans.ForCampaignsAsync(all, ct);
        var sendingOf = await allowances.ForCampaignsAsync(all, planOf, ct);
        // The offer is the price book's, the same for every event and buyer.
        var offer = all.Count == 0 ? null : await offers.ForCampaignAsync(all[0].Id, ct);
        var events = mine.Select(c => Describe(c, planOf[c.Id], sendingOf[c.Id], offer!, now, BillingRoles.Organiser)).ToList();
        var forYou = theirs.Select(t => Describe(t.Campaign, planOf[t.Campaign.Id], sendingOf[t.Campaign.Id], offer!, now, t.Role)).ToList();

        var ids = mine.Select(c => c.Id).ToList();
        var history = await payments.Query()
            .Where(x => x.UserId == me || (x.CampaignId != null && ids.Contains(x.CampaignId.Value)))
            .OrderByDescending(x => x.CreatedAt)
            .Take(MaxListed)
            .ToListAsync(ct);

        return new BillingOverviewDto(
            Enabled, PlanCatalog.Currency, p.MvrPerUsd,
            new BillingPricesDto(p.GoldPass, p.PlatinumPass, p.KeepPhotosYearly, p.SendingPerBlock, PricingCalculator.BlockSize,
                // The venue discount is gone: 0 whatever a saved price list holds.
                p.PremiumMonthly, PlanCatalog.VenuePassDiscountPercent,
                p.GoldExtension, p.PlatinumExtension, p.VenueMonthly, p.DesignerAiMonthly, p.PhotographerMonthly),
            await AccountWithStoreAsync(user, now, ct),
            events,
            history.Select(x => new BillingPaymentDto(x.Id, ItemName(x.Kind), x.Description ?? ItemName(x.Kind),
                x.Amount, x.Currency, x.Status.ToString(), x.CreatedAt, x.PaidAt, x.CampaignId)).ToList(),
            forYou);
    }

    /// <summary>
    /// Events someone else started that this account is in, with why: one of the people it is for (matched on the
    /// account's proved email or phone, as <see cref="ICampaignOwnershipService"/> does). Cancelled events are
    /// left out, as in the account's own list. Two queries.
    /// </summary>
    private async Task<IReadOnlyList<(Campaign Campaign, string Role)>> ForYouAsync(AppUser user, CancellationToken ct)
    {
        if (celebrants is null) return [];
        var email = user.Email?.Trim().ToLowerInvariant();
        var phone = user.PhoneE164?.Trim();
        // Access to an event needs a proved contact (CampaignOwnershipService): none, no events.
        if (string.IsNullOrEmpty(email) && string.IsNullOrEmpty(phone)) return [];

        var celebrating = (await celebrants.Query()
                .Where(c => (email != null && c.Email == email) || (phone != null && c.PhoneE164 == phone))
                .Select(c => new { c.CampaignId, c.CanManage })
                .ToListAsync(ct))
            .GroupBy(c => c.CampaignId)
            .ToDictionary(g => g.Key, g => g.Any(c => c.CanManage));
        var celebratingIds = celebrating.Keys.ToList();

        if (celebratingIds.Count == 0) return [];
        var me = user.Id;
        var list = await campaigns.Query()
            .Where(c => c.CreatedByUserId != me && c.Status != CampaignStatus.Cancelled && celebratingIds.Contains(c.Id))
            .OrderByDescending(c => c.EventStartAt)
            .Take(MaxListed)
            .ToListAsync(ct);

        // The same order of precedence as the event's access: a managing celebrant, then read-only.
        return list.Select(c => (c,
                celebrating.TryGetValue(c.Id, out var manages) && manages ? BillingRoles.Manager
                : BillingRoles.Celebrant))
            .ToList();
    }

    public async Task<BillingEventDto> EventAsync(Guid campaignId, CancellationToken ct = default)
    {
        RequireUser();
        // Whoever may buy something for it: the people it is for as well as whoever runs it.
        var access = await ownership.AccessAsync(campaignId, ct);
        if (access < CampaignAccess.Celebrant)
            throw new ForbiddenException("Only the event's host, or the people it is for, can see what it costs.");
        var c = await campaigns.GetByIdAsync(campaignId, ct) ?? throw new NotFoundException("That event no longer exists.");
        var role = access switch
        {
            CampaignAccess.Organiser => BillingRoles.Organiser,
            CampaignAccess.Manager => BillingRoles.Manager,
            _ => BillingRoles.Celebrant,
        };
        var plan = await plans.ForCampaignAsync(c.Id, ct);
        return Describe(c, plan, await allowances.ForCampaignAsync(c.Id, ct), await offers.ForCampaignAsync(c.Id, ct),
            DateTimeOffset.UtcNow, role);
    }

    private static BillingEventDto Describe(
        Campaign c, EventPlan plan, SendingAllowanceDto sending, PassOfferDto offer, DateTimeOffset now, string role)
    {
        var pass = EventPasses.Active(c, now);
        return new BillingEventDto(
            c.Id, c.Title, c.EventStartAt, SaveTheDates.Name(c.Kind), plan.Kind.ToString(),
            pass == EventPassKind.None ? null : c.EventPassUntil, c.KeepPhotosUntil, plan.CoveredUntil,
            plan.Phase.ToString(), sending, plan.VenueAccountId is not null || plan.VenueEnded,
            c.EventPass.ToString(), pass != EventPassKind.None, offer,
            c.Status == CampaignStatus.Draft,
            plan.Phase == MediaPhase.UploadsClosed ? c.MediaTrimAt : null,
            plan.Phase == MediaPhase.UploadsClosed ? c.MediaTrimBytes : null,
            c.TimeZone,
            role,
            // The organiser's Studio plan covered it and has ended.
            plan.VenueEnded);
    }

    public async Task<CheckoutQuoteDto> QuoteAsync(CheckoutRequest req, CancellationToken ct = default)
    {
        var me = RequireUser();
        var priced = await PriceAsync(me, req, ct);
        var p = await priceBook.CurrentAsync(ct);
        var (charge, chargeCurrency) = ChargeCurrency.For(config, priced.Amount, p.MvrPerUsd);
        var (renewsItem, renewsAmount) = Renewal(priced, p);
        return new CheckoutQuoteDto(Enabled, ItemName(priced.Kind), priced.Description, priced.Amount, PlanCatalog.Currency,
            p.MvrPerUsd, LegalTerms.Version, Enabled ? null : NotYetMessage, InquireTopic(priced.Kind), charge, chargeCurrency,
            renewsAmount, renewsItem is { } k ? ItemName(k) : null,
            priced.Kind == PaymentKind.DesignerAiAdd ? priced.Account?.SubscriptionEndsAt : null);
    }

    /// <summary>What renews each month after buying this, and for how much: the review step says it.</summary>
    private static (PaymentKind? Kind, decimal? Amount) Renewal(Priced priced, Prices p) => priced.Kind switch
    {
        PaymentKind.PremiumMonthly => (PaymentKind.PremiumMonthly, p.PremiumMonthly),
        PaymentKind.PremiumAiMonthly => (PaymentKind.PremiumAiMonthly, p.PremiumMonthly + p.DesignerAiMonthly),
        PaymentKind.VenueMonthly => (PaymentKind.VenueMonthly, p.VenueMonthly),
        PaymentKind.PhotographerMonthly => (PaymentKind.PhotographerMonthly, p.PhotographerMonthly),
        // Added to a Premium that renews by card: the renewal carries it from then on.
        PaymentKind.DesignerAiAdd when priced.Account is { AutoRenewKind: not null, SubscriptionEndsAt: not null } =>
            (PaymentKind.PremiumAiMonthly, p.PremiumMonthly + p.DesignerAiMonthly),
        _ => (null, null),
    };

    private const string NotYetMessage = "Online payment is being set up. Ask us and we'll add it for you.";

    public async Task<CheckoutResultDto> CheckoutAsync(CheckoutRequest req, CancellationToken ct = default)
    {
        var me = RequireUser();
        var (kind, quantity, campaign, price, description, _) = await PriceAsync(me, req, ct);
        var now = DateTimeOffset.UtcNow;
        // What the card is charged, which is what the payment records and the gateway must confirm.
        var (amount, currency) = ChargeCurrency.For(config, price, (await priceBook.CurrentAsync(ct)).MvrPerUsd);

        if (!Enabled)
            return new CheckoutResultDto(false, null, NotYetMessage, InquireTopic(kind), null);

        // The terms have to be on screen and accepted before paying, and the ones accepted have to be
        // the ones in force: a page opened before they changed must be reloaded, not paid through.
        if (!req.AcceptedTerms)
            throw new BusinessRuleException("Please read and accept the terms before paying.", "billing_terms_not_accepted");
        if (req.TermsVersion != LegalTerms.Version)
            throw new BusinessRuleException("Our terms have changed since this page opened. Please reload it and review them.",
                "billing_terms_changed");

        // A plan is bought with its card saved, so it can renew by itself: the gateway keeps the card
        // for this account's customer there. Not for an account with no email (the gateway needs one).
        var saveCard = false;
        string? customerId = null;
        if (IsSubscription(kind) && recurring is not null)
        {
            var buyer = await users.Query(tracking: true).FirstOrDefaultAsync(u => u.Id == me, ct);
            if (buyer is { Email: { Length: > 0 } email })
            {
                try
                {
                    buyer.PaymentCustomerId ??= await recurring.CreateCustomerAsync(buyer.DisplayName, email, ct);
                    customerId = buyer.PaymentCustomerId;
                    saveCard = true;
                }
                catch (HttpRequestException)
                {
                    // The gateway's customer couldn't be made: the plan is still bought, just not set to
                    // renew by itself. Paying matters more than the convenience.
                }
            }
        }

        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            AutoRenew = saveCard,
            CampaignId = campaign?.Id,
            UserId = me,
            Kind = kind,
            Quantity = quantity,
            InviteCount = kind == PaymentKind.Sending ? quantity * PricingCalculator.BlockSize : 0,
            Amount = amount,
            Currency = currency,
            Description = description,
            Status = PaymentStatus.Pending,
            Provider = provider.Name,
            CreatedAt = now,
            TermsAcceptedAt = now,
            TermsVersion = LegalTerms.Version,
        };
        await payments.AddAsync(payment, ct);
        await uow.SaveChangesAsync(ct);

        // Back to the web app (app.invites.blog), where the review step and Billing live.
        var inviterBase = config.AppBase();
        var back = string.IsNullOrWhiteSpace(req.ReturnPath) || !req.ReturnPath.StartsWith('/') || req.ReturnPath.StartsWith("//")
            ? "/billing" : req.ReturnPath;
        var session = await provider.CreateCheckoutSessionAsync(new CreateCheckoutSessionRequest(
            campaign?.Id ?? Guid.Empty, kind.ToString(), amount, currency, payment.InviteCount,
            $"{inviterBase}{back}{(back.Contains('?') ? '&' : '?')}paid={payment.Id}", $"{inviterBase}{back}")
        {
            PaymentId = payment.Id,
            Description = description,
            CustomerId = customerId,
            SaveCard = saveCard,
        }, ct);
        payment.ProviderSessionId = session.SessionId;
        await uow.SaveChangesAsync(ct);

        return new CheckoutResultDto(true, session.CheckoutUrl, null, null, payment.Id);
    }

    private sealed record Priced(PaymentKind Kind, int Quantity, Campaign? Campaign, decimal Amount, string Description, AppUser? Account = null);

    /// <summary>
    /// What an item is and costs for this buyer, after every check that decides whether they may buy it.
    /// The quote and the payment both go through here, so the review step never shows a price the
    /// payment wouldn't charge.
    /// </summary>
    private async Task<Priced> PriceAsync(Guid me, CheckoutRequest req, CancellationToken ct)
    {
        var kind = ParseItem(req.Item);
        var p = await priceBook.CurrentAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var quantity = kind == PaymentKind.Sending ? Math.Clamp(req.Quantity ?? 1, 1, 50) : 1;

        Campaign? campaign = null;
        AppUser? account = null;
        if (IsEventItem(kind))
        {
            if (req.CampaignId is not { } id) throw new BusinessRuleException("Choose which event it's for.", "billing_event_required");
            if (!MayBuy(kind, await ownership.AccessAsync(id, ct)))
                throw new ForbiddenException(kind == PaymentKind.Sending
                    ? "Only the event's host can pay for it."
                    : "Only the event's host, or the people it is for, can pay for it.");
            campaign = await campaigns.GetByIdAsync(id, ct) ?? throw new NotFoundException("That event no longer exists.");
            EnsureEventCanHave(kind, campaign, now);
        }
        else
        {
            account = await users.Query().FirstOrDefaultAsync(u => u.Id == me, ct)
                       ?? throw new NotFoundException("That account no longer exists.");
            EnsureAccountCanHave(kind, account, now);
            await EnsureCardCanHaveAsync(kind, account, now, ct);
        }

        var offer = campaign is null ? null : await offers.ForCampaignAsync(campaign.Id, ct);
        var amount = kind switch
        {
            PaymentKind.GoldPass => offer!.GoldPass,
            PaymentKind.PlatinumPass => offer!.PlatinumPass,
            PaymentKind.GoldExtension => offer!.GoldExtension,
            PaymentKind.PlatinumExtension => offer!.PlatinumExtension,
            PaymentKind.KeepPhotos => p.KeepPhotosYearly,
            PaymentKind.Sending => p.SendingPerBlock * quantity,
            PaymentKind.PremiumMonthly => p.PremiumMonthly,
            PaymentKind.PremiumAiMonthly => p.PremiumMonthly + p.DesignerAiMonthly,
            PaymentKind.DesignerAiAdd => AddOns.ProratedDesignerAi(p, account!.SubscriptionEndsAt, now),
            PaymentKind.VenueMonthly => p.VenueMonthly,
            PaymentKind.PhotographerMonthly => p.PhotographerMonthly,
            _ => throw new BusinessRuleException("That can't be bought here.", "billing_item_unknown"),
        };
        var description = Describe(kind, quantity, campaign);

        return new Priced(kind, quantity, campaign, amount, description, account);
    }

    /// <summary>
    /// What only the website's card checkout refuses (a store sells the same things its own way): Premium with the
    /// AI designer bought on top of a running Premium (that is the prorated add-on), and the add-on itself unless
    /// the account is on a Premium the card renews, without it yet.
    /// </summary>
    private async Task EnsureCardCanHaveAsync(PaymentKind kind, AppUser user, DateTimeOffset now, CancellationToken ct)
    {
        if (kind == PaymentKind.PremiumAiMonthly && AddOns.PremiumActive(user, now) && !AddOns.DesignerAiActive(user, now))
            throw new CodedRuleException("You're on Premium already: add the AI designer from Billing.", "designer_ai_use_add");
        if (kind != PaymentKind.DesignerAiAdd) return;
        if (AddOns.VenueActive(user, now))
            throw new CodedRuleException("Studio has the AI designer included.", "designer_ai_included");
        if (!AddOns.PremiumActive(user, now))
            throw new CodedRuleException("The AI designer is an add-on to Premium. Get Premium with it.", "designer_ai_needs_premium");
        if (AddOns.DesignerAiActive(user, now))
            throw new CodedRuleException("The AI designer is already on.", "designer_ai_already_on");
        if (await StoreOfAsync(user, now, ct) is not null)
            throw new CodedRuleException("Your Premium is billed by the app store. Add the AI designer in the app.", "designer_ai_store");
    }

    /// <summary>
    /// Whether someone with this access to an event may buy this item for it. A pass, another year of one,
    /// and keeping the photos: anyone the event is for (a celebrant, read-only or not), whoever runs it
    /// (a venue's owner or staff) and its organiser; so a couple can keep their photos when the venue's plan
    /// ends (owner, 2026-11-12). Emailed invitations: the organiser only, as before. Checkout and both stores
    /// ask the same question.
    /// </summary>
    public static bool MayBuy(PaymentKind kind, CampaignAccess access) =>
        kind == PaymentKind.Sending ? access == CampaignAccess.Organiser : access >= CampaignAccess.Celebrant;

    /// <summary>Whether this event can have this item now. Checkout and a store purchase ask the same question.</summary>
    public static void EnsureEventCanHave(PaymentKind kind, Campaign campaign, DateTimeOffset now)
    {
        if (campaign.Status == CampaignStatus.Cancelled)
            throw new BusinessRuleException("That event was cancelled.", "event_cancelled");
        if (kind == PaymentKind.GoldPass && EventPasses.Active(campaign, now) == EventPassKind.Platinum)
            throw new BusinessRuleException("That event already has a Platinum pass.", "pass_already_bigger");
        if (kind is PaymentKind.GoldExtension or PaymentKind.PlatinumExtension
            && campaign.EventPass != (kind == PaymentKind.PlatinumExtension ? EventPassKind.Platinum : EventPassKind.Gold))
            throw new BusinessRuleException("Extending is for the pass the event already has.", "extension_needs_pass");
        // A free event (one that never had a pass) is kept for good, so there is nothing to keep.
        if (kind == PaymentKind.KeepPhotos && campaign.EventPass == EventPassKind.None)
            throw new BusinessRuleException("The photos from a free event are kept for good already.", "keep_not_needed");
    }

    /// <summary>Whether this account can have this plan now. Checkout and a store purchase ask the same question.</summary>
    public static void EnsureAccountCanHave(PaymentKind kind, AppUser user, DateTimeOffset now)
    {
        var active = PlanRules.IsActive(user.SubscriptionTier, user.SubscriptionEndsAt, now);
        if (!active || TierOf(kind) is not { } wanted || wanted == user.SubscriptionTier) return;
        // Premium, Photographer and Studio are one at a time; changing between them is done by us (admin), as
        // Premium <-> Studio always was. Renewing the plan one is on is fine.
        // Buying Premium with or without the AI designer is the same plan (Premium), so it falls through above.
        throw user.SubscriptionTier switch
        {
            SubscriptionTier.Venue => new BusinessRuleException("This account is on Studio. Talk to us about changing it.", "billing_venue_account"),
            SubscriptionTier.Photographer => new BusinessRuleException("This account is on Photographer. Talk to us about changing it.", "billing_photographer_account"),
            _ => new BusinessRuleException("This account is on Premium. Talk to us about changing it.", "billing_premium_account"),
        };
    }

    public async Task FulfilAsync(Guid paymentId, CancellationToken ct = default)
    {
        var payment = await payments.Query(tracking: true).FirstOrDefaultAsync(x => x.Id == paymentId, ct);
        if (payment is null || payment.Status != PaymentStatus.Paid || payment.FulfilledAt is not null) return;
        var now = DateTimeOffset.UtcNow;

        var campaign = payment.CampaignId is { } cid
            ? await campaigns.Query(tracking: true).FirstOrDefaultAsync(c => c.Id == cid, ct)
            : null;
        var raiseWindows = false;

        switch (payment.Kind)
        {
            case PaymentKind.GoldPass:
            case PaymentKind.PlatinumPass:
                if (campaign is null) break;
                EventPasses.Apply(campaign, payment.Kind == PaymentKind.PlatinumPass ? EventPassKind.Platinum : EventPassKind.Gold, now);
                raiseWindows = true;
                break;
            case PaymentKind.GoldExtension:
            case PaymentKind.PlatinumExtension:
                if (campaign is null) break;
                raiseWindows = EventPasses.Extend(campaign, now);
                break;
            case PaymentKind.KeepPhotos:
                if (campaign is null) break;
                // A year more from whenever the photos would otherwise start to lapse (as an admin gives it).
                var cover = (await plans.ForCampaignAsync(campaign.Id, ct)).CoveredUntil ?? now;
                var from = cover > now ? cover : now;
                campaign.KeepPhotosUntil = from.AddMonths(PlanCatalog.KeepPhotosMonths * payment.Quantity);
                break;
            case PaymentKind.Sending:
                if (campaign is null) break;
                campaign.PaidInviteCapacity += payment.Quantity * PricingCalculator.BlockSize;
                break;
            case PaymentKind.DesignerAiAdd:
            {
                if (payment.UserId is not { } adder) break;
                var account = await users.Query(tracking: true).FirstOrDefaultAsync(u => u.Id == adder, ct);
                if (account is null) break;
                // For the rest of the paid month (a month when Premium has no end), and with every card renewal
                // after it while Premium renews by card.
                var renewsByCard = account.AutoRenewKind is not null && account.SubscriptionEndsAt is not null;
                AddOns.Set(account, AddOns.DesignerAi, account.SubscriptionEndsAt ?? now.AddMonths(1), renewsByCard, AddOnSource(payment));
                break;
            }
            case PaymentKind.PremiumMonthly:
            case PaymentKind.PremiumAiMonthly:
            case PaymentKind.StudioYearly:
            case PaymentKind.VenueMonthly:
            case PaymentKind.PhotographerMonthly:
                if (payment.UserId is not { } buyer) break;
                var user = await users.Query(tracking: true).FirstOrDefaultAsync(u => u.Id == buyer, ct);
                if (user is null) break;
                var tier = TierOf(payment.Kind) ?? SubscriptionTier.Premium;
                var running = user.SubscriptionTier == tier
                              && PlanRules.IsActive(user.SubscriptionTier, user.SubscriptionEndsAt, now);
                // A plan given with no end date stays open-ended; otherwise the new period follows the old.
                if (!(running && user.SubscriptionEndsAt is null))
                {
                    var start = running && user.SubscriptionEndsAt > now ? user.SubscriptionEndsAt!.Value : now;
                    user.SubscriptionEndsAt = payment.PeriodEndsAt is { } storeEnd
                        // The store decides when its period ends. Time already paid for elsewhere is kept.
                        ? (running && user.SubscriptionEndsAt > storeEnd ? user.SubscriptionEndsAt : storeEnd)
                        : payment.Kind == PaymentKind.StudioYearly ? start.AddYears(1) : start.AddMonths(1);
                }
                // The store renews it from here on, so a card saved earlier must not be charged as well.
                if (payment.PeriodEndsAt is not null) user.AutoRenewKind = null;
                user.SubscriptionTier = tier;
                // Bought with the card saved (or renewed with it): it goes on renewing the same way. The AI
                // designer renews as an add-on to Premium (below), so the plan itself renews as Premium.
                if (payment.AutoRenew)
                {
                    user.AutoRenewKind = payment.Kind == PaymentKind.PremiumAiMonthly ? PaymentKind.PremiumMonthly : payment.Kind;
                    user.RenewalFailures = 0;
                }
                // With the AI designer: the add-on runs to the same end. A store subscription's own sync then
                // records whether it renews with it; a card renews it while the card renews Premium.
                if (payment.Kind == PaymentKind.PremiumAiMonthly)
                    AddOns.Set(user, AddOns.DesignerAi, user.SubscriptionEndsAt,
                        payment.PeriodEndsAt is not null || payment.AutoRenew, AddOnSource(payment));
                break;
        }

        payment.FulfilledAt = now;
        if (campaign is not null) campaign.UpdatedAt = now;
        await auditLogs.AddAsync(new AuditLog
        {
            Id = Guid.NewGuid(),
            Action = "billing.fulfil",
            Actor = payment.UserId?.ToString() ?? "gateway",
            CampaignId = payment.CampaignId,
            DataJson = JsonSerializer.Serialize(new { payment = payment.Id, kind = payment.Kind.ToString(), payment.Quantity, payment.Amount }),
            CreatedAt = now,
        }, ct);
        await uow.SaveChangesAsync(ct);

        if (raiseWindows && campaign is not null) await buckets.RaiseWindowsToPlanAsync(campaign.Id, ct);
    }

    private static string AddOnSource(Payment payment) => payment.Provider switch
    {
        PaymentProviders.GooglePlay => AddOns.Sources.GooglePlay,
        PaymentProviders.AppStore => AddOns.Sources.AppStore,
        _ => AddOns.Sources.Bml,
    };

    public async Task<PaymentStatusDto> PaymentStatusAsync(Guid paymentId, CancellationToken ct = default)
    {
        var me = RequireUser();
        var payment = await payments.Query().FirstOrDefaultAsync(x => x.Id == paymentId && x.UserId == me, ct)
                      ?? throw new NotFoundException("That payment isn't on this account.");

        if (payment.Status is PaymentStatus.Pending or PaymentStatus.Created && paymentService is not null)
        {
            var synced = await paymentService.SyncAsync(paymentId, ct);
            if (synced.FulfilPaymentId is { } paid) await FulfilAsync(paid, ct);
            payment = await payments.Query().FirstAsync(x => x.Id == paymentId, ct);
        }

        var status = payment.Status switch
        {
            PaymentStatus.Paid or PaymentStatus.Refunded or PaymentStatus.PartiallyRefunded => "Paid",
            PaymentStatus.Failed => "Failed",
            _ => "Pending",
        };
        return new PaymentStatusDto(payment.Id, ItemName(payment.Kind), payment.Description ?? ItemName(payment.Kind),
            payment.Amount, payment.Currency, status);
    }

    public async Task<BillingAccountDto> StopAutoRenewAsync(CancellationToken ct = default)
    {
        var me = RequireUser();
        var user = await users.Query(tracking: true).FirstOrDefaultAsync(u => u.Id == me, ct)
                   ?? throw new NotFoundException("That account no longer exists.");
        var now = DateTimeOffset.UtcNow;
        if (user.AutoRenewKind is not null)
        {
            user.AutoRenewKind = null;
            await auditLogs.AddAsync(new AuditLog
            {
                Id = Guid.NewGuid(), Action = "billing.autorenew.off", Actor = me.ToString(), CreatedAt = now,
                DataJson = JsonSerializer.Serialize(new { tier = user.SubscriptionTier.ToString(), user.SubscriptionEndsAt }),
            }, ct);
            await uow.SaveChangesAsync(ct);
        }
        return await AccountWithStoreAsync(user, now, ct);
    }

    public async Task<BillingAccountDto> SetDesignerAiRenewalAsync(bool renew, CancellationToken ct = default)
    {
        var me = RequireUser();
        var user = await users.Query(tracking: true).FirstOrDefaultAsync(u => u.Id == me, ct)
                   ?? throw new NotFoundException("That account no longer exists.");
        var now = DateTimeOffset.UtcNow;
        if (!AddOns.DesignerAiActive(user, now))
            throw new CodedRuleException(renew
                    ? "The AI designer isn't on. Add it from Billing."
                    : "The AI designer isn't on.", "designer_ai_not_on");
        if (await StoreOfAsync(user, now, ct) is not null)
            throw new CodedRuleException("Your Premium is billed by the app store. Change the AI designer in the app.", "designer_ai_store");
        var was = AddOns.Find(user, AddOns.DesignerAi)!.Renews;
        if (was != renew)
        {
            AddOns.SetRenews(user, AddOns.DesignerAi, renew);
            await auditLogs.AddAsync(new AuditLog
            {
                Id = Guid.NewGuid(), Action = renew ? "billing.designer_ai.on" : "billing.designer_ai.off", Actor = me.ToString(), CreatedAt = now,
                DataJson = JsonSerializer.Serialize(new { endsAt = AddOns.Find(user, AddOns.DesignerAi)!.EndsAt }),
            }, ct);
            await uow.SaveChangesAsync(ct);
        }
        return await AccountWithStoreAsync(user, now, ct);
    }

    /// <summary>The store whose subscription the account's plan runs on, with whether it renews; null for none.</summary>
    private async Task<(string Store, bool AutoRenew)?> StoreOfAsync(AppUser user, DateTimeOffset now, CancellationToken ct)
    {
        if (playPurchases is not null)
        {
            var store = await playPurchases.Query()
                .Where(p => p.UserId == user.Id && p.CampaignId == null && p.FulfilledAt != null && p.VoidedAt == null && p.ExpiresAt > now)
                .OrderByDescending(p => p.ExpiresAt)
                .Select(p => new { p.AutoRenewing })
                .FirstOrDefaultAsync(ct);
            if (store is not null) return (PaymentProviders.GooglePlay, store.AutoRenewing);
        }
        if (appStorePurchases is not null)
        {
            var store = await appStorePurchases.Query()
                .Where(p => p.UserId == user.Id && p.CampaignId == null && p.FulfilledAt != null && p.VoidedAt == null && p.ExpiresAt > now)
                .OrderByDescending(p => p.ExpiresAt)
                .Select(p => new { p.AutoRenewing })
                .FirstOrDefaultAsync(ct);
            if (store is not null) return (PaymentProviders.AppStore, store.AutoRenewing);
        }
        return null;
    }

    private async Task<BillingAccountDto> AccountWithStoreAsync(AppUser user, DateTimeOffset now, CancellationToken ct)
    {
        var account = Account(user, now);
        if (!account.Active) return account;
        return await StoreOfAsync(user, now, ct) is { } store
            ? account with { Store = store.Store, StoreAutoRenew = store.AutoRenew }
            : account;
    }

    private static BillingAccountDto Account(AppUser user, DateTimeOffset now)
    {
        var active = PlanRules.IsActive(user.SubscriptionTier, user.SubscriptionEndsAt, now);
        return new BillingAccountDto(user.SubscriptionTier.ToString(), user.SubscriptionEndsAt, active,
            active && user.AutoRenewKind is { } k ? ItemName(k) : null,
            AddOns: AddOns.ForAccount(user, now), CanDownloadAll: DownloadRules.AccountAllows(user, now));
    }

    public static bool IsSubscription(PaymentKind kind) =>
        kind is PaymentKind.PremiumMonthly or PaymentKind.PremiumAiMonthly or PaymentKind.VenueMonthly or PaymentKind.PhotographerMonthly;

    /// <summary>The account plan a subscription item gives; null for everything else.</summary>
    public static SubscriptionTier? TierOf(PaymentKind kind) => kind switch
    {
        PaymentKind.PremiumMonthly or PaymentKind.PremiumAiMonthly or PaymentKind.StudioYearly => SubscriptionTier.Premium,
        PaymentKind.VenueMonthly => SubscriptionTier.Venue,
        PaymentKind.PhotographerMonthly => SubscriptionTier.Photographer,
        _ => null,
    };

    private Guid RequireUser() => currentUser.UserId ?? throw new ForbiddenException("Sign in to see your billing.");

    private static bool IsEventItem(PaymentKind kind) =>
        kind is PaymentKind.GoldPass or PaymentKind.PlatinumPass or PaymentKind.GoldExtension or PaymentKind.PlatinumExtension
            or PaymentKind.KeepPhotos or PaymentKind.Sending;

    /// <summary>
    /// A billing key to what it buys. The keys from before Party and Wedding became Gold and Platinum
    /// are still accepted: a tab open during the deploy, or an old client, may post them.
    /// </summary>
    public static PaymentKind ParseItem(string? item) => (item ?? "").Trim().ToLowerInvariant() switch
    {
        "gold-pass" or "party-pass" => PaymentKind.GoldPass,
        "platinum-pass" or "wedding-pass" => PaymentKind.PlatinumPass,
        "keep-photos" => PaymentKind.KeepPhotos,
        "sending" => PaymentKind.Sending,
        "premium-monthly" => PaymentKind.PremiumMonthly,
        "premium-ai-monthly" => PaymentKind.PremiumAiMonthly,
        "designer-ai-add" => PaymentKind.DesignerAiAdd,
        "venue-monthly" => PaymentKind.VenueMonthly,
        "photographer-monthly" => PaymentKind.PhotographerMonthly,
        "gold-extension" or "party-extension" => PaymentKind.GoldExtension,
        "platinum-extension" or "wedding-extension" => PaymentKind.PlatinumExtension,
        _ => throw new BusinessRuleException("That can't be bought here.", "billing_item_unknown"),
    };

    public static string ItemName(PaymentKind kind) => kind switch
    {
        PaymentKind.GoldPass => "gold-pass",
        PaymentKind.PlatinumPass => "platinum-pass",
        PaymentKind.KeepPhotos => "keep-photos",
        PaymentKind.Sending or PaymentKind.Initial or PaymentKind.TopUp => "sending",
        PaymentKind.PremiumMonthly => "premium-monthly",
        PaymentKind.PremiumAiMonthly => "premium-ai-monthly",
        PaymentKind.DesignerAiAdd => "designer-ai-add",
        PaymentKind.StudioYearly => "studio-yearly",
        PaymentKind.VenueMonthly => "venue-monthly",
        PaymentKind.PhotographerMonthly => "photographer-monthly",
        PaymentKind.GoldExtension => "gold-extension",
        PaymentKind.PlatinumExtension => "platinum-extension",
        PaymentKind.StudioGoldCredits => "studio-gold-credits",
        PaymentKind.StudioPlatinumCredits => "studio-platinum-credits",
        _ => "other",
    };

    public static string Describe(PaymentKind kind, int quantity, Campaign? c)
    {
        var on = c is null ? "" : $" · {c.Title}";
        return kind switch
        {
            PaymentKind.GoldPass => $"Gold pass{on}",
            PaymentKind.PlatinumPass => $"Platinum pass{on}",
            PaymentKind.GoldExtension => $"Gold pass, another year{on}",
            PaymentKind.PlatinumExtension => $"Platinum pass, another year{on}",
            PaymentKind.KeepPhotos => $"Keep your photos, a year{on}",
            PaymentKind.Sending => $"{quantity * PricingCalculator.BlockSize} emailed invitations{on}",
            PaymentKind.PremiumMonthly => "Premium, a month",
            PaymentKind.PremiumAiMonthly => "Premium with the AI designer, a month",
            PaymentKind.DesignerAiAdd => "AI designer, the rest of this month",
            // StudioYearly was Premium's yearly payment when Premium was called Studio: not today's Studio.
            PaymentKind.StudioYearly => "Premium, a year",
            PaymentKind.VenueMonthly => "Studio, a month",
            PaymentKind.PhotographerMonthly => "Photographer, a month",
            PaymentKind.StudioGoldCredits => $"{quantity} Gold pass{(quantity == 1 ? "" : "es")} for clients",
            PaymentKind.StudioPlatinumCredits => $"{quantity} Platinum pass{(quantity == 1 ? "" : "es")} for clients",
            _ => kind.ToString(),
        };
    }

    private static string InquireTopic(PaymentKind kind) => kind switch
    {
        PaymentKind.GoldPass or PaymentKind.GoldExtension => "gold",
        PaymentKind.PlatinumPass or PaymentKind.PlatinumExtension => "platinum",
        PaymentKind.KeepPhotos => "keep",
        PaymentKind.Sending => "sending",
        PaymentKind.PremiumMonthly or PaymentKind.PremiumAiMonthly or PaymentKind.DesignerAiAdd => "premium",
        _ => "venue",
    };
}
