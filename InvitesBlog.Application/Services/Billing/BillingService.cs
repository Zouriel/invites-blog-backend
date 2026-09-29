using System.Text.Json;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Campaigns;
using InvitesBlog.Application.Common;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Plans;
using InvitesBlog.Application.Pricing;
using InvitesBlog.Application.Services.Campaigns;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace InvitesBlog.Application.Services.Billing;

public sealed record BillingPricesDto(
    decimal PartyPass, decimal WeddingPass, decimal KeepPhotos, decimal SendingPerBlock, int SendingBlockSize,
    decimal StudioMonthly, decimal StudioYearly, int VenueDiscountPercent, int StudioDiscountPercent,
    decimal PartyExtension, decimal WeddingExtension, decimal VenueMonthly);

/// <summary>The account's own plan: None, Studio or Venue.</summary>
public sealed record BillingAccountDto(string Tier, DateTimeOffset? EndsAt, bool Active);

/// <summary>One of the account's events, with everything that can be bought for it.</summary>
/// <param name="Pass">The pass it has or last had (None, Party, Wedding) — what an extension extends.</param>
/// <param name="Offer">What its passes cost this host: with the Studio discount on a design made for them.</param>
/// <param name="IsDraft">Not finished yet: a pass bought now is what it goes out with.</param>
public sealed record BillingEventDto(
    Guid CampaignId, string Title, DateTimeOffset EventStartAt, string Kind, string Plan,
    DateTimeOffset? PassUntil, DateTimeOffset? KeepPhotosUntil, DateTimeOffset? CoveredUntil, string Phase,
    SendingAllowanceDto Sending, bool AtVenue, string Pass, bool PassActive, PassOfferDto Offer, bool IsDraft);

public sealed record BillingPaymentDto(
    Guid Id, string Item, string Description, decimal Amount, string Currency, string Status,
    DateTimeOffset CreatedAt, DateTimeOffset? PaidAt, Guid? CampaignId);

public sealed record BillingOverviewDto(
    bool PaymentsEnabled, string Currency, decimal MvrPerUsd, BillingPricesDto Prices, BillingAccountDto Account,
    IReadOnlyList<BillingEventDto> Events, IReadOnlyList<BillingPaymentDto> Payments);

/// <param name="Item">party-pass, wedding-pass, party-extension, wedding-extension, keep-photos, sending,
/// studio-monthly or studio-yearly.</param>
/// <param name="Quantity">Blocks of emails. 1 for the rest.</param>
/// <param name="ReturnPath">A path on this site to come back to after paying (the plan step); the billing page by default.</param>
public sealed record CheckoutRequest(string Item, Guid? CampaignId, int? Quantity, string? ReturnPath = null);

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
    Task<CheckoutResultDto> CheckoutAsync(CheckoutRequest req, CancellationToken ct = default);

    /// <summary>Applies what a paid payment bought, once. Called after the gateway confirms payment.</summary>
    Task FulfilAsync(Guid paymentId, CancellationToken ct = default);
}

