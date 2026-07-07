namespace InvitesBlog.Application.Exceptions.Payments;

/// <summary>A webhook event could not be verified or is not one we act on (§10.5) — maps to 400.</summary>
public sealed class PaymentWebhookInvalidException()
    : BusinessRuleException("Unhandled or invalid event.", "payment_webhook_invalid");
