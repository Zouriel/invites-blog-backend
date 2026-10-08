using System.Text.Json;
using InvitesBlog.Application.Services.Delivery;
using InvitesBlog.Domain.Enums;
using InvitesBlog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Delivery;

/// <summary>
/// Applies Infobip's Viber delivery reports to our <see cref="Domain.Entities.DeliveryAttempt"/> rows,
/// and emails the guest when Viber could not reach them (not on Viber, blocked, or the message expired).
/// Idempotent: a repeated report changes nothing, and the email fallback fires once.
///
/// <para>Report shape: <c>{ "results": [ { "messageId": "...", "status": { "groupName": "DELIVERED" },
/// "error": { "name": "..." } } ] }</c>. Group names: PENDING, DELIVERED, UNDELIVERABLE, EXPIRED,
/// REJECTED.</para>
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
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("results", out var results) ||
                results.ValueKind != JsonValueKind.Array)
                return;

            foreach (var result in results.EnumerateArray())
                await ApplyOneAsync(result, ct);
        }
    }

    private async Task ApplyOneAsync(JsonElement result, CancellationToken ct)
    {
        var messageId = Str(result, "messageId");
        if (string.IsNullOrWhiteSpace(messageId)) return;
        var group = (result.TryGetProperty("status", out var status) ? Str(status, "groupName") : null)?.ToUpperInvariant();
        var error = result.TryGetProperty("error", out var err) ? Str(err, "name") ?? Str(err, "description") : null;

        var attempt = await db.DeliveryAttempts
            .FirstOrDefaultAsync(a => a.ProviderMessageId == messageId && a.Channel == "viber", ct);
        if (attempt is null)
        {
            logger.LogInformation("Infobip report for unknown messageId {MessageId}, ignored.", messageId);
            return;
        }
        // A final report was already applied (Infobip retries, and can send more than one).
        if (attempt.Status is DeliveryStatus.Delivered or DeliveryStatus.Failed) return;

        if (group is "DELIVERED")
        {
            attempt.Status = DeliveryStatus.Delivered;
            await db.SaveChangesAsync(ct);
            return;
        }
        if (group is not ("UNDELIVERABLE" or "EXPIRED" or "REJECTED")) return; // still on its way

        attempt.Status = DeliveryStatus.Failed;
        attempt.ErrorMessage = $"Viber could not reach them ({error ?? group}).";
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Viber message {MessageId} undeliverable ({Error}).", messageId, error ?? group);

        if (!await dispatch.FallbackToEmailAsync(attempt.InviteId, attempt.Id, ct))
        {
            // No email to fall back to (or a newer send already went out): the dashboard shows it failed.
            var invite = await db.Invites.FirstOrDefaultAsync(i => i.Id == attempt.InviteId, ct);
            var newer = await db.DeliveryAttempts.AnyAsync(
                a => a.InviteId == attempt.InviteId && a.Id != attempt.Id && a.AttemptedAt >= attempt.AttemptedAt, ct);
            if (invite is { Status: InviteStatus.Sent } && !newer)
            {
                invite.Status = InviteStatus.Failed;
                await db.SaveChangesAsync(ct);
            }
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() : null;
}
