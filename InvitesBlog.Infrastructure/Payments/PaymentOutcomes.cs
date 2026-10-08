using InvitesBlog.Application.Dtos.Payments;
using InvitesBlog.Application.Services.Billing;
using InvitesBlog.Infrastructure.Delivery;

namespace InvitesBlog.Infrastructure.Payments;

/// <summary>
/// What a recorded payment result leads to: applying what was bought (a pass, a plan) or sending a
/// campaign's invitations. One place for it, because a result arrives three ways: the webhook, the
/// status sweep that finishes payments whose webhook never came, and a saved-card renewal.
/// </summary>
public sealed class PaymentOutcomes(IBillingService billing, DispatchService dispatch)
{
    public async Task ApplyAsync(WebhookProcessResult result)
    {
        // CancellationToken.None: once money has moved, applying it must not be cut short by a
        // provider disconnecting or the host stopping mid-way. Both steps are idempotent.
        if (result.FulfilPaymentId is Guid paid)
            await billing.FulfilAsync(paid, CancellationToken.None);
        if (result.DispatchCampaignId is Guid campaignId)
            await dispatch.DispatchCampaignAsync(campaignId, CancellationToken.None);
    }
}
