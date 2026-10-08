using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Common;
using InvitesBlog.Application.Plans;
using InvitesBlog.Application.Services.Payments;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Application.Services.Billing;

public interface ISubscriptionRenewalService
{
    /// <summary>Renews every plan that is due, by charging its saved card. Returns how many were tried.</summary>
    Task<int> RunOnceAsync(DateTimeOffset now, CancellationToken ct = default);
}

/// <summary>
/// The Premium pass and Venue renewing by themselves. A plan bought with its card saved
/// (<see cref="AppUser.AutoRenewKind"/>) is charged again from a day before it ends, at the price
/// book's price then, and extended exactly as a payment made by hand would extend it
/// (<see cref="IBillingService.FulfilAsync"/>).
///
/// <para>A charge that fails is tried again a day later; after three failures in a row automatic
/// renewal turns off and the plan runs to the end of what was paid, like one never set to renew. The
/// account is emailed at each step, and a receipt when it renews.</para>
///
/// <para>Never two charges for one renewal: an account with a renewal still pending at the gateway
/// isn't charged again; that one is finished by its webhook or the status sweep.</para>
/// </summary>
public sealed class SubscriptionRenewalService(
    IRepository<AppUser> users,
    IPaymentRepository payments,
    IPriceBook priceBook,
    IPaymentProvider provider,
    IRecurringPaymentProvider recurring,
    IPaymentService paymentService,
    IBillingService billing,
    IEmailSender email,
    IConfiguration config,
    IUnitOfWork uow,
    ILogger<SubscriptionRenewalService> logger) : ISubscriptionRenewalService
{
    /// <summary>How long before the plan ends the first try is made.</summary>
    public static readonly TimeSpan Lead = TimeSpan.FromDays(1);
    /// <summary>Between tries after one fails.</summary>
    public static readonly TimeSpan RetryAfter = TimeSpan.FromHours(20);
    /// <summary>A plan lapsed longer than this isn't brought back by itself.</summary>
    public static readonly TimeSpan GiveUpAfter = TimeSpan.FromDays(7);
    public const int MaxFailures = 3;

    private string BillingLink => $"{config.InviterBase()}/billing";

    public async Task<int> RunOnceAsync(DateTimeOffset now, CancellationToken ct = default)
    {
        var dueBy = now + Lead;
        var lapsedSince = now - GiveUpAfter;
        var retryBefore = now - RetryAfter;
        var due = await users.Query(tracking: true)
            .Where(u => u.AutoRenewKind != null && u.SubscriptionEndsAt != null
                        && u.SubscriptionEndsAt <= dueBy && u.SubscriptionEndsAt > lapsedSince
                        && (u.RenewalLastTriedAt == null || u.RenewalLastTriedAt < retryBefore))
            .ToListAsync(ct);

        var tried = 0;
        foreach (var user in due)
        {
            try
            {
                if (await RenewAsync(user, now, ct)) tried++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One account's failure must not stop the rest.
                logger.LogError(ex, "Renewal failed for account {UserId}.", user.Id);
            }
        }
        return tried;
    }

    private async Task<bool> RenewAsync(AppUser user, DateTimeOffset now, CancellationToken ct)
    {
        var kind = user.AutoRenewKind!.Value;
        // Studio's year is retired: Studio became Premium, which renews by the month.
        if (kind == PaymentKind.StudioYearly) kind = PaymentKind.PremiumMonthly;
        var tier = kind == PaymentKind.VenueMonthly ? SubscriptionTier.Venue : SubscriptionTier.Premium;
        // The plan was changed by hand since (an admin moved it): renewing the old one would be wrong.
        if (user.SubscriptionTier != tier)
        {
            user.AutoRenewKind = null;
            await uow.SaveChangesAsync(ct);
            return false;
        }

        // A renewal already waiting at the gateway is finished there, not charged a second time.
        var pendingSince = now - GiveUpAfter;
        if (await payments.Query().AnyAsync(p => p.UserId == user.Id && p.AutoRenew && p.Kind == kind
                && p.Status == PaymentStatus.Pending && p.CreatedAt > pendingSince, ct))
            return false;

        var plan = PlanName(kind);
        user.RenewalLastTriedAt = now;

        if (user.PaymentCustomerId is not { } customer || !await recurring.HasSavedCardAsync(customer, ct))
        {
            user.AutoRenewKind = null;
            await uow.SaveChangesAsync(ct);
            if (user.Email is { } to) await email.SendAsync(RenewalEmails.NoCard(to, plan, BillingLink), ct);
            logger.LogInformation("Account {UserId} has no saved card; automatic renewal is off.", user.Id);
            return true;
        }

        var p = await priceBook.CurrentAsync(ct);
        var price = kind switch
        {
            PaymentKind.VenueMonthly => p.VenueMonthly,
            _ => p.PremiumMonthly,
        };
        var (amount, currency) = ChargeCurrency.For(config, price, p.MvrPerUsd);
        var payment = new Payment
        {
            Id = Guid.NewGuid(),
            UserId = user.Id,
            Kind = kind,
            Quantity = 1,
            Amount = amount,
            Currency = currency,
            Description = $"{BillingService.Describe(kind, 1, null)} · renewed automatically",
            Status = PaymentStatus.Pending,
            Provider = provider.Name,
            AutoRenew = true,
            CreatedAt = now,
        };
        await payments.AddAsync(payment, ct);
        await uow.SaveChangesAsync(ct);

        PaymentWebhookResult result;
        try
        {
            result = await recurring.ChargeSavedCardAsync(
                new SavedCardCharge(customer, amount, currency, payment.Id, payment.Description), ct);
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning(ex, "Charging the saved card of account {UserId} failed.", user.Id);
            payment.Status = PaymentStatus.Failed;
            await FailedAsync(user, plan, ct);
            return true;
        }

        payment.ProviderSessionId = result.ProviderSessionId;
        await uow.SaveChangesAsync(ct);

        var applied = await paymentService.ApplyAsync(result, ct);
        if (applied.FulfilPaymentId is { } paid)
        {
            await billing.FulfilAsync(paid, ct);
            var renewed = await users.Query().FirstAsync(u => u.Id == user.Id, ct);
            if (renewed.Email is { } to)
                await email.SendAsync(RenewalEmails.Renewed(to, plan, amount, currency, renewed.SubscriptionEndsAt, BillingLink), ct);
            logger.LogInformation("Renewed {Plan} for account {UserId}.", plan, user.Id);
            return true;
        }

        // Declined, or the bank wants the cardholder to confirm (3-D Secure), which can't happen with
        // nobody there. Either way this try didn't renew it.
        if (result.Kind == WebhookEventKind.PaymentFailed || result.NextActionUrl is not null)
        {
            var stale = await payments.Query(tracking: true).FirstAsync(x => x.Id == payment.Id, ct);
            if (stale.Status == PaymentStatus.Pending) stale.Status = PaymentStatus.Failed;
            await FailedAsync(user, plan, ct);
        }
        // Otherwise still pending at the gateway: its webhook or the status sweep finishes it.
        return true;
    }

    private async Task FailedAsync(AppUser user, string plan, CancellationToken ct)
    {
        user.RenewalFailures++;
        var stopped = user.RenewalFailures >= MaxFailures;
        if (stopped) user.AutoRenewKind = null;
        await uow.SaveChangesAsync(ct);
        if (user.Email is { } to) await email.SendAsync(RenewalEmails.Failed(to, plan, stopped, BillingLink), ct);
        logger.LogInformation("Renewal of {Plan} failed for account {UserId} ({Failures} in a row).", plan, user.Id, user.RenewalFailures);
    }

    private static string PlanName(PaymentKind kind) => kind == PaymentKind.VenueMonthly ? "Venue" : "Premium pass";
}
