using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Security;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Application.Services.Delivery;

public interface IDeliveryEventService
{
    Task ProcessAsync(string eventType, string? providerMessageId, IReadOnlyList<string> recipients, CancellationToken ct = default);
}

/// <summary>
/// Applies Infobip Viber delivery reports (§13.3). Implemented in Infrastructure because it reads the
/// invite/guest graph and re-delivers via email when a Viber message comes back undeliverable.
/// </summary>
public interface IInfobipReportHandler
{
    /// <summary>Parse a raw Infobip delivery-report body and apply every result. Idempotent.</summary>
    Task HandleReportAsync(string rawBody, CancellationToken ct = default);
}

/// <summary>
/// Applies Resend delivery webhook events to our records (provider guide §2.6). Idempotent: a repeated
/// event is a no-op once the attempt already reflects it. Bounces mark the attempt Failed (surfaced in
/// the campaign report); complaints also add the contact to the hashed suppression list (§15.3).
/// </summary>
public sealed class DeliveryEventService(
    IRepository<DeliveryAttempt> attempts,
    ISuppressionRepository suppression,
    IUnitOfWork uow,
    ILogger<DeliveryEventService> logger) : IDeliveryEventService
{
    public async Task ProcessAsync(string eventType, string? providerMessageId, IReadOnlyList<string> recipients, CancellationToken ct = default)
    {
        var status = eventType switch
        {
            "email.delivered" => DeliveryStatus.Delivered,
            "email.bounced" => DeliveryStatus.Failed,
            "email.complained" => DeliveryStatus.Failed,
            _ => (DeliveryStatus?)null
        };
        if (status is null) return; // opened/clicked/sent etc. — analytics only

        var changed = false;

        if (!string.IsNullOrWhiteSpace(providerMessageId))
        {
            var attempt = await attempts.Query(tracking: true)
                .FirstOrDefaultAsync(a => a.ProviderMessageId == providerMessageId, ct);
            if (attempt is not null && attempt.Status != status.Value)
            {
                attempt.Status = status.Value;
                if (eventType != "email.delivered") attempt.ErrorMessage = eventType;
                changed = true;
            }
        }

        // A spam complaint suppresses the contact so no inviter can re-message them (§15.3).
        if (eventType == "email.complained")
        {
            foreach (var recipient in recipients)
            {
                if (string.IsNullOrWhiteSpace(recipient)) continue;
                var hash = TokenService.HashContact(recipient);
                if (await suppression.ExistsByHashAsync(hash, ct)) continue;
                await suppression.AddAsync(new SuppressionEntry
                {
                    Id = Guid.NewGuid(), ContactHash = hash, ContactType = "email", CreatedAt = DateTimeOffset.UtcNow
                }, ct);
                changed = true;
            }
        }

        if (changed)
        {
            await uow.SaveChangesAsync(ct);
            logger.LogInformation("Delivery event {Type} applied (message {Id}).", eventType, providerMessageId);
        }
    }
}
