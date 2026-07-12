using System.Text.Json;
using InvitesBlog.Application.Services.Delivery;
using InvitesBlog.Domain.Enums;
using InvitesBlog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Delivery;

/// <summary>
/// Applies Infobip Viber delivery reports to our <see cref="Domain.Entities.DeliveryAttempt"/> rows
/// and, when a message is undeliverable, auto-falls-back to email (§13.2). Idempotent: duplicate
/// reports are harmless — a delivered attempt is left alone and email fallback only fires once.
///
/// Report shape: { "results": [ { "messageId": "...", "status": { "groupName": "DELIVERED" | ... },
///                                "error": { "name": "..." } } ] }.
/// </summary>
public sealed class InfobipReportHandler(
    AppDbContext db,
    DispatchService dispatch,
    ILogger<InfobipReportHandler> logger) : IInfobipReportHandler
{
    public async Task HandleReportAsync(string rawBody, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(rawBody)) return;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(rawBody); }
        catch (JsonException) { logger.LogWarning("Infobip report: unparseable body."); return; }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array)
                return;

            foreach (var result in results.EnumerateArray())
                await ApplyOneAsync(result, ct);
        }
    }

    private async Task ApplyOneAsync(JsonElement result, CancellationToken ct)
    {
        var messageId = result.TryGetProperty("messageId", out var mid) ? mid.GetString() : null;
        if (string.IsNullOrWhiteSpace(messageId)) return;

        var groupName = result.TryGetProperty("status", out var status) &&
                        status.TryGetProperty("groupName", out var gn)
            ? gn.GetString()?.ToUpperInvariant()
            : null;

        var errorName = result.TryGetProperty("error", out var err) &&
                        err.TryGetProperty("name", out var en)
            ? en.GetString()
            : null;

        var attempt = await db.DeliveryAttempts
            .FirstOrDefaultAsync(a => a.ProviderMessageId == messageId, ct);
        if (attempt is null)
        {
            logger.LogInformation("Infobip report for unknown messageId {MessageId} — ignored.", messageId);
            return;
        }

        // Already terminal-delivered → ignore (duplicate report).
        if (attempt.Status == DeliveryStatus.Delivered) return;

        var delivered = groupName is "DELIVERED";
        var undeliverable = groupName is "REJECTED" or "UNDELIVERABLE" or "EXPIRED";

        if (delivered)
        {
            attempt.Status = DeliveryStatus.Delivered;
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Infobip Viber message {MessageId} delivered.", messageId);
            return;
        }

        if (!undeliverable) return; // PENDING/other transient groups — wait for a terminal report.

        attempt.Status = DeliveryStatus.Failed;
        attempt.ErrorMessage = errorName ?? groupName;
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Infobip Viber message {MessageId} undeliverable ({Error}).", messageId, attempt.ErrorMessage);

        // Async email fallback — no-op if the invite already has a successful attempt or no email.
        var recovered = await dispatch.FallbackToEmailAsync(attempt.InviteId, ct);
        if (recovered)
            logger.LogInformation("Invite {InviteId} recovered via email after Viber failure.", attempt.InviteId);
    }
}
