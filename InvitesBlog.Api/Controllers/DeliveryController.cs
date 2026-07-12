using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InvitesBlog.Application.Common;
using InvitesBlog.Application.Services.Delivery;
using InvitesBlog.Infrastructure.Email;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;

namespace InvitesBlog.Api.Controllers;

/// <summary>Provider delivery webhooks (provider guide §2.6). Signature-verified + idempotent.</summary>
[Route("api/delivery")]
public sealed class DeliveryController(
    ResendWebhookVerifier verifier,
    IDeliveryEventService events,
    IInfobipReportHandler infobip,
    IConfiguration config)
    : BaseApiController
{
    /// <summary>POST /api/delivery/resend/webhook — Resend delivery/bounce/complaint events.</summary>
    [HttpPost("resend/webhook")]
    [AllowAnonymous]
    public async Task<IActionResult> ResendWebhook(CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync(ct);

        var svixId = Request.Headers["svix-id"].FirstOrDefault();
        var svixTs = Request.Headers["svix-timestamp"].FirstOrDefault();
        var svixSig = Request.Headers["svix-signature"].FirstOrDefault();
        if (!verifier.Verify(body, svixId, svixTs, svixSig))
            return Unauthorized(ApiResponse<object?>.Fail("Invalid webhook signature."));

        var (type, emailId, recipients) = Parse(body);
        await events.ProcessAsync(type, emailId, recipients, ct);
        return Success(new { received = true });
    }

    /// <summary>
    /// POST /api/delivery/infobip/webhook?t={token} — Infobip pushes per-message Viber delivery
    /// reports here (the provider sets this URL per send). Guarded by a shared secret query token.
    /// </summary>
    [HttpPost("infobip/webhook")]
    [AllowAnonymous]
    public async Task<IActionResult> InfobipWebhook([FromQuery(Name = "t")] string? token, CancellationToken ct)
    {
        var expected = config["Infobip:WebhookToken"] ?? "";
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(expected) ||
            !CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(token), Encoding.UTF8.GetBytes(expected)))
            return Unauthorized(ApiResponse<object?>.Fail("Invalid webhook token."));

        using var reader = new StreamReader(Request.Body);
        var body = await reader.ReadToEndAsync(ct);
        await infobip.HandleReportAsync(body, ct);
        return Success(new { received = true });
    }

    private static (string Type, string? EmailId, IReadOnlyList<string> Recipients) Parse(string body)
    {
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";

        string? emailId = null;
        var recipients = new List<string>();
        if (root.TryGetProperty("data", out var data))
        {
            if (data.TryGetProperty("email_id", out var eid)) emailId = eid.GetString();
            if (data.TryGetProperty("to", out var to))
            {
                if (to.ValueKind == JsonValueKind.Array)
                    recipients.AddRange(to.EnumerateArray().Select(x => x.GetString()).Where(s => s is not null)!);
                else if (to.ValueKind == JsonValueKind.String && to.GetString() is { } single)
                    recipients.Add(single);
            }
        }
        return (type, emailId, recipients);
    }
}
