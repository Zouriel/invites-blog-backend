using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Events;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Delivery;

/// <summary>
/// Guest invitations over Viber Business Messages, through Infobip. One TEXT message from the approved
/// sender: the host's message, a button that opens the guest's own /i/{token} link, and the §15.2
/// footer with their "remove my data" link. POST {BaseUrl}/viber/2/messages, <c>Authorization: App key</c>.
///
/// <para>Infobip accepting the message is not delivery: Viber reports later, to the per-message
/// webhook attached here, whether the guest got it. <see cref="InfobipReportHandler"/> applies that
/// report and emails the guest instead when Viber could not reach them.</para>
///
/// <para>Only registered when <c>Infobip:ApiKey</c> is set. <c>Infobip:OnlyTo</c> (comma-separated
/// numbers) limits Viber to those numbers so it can be tested on production before everyone gets it;
/// every other guest is emailed as before.</para>
/// </summary>
public sealed class InfobipViberSender(
    HttpClient http,
    IConfiguration config,
    ILogger<InfobipViberSender> logger) : IInviteDeliveryProvider, IAddressGatedProvider
{
    /// <summary>Viber's limits: 1000 characters of text, 30 for the button's title.</summary>
    public const int MaxText = 1000;

    public string Channel => "viber";

    public bool CanSendTo(string address)
    {
        var only = config["Infobip:OnlyTo"];
        if (string.IsNullOrWhiteSpace(only)) return true;
        var to = Digits(address);
        return only.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries).Any(n => Digits(n) == to);
    }

    public async Task<DeliveryResult> SendAsync(InviteDeliveryMessage m, CancellationToken ct)
    {
        var sender = config["Infobip:ViberSender"];
        if (string.IsNullOrWhiteSpace(sender))
            return DeliveryResult.Fail("Viber sender not configured.");

        var message = new Dictionary<string, object?>
        {
            ["sender"] = sender,
            // Infobip wants the number in international format without the '+'; guests are stored E.164.
            ["destinations"] = new[] { new { to = Digits(m.RecipientAddress) } },
            ["content"] = new
            {
                type = "TEXT",
                text = Text(m),
                button = new { title = m.SaveTheDate is not null ? "See the save the date" : "Open your invitation", action = m.InviteLink }
            },
            ["webhooks"] = new
            {
                delivery = new { url = DeliveryReportUrl() },
                // Read back with the report, so a report can be tied to the invite even without our row.
                callbackData = m.InviteId?.ToString()
            },
        };
        // TRANSACTIONAL needs a Viber-approved template on the sender; left out, Infobip's default applies.
        if (config["Infobip:ViberLabel"] is { Length: > 0 } label)
            message["options"] = new { label };

        try
        {
            using var response = await http.PostAsJsonAsync("viber/2/messages", new { messages = new[] { message } }, ct);
            var body = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Infobip Viber send failed ({Status}): {Body}", (int)response.StatusCode, body);
                return DeliveryResult.Fail($"Viber refused the message ({(int)response.StatusCode}).");
            }

            // { "bulkId": "...", "messages": [ { "messageId": "...", "status": { "groupName": "PENDING" } } ] }
            using var doc = JsonDocument.Parse(body);
            var sent = doc.RootElement.GetProperty("messages")[0];
            var group = sent.TryGetProperty("status", out var st) && st.TryGetProperty("groupName", out var g)
                ? g.GetString() : null;
            if (group is "REJECTED" or "UNDELIVERABLE")
                return DeliveryResult.Fail($"Viber rejected it ({St(st, "name") ?? group}).");
            return DeliveryResult.Ok(sent.GetProperty("messageId").GetString());
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException
                                       or KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException)
        {
            logger.LogWarning(ex, "Infobip Viber send error for invite {InviteId}", m.InviteId);
            return DeliveryResult.Fail("Viber could not be reached.");
        }
    }

    /// <summary>The message, the day for a save the date, and the §15.2 footer — cut to Viber's limit.</summary>
    public static string Text(InviteDeliveryMessage m)
    {
        var lines = new List<string>();
        if (m.SaveTheDate is { } std)
        {
            var day = std.Start.ToOffset(EventDayWindow.Male);
            lines.Add($"Save the date: {std.Title}");
            lines.Add(day.ToString(std.AllDay ? "dddd, d MMMM yyyy" : "dddd, d MMMM yyyy 'at' h:mm tt", CultureInfo.GetCultureInfo("en-GB")) +
                      (string.IsNullOrWhiteSpace(std.Location) ? "" : $" · {std.Location}"));
            lines.Add("");
        }
        var footer = $"\n\nSent via invites.blog on behalf of {m.InviterName}.\nRemove my data: {m.RemovalLink ?? "https://invites.blog/privacy"}";
        var body = string.Join("\n", lines) + m.MessageText.Trim();
        var room = MaxText - footer.Length;
        if (body.Length > room) body = body[..Math.Max(0, room - 1)].TrimEnd() + "…";
        return body + footer;
    }

    private string DeliveryReportUrl()
    {
        var apiBase = (config["Urls:ApiBase"] ?? "https://invites.blog").TrimEnd('/');
        return $"{apiBase}/api/delivery/infobip/webhook?t={Uri.EscapeDataString(config["Infobip:WebhookToken"] ?? "")}";
    }

    private static string? St(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) ? v.GetString() : null;

    private static string Digits(string number) => new(number.Where(char.IsDigit).ToArray());
}
