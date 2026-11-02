using InvitesBlog.Application.Plans;
using InvitesBlog.Application.Pricing;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using InvitesBlog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Infrastructure.Payments;

/// <summary>What a store refund (Google Play or the App Store) takes back from an event: only what that payment gave.</summary>
internal static class StoreRefunds
{
    public static async Task TakeBackAsync(AppDbContext db, Payment payment, Campaign campaign, DateTimeOffset now, CancellationToken ct)
    {
        switch (payment.Kind)
        {
            case PaymentKind.GoldPass:
            case PaymentKind.PlatinumPass:
            {
                var voided = payment.Kind == PaymentKind.PlatinumPass ? EventPassKind.Platinum : EventPassKind.Gold;
                var others = await db.Payments
                    .Where(p => p.CampaignId == campaign.Id && p.Id != payment.Id && p.Status == PaymentStatus.Paid
                                && (p.Kind == PaymentKind.GoldPass || p.Kind == PaymentKind.PlatinumPass))
                    .Select(p => p.Kind).ToListAsync(ct);
                // Only what this one gave: a Gold pass refunded under a Platinum pass changes nothing,
                // and one of two the same loses its year.
                if (campaign.EventPass != voided) break;
                if (others.Contains(payment.Kind))
                {
                    if (campaign.EventPassUntil is { } until) campaign.EventPassUntil = until.AddMonths(-PlanCatalog.PassMonths);
                }
                else if (voided == EventPassKind.Platinum && others.Contains(PaymentKind.GoldPass))
                {
                    campaign.EventPass = EventPassKind.Gold;
                }
                else
                {
                    EventPasses.Apply(campaign, EventPassKind.None, now);
                }
                break;
            }
            case PaymentKind.GoldExtension:
            case PaymentKind.PlatinumExtension:
                if (campaign.EventPassUntil is { } end) campaign.EventPassUntil = end.AddMonths(-PlanCatalog.PassMonths);
                break;
            case PaymentKind.KeepPhotos:
                if (campaign.KeepPhotosUntil is { } keep)
                {
                    // Off, as an admin takes it off, unless another year was bought and still stands.
                    var another = await db.Payments.AnyAsync(p => p.CampaignId == campaign.Id && p.Id != payment.Id
                        && p.Status == PaymentStatus.Paid && p.Kind == PaymentKind.KeepPhotos, ct);
                    campaign.KeepPhotosUntil = another ? keep.AddMonths(-PlanCatalog.KeepPhotosMonths) : null;
                }
                break;
            case PaymentKind.Sending:
                campaign.PaidInviteCapacity = Math.Max(0, campaign.PaidInviteCapacity - PricingCalculator.BlockSize);
                break;
        }
        campaign.UpdatedAt = now;
    }
}
