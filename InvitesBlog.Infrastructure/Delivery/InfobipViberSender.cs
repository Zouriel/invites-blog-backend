using System.Net.Http.Json;
using System.Text.Json;
using InvitesBlog.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Delivery;

/// <summary>
/// Viber Business Messages delivery channel via Infobip (§4.8.1 priority 3).
/// Sends the rendered invite text (link inline) as a TEXT message from the registered sender.
/// POST {baseUrl}/viber/2/messages, Authorization: App {api-key}. A per-message delivery-report
/// webhook is attached so Infobip pushes the async DELIVERED/UNDELIVERABLE outcome to us (§13.3).
/// </summary>
public sealed class InfobipViberSender(
    HttpClient http,
    IConfiguration config,
    ILogger<InfobipViberSender> logger) : IInviteDeliveryProvider
{
    public string Channel => "viber";

    public async Task<DeliveryResult> SendAsync(InviteDeliveryMessage m, CancellationToken ct)
    {
        var sender = config["Infobip:ViberSender"];
        if (string.IsNullOrWhiteSpace(sender))
            return DeliveryResult.Fail("Viber sender not configured.");

        // Infobip expects international format without '+'; guests are stored E.164 (§4.4.4).
        var to = m.RecipientAddress.TrimStart('+');

        var payload = new
        {
            messages = new[]
            {
                new
                {
                    sender,
                    destinations = new[] { new { to } },
                    content = new
                    {
                        type = "TEXT",
                        // §15.2 transparency footer travels with the message text.
                        text = $"{m.MessageText}\n\nSent via invites.blog on behalf of {m.InviterName}."
                    },
                    // Push delivery reports for THIS message to our endpoint (§13.3).
                    webhooks = new
                    {
                        delivery = new { url = DeliveryReportUrl() }
                    }
                }
            }
        };

        try
        {
            using var response = await http.PostAsJsonAsync("/viber/2/messages", payload, ct);
            var body = await response.Content.ReadAsStringAsync(ct);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Infobip Viber send failed ({Status}): {Body}", (int)response.StatusCode, body);
                return DeliveryResult.Fail($"infobip {(int)response.StatusCode}");
            }

            // Response: { "messages": [ { "messageId": "...", "status": { ... } } ] }
            using var doc = JsonDocument.Parse(body);
            var messageId = doc.RootElement.GetProperty("messages")[0]
                .GetProperty("messageId").GetString();
            return DeliveryResult.Ok(messageId);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            logger.LogWarning(ex, "Infobip Viber send error for invite {InviteId}", m.InviteId);
            return DeliveryResult.Fail(ex.GetType().Name);
        }
    }

    private string DeliveryReportUrl()
    {
        var apiBase = (config["Urls:ApiBase"] ?? "https://invites.blog").TrimEnd('/');
        var token = config["Infobip:WebhookToken"];
        return $"{apiBase}/api/delivery/infobip/webhook?t={token}";
    }
}