/// <summary>
/// Everything that can be paid for, in one place: per event (a pass, another year of it, keeping the
/// photos, emails) and per account (Studio). Prices come from the price book; a pass on a design a
/// Studio made for this host is discounted automatically (<see cref="IPassOfferService"/>).
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
    IDesignerAccessService designerAccess,
    IRepository<AuditLog> auditLogs,
    IUnitOfWork uow) : IBillingService
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
        var events = new List<BillingEventDto>(mine.Count);
        foreach (var c in mine) events.Add(await DescribeEventAsync(c, now, ct));

        var ids = mine.Select(c => c.Id).ToList();
        var history = await payments.Query()
            .Where(x => x.UserId == me || (x.CampaignId != null && ids.Contains(x.CampaignId.Value)))
            .OrderByDescending(x => x.CreatedAt)
            .Take(MaxListed)
            .ToListAsync(ct);

        return new BillingOverviewDto(
            Enabled, PlanCatalog.Currency, p.MvrPerUsd,
            new BillingPricesDto(p.PartyPass, p.WeddingPass, p.KeepPhotosYearly, p.SendingPerBlock, PricingCalculator.BlockSize,
                p.StudioMonthly, p.StudioYearly, p.VenueDiscountPercent, p.StudioDiscountPercent,
                p.PartyExtension, p.WeddingExtension, p.VenueMonthly),
            new BillingAccountDto(user.SubscriptionTier.ToString(), user.SubscriptionEndsAt,
                PlanRules.IsActive(user.SubscriptionTier, user.SubscriptionEndsAt, now)),
            events,
            history.Select(x => new BillingPaymentDto(x.Id, ItemName(x.Kind), x.Description ?? ItemName(x.Kind),
                x.Amount, x.Currency, x.Status.ToString(), x.CreatedAt, x.PaidAt, x.CampaignId)).ToList());
    }

    public async Task<BillingEventDto> EventAsync(Guid campaignId, CancellationToken ct = default)
    {
        RequireUser();
        if (await ownership.AccessAsync(campaignId, ct) != CampaignAccess.Organiser)
            throw new ForbiddenException("Only the event's host can see what it costs.");
        var c = await campaigns.GetByIdAsync(campaignId, ct) ?? throw new NotFoundException("That event no longer exists.");
        return await DescribeEventAsync(c, DateTimeOffset.UtcNow, ct);
    }

    private async Task<BillingEventDto> DescribeEventAsync(Campaign c, DateTimeOffset now, CancellationToken ct)
    {
        var plan = await plans.ForCampaignAsync(c.Id, ct);
        var pass = EventPasses.Active(c, now);
        return new BillingEventDto(
            c.Id, c.Title, c.EventStartAt, SaveTheDates.Name(c.Kind), plan.Kind.ToString(),
            pass == EventPassKind.None ? null : c.EventPassUntil, c.KeepPhotosUntil, plan.CoveredUntil,
            plan.Phase.ToString(), await allowances.ForCampaignAsync(c.Id, ct), c.VenueId is not null,
            c.EventPass.ToString(), pass != EventPassKind.None, await offers.ForCampaignAsync(c.Id, ct),
            c.Status == CampaignStatus.Draft);
    }

    public async Task<CheckoutResultDto> CheckoutAsync(CheckoutRequest req, CancellationToken ct = default)
    {
        var me = RequireUser();
        var kind = ParseItem(req.Item);
        var p = await priceBook.CurrentAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var quantity = kind == PaymentKind.Sending ? Math.Clamp(req.Quantity ?? 1, 1, 50) : 1;

        Campaign? campaign = null;
        if (IsEventItem(kind))
        {
            if (req.CampaignId is not { } id) throw new BusinessRuleException("Choose which event it's for.", "billing_event_required");
            if (await ownership.AccessAsync(id, ct) != CampaignAccess.Organiser)
                throw new ForbiddenException("Only the event's host can pay for it.");
            campaign = await campaigns.GetByIdAsync(id, ct) ?? throw new NotFoundException("That event no longer exists.");
            if (campaign.Status == CampaignStatus.Cancelled)
                throw new BusinessRuleException("That event was cancelled.", "event_cancelled");
            if (kind == PaymentKind.PartyPass && EventPasses.Active(campaign, now) == EventPassKind.Wedding)
                throw new BusinessRuleException("That event already has a Wedding pass.", "pass_already_bigger");
            if (kind is PaymentKind.PartyExtension or PaymentKind.WeddingExtension
                && campaign.EventPass != (kind == PaymentKind.WeddingExtension ? EventPassKind.Wedding : EventPassKind.Party))
                throw new BusinessRuleException("Extending is for the pass the event already has.", "extension_needs_pass");
        }
        else
        {
            var user = await users.Query().FirstOrDefaultAsync(u => u.Id == me, ct)
                       ?? throw new NotFoundException("That account no longer exists.");
            var active = PlanRules.IsActive(user.SubscriptionTier, user.SubscriptionEndsAt, now);
            if (kind is PaymentKind.StudioMonthly or PaymentKind.StudioYearly
                && active && user.SubscriptionTier == SubscriptionTier.Venue)
                throw new BusinessRuleException("This account is on the Venue plan. Talk to us about changing it.", "billing_venue_account");
            if (kind == PaymentKind.VenueMonthly && active && user.SubscriptionTier == SubscriptionTier.Studio)
                throw new BusinessRuleException("This account is on Studio. Talk to us about changing it.", "billing_studio_account");
        }

        var offer = campaign is null ? null : await offers.ForCampaignAsync(campaign.Id, ct);
        var amount = kind switch
        {
            PaymentKind.PartyPass => offer!.PartyPass,
            PaymentKind.WeddingPass => offer!.WeddingPass,
            PaymentKind.PartyExtension => offer!.PartyExtension,
            PaymentKind.WeddingExtension => offer!.WeddingExtension,
            PaymentKind.KeepPhotos => p.KeepPhotosYearly,
            PaymentKind.Sending => p.SendingPerBlock * quantity,
            PaymentKind.StudioMonthly => p.StudioMonthly,
            PaymentKind.StudioYearly => p.StudioYearly,
            PaymentKind.VenueMonthly => p.VenueMonthly,
            _ => throw new BusinessRuleException("That can't be bought here.", "billing_item_unknown"),
        };
        var description = Describe(kind, quantity, campaign);
        // The discount says where it comes from, on the receipt as on the page.
        if (offer is { VenuePercent: > 0 } v && kind is PaymentKind.PartyPass or PaymentKind.WeddingPass
                or PaymentKind.PartyExtension or PaymentKind.WeddingExtension
            && (v.ByVenue || kind is PaymentKind.PartyExtension or PaymentKind.WeddingExtension))
            description += $" · {v.VenuePercent}% venue price, {v.VenueName}";
        else if (kind is PaymentKind.PartyPass or PaymentKind.WeddingPass && offer is { DiscountPercent: > 0 } o)
            description += $" · {o.DiscountPercent}% off, designed by {o.DesignedBy}";

        if (!Enabled)
            return new CheckoutResultDto(false, null,
                "Online payment is being set up. Ask us and we'll add it for you.", InquireTopic(kind), null);

        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            CampaignId = campaign?.Id,
            UserId = me,
            Kind = kind,
            Quantity = quantity,
            InviteCount = kind == PaymentKind.Sending ? quantity * PricingCalculator.BlockSize : 0,
            Amount = amount,
            Currency = PlanCatalog.Currency,
            Description = description,
            Status = PaymentStatus.Pending,
            Provider = provider.Name,
            CreatedAt = now,
        };
        await payments.AddAsync(payment, ct);
        await uow.SaveChangesAsync(ct);

        var inviterBase = config.InviterBase();
        var back = string.IsNullOrWhiteSpace(req.ReturnPath) || !req.ReturnPath.StartsWith('/') || req.ReturnPath.StartsWith("//")
            ? "/billing" : req.ReturnPath;
        var session = await provider.CreateCheckoutSessionAsync(new CreateCheckoutSessionRequest(
            campaign?.Id ?? Guid.Empty, kind.ToString(), amount, PlanCatalog.Currency, payment.InviteCount,
            $"{inviterBase}{back}{(back.Contains('?') ? '&' : '?')}paid={payment.Id}", $"{inviterBase}{back}"), ct);
        payment.ProviderSessionId = session.SessionId;
        await uow.SaveChangesAsync(ct);

        return new CheckoutResultDto(true, session.CheckoutUrl, null, null, payment.Id);
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
        var studioChanged = false;

        switch (payment.Kind)
        {
            case PaymentKind.PartyPass:
            case PaymentKind.WeddingPass:
                if (campaign is null) break;
                EventPasses.Apply(campaign, payment.Kind == PaymentKind.WeddingPass ? EventPassKind.Wedding : EventPassKind.Party, now);
                raiseWindows = true;
                break;
            case PaymentKind.PartyExtension:
            case PaymentKind.WeddingExtension:
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
            case PaymentKind.StudioMonthly:
            case PaymentKind.StudioYearly:
            case PaymentKind.VenueMonthly:
                if (payment.UserId is not { } buyer) break;
                var user = await users.Query(tracking: true).FirstOrDefaultAsync(u => u.Id == buyer, ct);
                if (user is null) break;
                var tier = payment.Kind == PaymentKind.VenueMonthly ? SubscriptionTier.Venue : SubscriptionTier.Studio;
                var running = user.SubscriptionTier == tier
                              && PlanRules.IsActive(user.SubscriptionTier, user.SubscriptionEndsAt, now);
                // A plan given with no end date stays open-ended; otherwise the new period follows the old.
                if (!(running && user.SubscriptionEndsAt is null))
                {
                    var start = running && user.SubscriptionEndsAt > now ? user.SubscriptionEndsAt!.Value : now;
                    user.SubscriptionEndsAt = payment.Kind == PaymentKind.StudioYearly ? start.AddYears(1) : start.AddMonths(1);
                }
                user.SubscriptionTier = tier;
                studioChanged = true;
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
        if (studioChanged && payment.UserId is { } studioUser) await designerAccess.SyncAsync(studioUser, ct);
    }

    private Guid RequireUser() => currentUser.UserId ?? throw new ForbiddenException("Sign in to see your billing.");

    private static bool IsEventItem(PaymentKind kind) =>
        kind is PaymentKind.PartyPass or PaymentKind.WeddingPass or PaymentKind.PartyExtension or PaymentKind.WeddingExtension
            or PaymentKind.KeepPhotos or PaymentKind.Sending;

    public static PaymentKind ParseItem(string? item) => (item ?? "").Trim().ToLowerInvariant() switch
    {
        "party-pass" => PaymentKind.PartyPass,
        "wedding-pass" => PaymentKind.WeddingPass,
        "keep-photos" => PaymentKind.KeepPhotos,
        "sending" => PaymentKind.Sending,
        "studio-monthly" => PaymentKind.StudioMonthly,
        "studio-yearly" => PaymentKind.StudioYearly,
        "venue-monthly" => PaymentKind.VenueMonthly,
        "party-extension" => PaymentKind.PartyExtension,
        "wedding-extension" => PaymentKind.WeddingExtension,
        _ => throw new BusinessRuleException("That can't be bought here.", "billing_item_unknown"),
    };

    private static string ItemName(PaymentKind kind) => kind switch
    {
        PaymentKind.PartyPass => "party-pass",
        PaymentKind.WeddingPass => "wedding-pass",
        PaymentKind.KeepPhotos => "keep-photos",
        PaymentKind.Sending or PaymentKind.Initial or PaymentKind.TopUp => "sending",
        PaymentKind.StudioMonthly => "studio-monthly",
        PaymentKind.StudioYearly => "studio-yearly",
        PaymentKind.VenueMonthly => "venue-monthly",
        PaymentKind.PartyExtension => "party-extension",
        PaymentKind.WeddingExtension => "wedding-extension",
        PaymentKind.StudioPartyCredits => "studio-party-credits",
        PaymentKind.StudioWeddingCredits => "studio-wedding-credits",
        _ => "other",
    };

    private static string Describe(PaymentKind kind, int quantity, Campaign? c)
    {
        var on = c is null ? "" : $" · {c.Title}";
        return kind switch
        {
            PaymentKind.PartyPass => $"Party pass{on}",
            PaymentKind.WeddingPass => $"Wedding pass{on}",
            PaymentKind.PartyExtension => $"Party pass, another year{on}",
            PaymentKind.WeddingExtension => $"Wedding pass, another year{on}",
            PaymentKind.KeepPhotos => $"Keep your photos, a year{on}",
            PaymentKind.Sending => $"{quantity * PricingCalculator.BlockSize} emailed invitations{on}",
            PaymentKind.StudioMonthly => "Studio, a month",
            PaymentKind.StudioYearly => "Studio, a year",
            PaymentKind.VenueMonthly => "Venue, a month",
            PaymentKind.StudioPartyCredits => $"{quantity} Party pass{(quantity == 1 ? "" : "es")} for clients",
            PaymentKind.StudioWeddingCredits => $"{quantity} Wedding pass{(quantity == 1 ? "" : "es")} for clients",
            _ => kind.ToString(),
        };
    }

    private static string InquireTopic(PaymentKind kind) => kind switch
    {
        PaymentKind.PartyPass or PaymentKind.PartyExtension => "party",
        PaymentKind.WeddingPass or PaymentKind.WeddingExtension => "wedding",
        PaymentKind.KeepPhotos => "keep",
        PaymentKind.Sending => "sending",
        PaymentKind.StudioMonthly or PaymentKind.StudioYearly => "studio",
        PaymentKind.VenueMonthly => "venue",
        _ => "studio-passes",
    };
}
