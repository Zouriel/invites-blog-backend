using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using InvitesBlog.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Otp;

/// <summary>
/// Sends the sign-in code by SMS through MsgOwl (<c>POST https://rest.msgowl.com/messages</c>).
/// <para>
/// Used for OTP only — invitations still go by email, so this stays a small, single-purpose
/// integration. The code itself is never logged: only the provider's message id is, so a delivery
/// can be traced without the log becoming a list of valid codes (§11.1).
/// </para>
/// </summary>
public sealed class MsgOwlSmsOtpSender(
    IHttpClientFactory httpClientFactory,
    IConfiguration config,
    ILogger<MsgOwlSmsOtpSender> logger) : IOtpSender
{
    public const string ConfigSection = "Sms:MsgOwl";

    private string? ApiKey => config[$"{ConfigSection}:ApiKey"];
    // Must match the sender ID registered with MsgOwl EXACTLY — it is case-sensitive, and an
    // unregistered value is refused with 422 "Invalid sender_id" rather than silently substituted
    // ("InvitesBlog" is rejected; "Invitesblog" is the approved one). Alphanumeric sender IDs are
    // also capped at 11 GSM characters with no punctuation, which the old "invites.blog" default
    // (12 chars, and a period) broke on both counts.
    private string SenderId => config[$"{ConfigSection}:SenderId"] is { Length: > 0 } s ? s : "Invitesblog";
    private string Endpoint =>
        config[$"{ConfigSection}:Endpoint"] is { Length: > 0 } e ? e : "https://rest.msgowl.com/messages";

    public string Channel => "sms";

    /// <summary>True once an API key is configured; until then SMS sign-in reports itself unavailable.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(ApiKey);

    public async Task<DeliveryResult> SendCodeAsync(string recipient, string code, CancellationToken ct)
    {
        if (!IsConfigured)
            return DeliveryResult.Fail("SMS is not configured on this server.");

        // MsgOwl's examples use a plain national/international number; E.164 with the leading '+'
        // is accepted, and stripping it keeps us consistent with their samples.
        var to = recipient.Trim().TrimStart('+');

        // Same key OtpService uses to set ExpiresAt — the copy has to match the real expiry.
        var minutes = int.TryParse(config["Otp:ExpiryMinutes"], out var m) ? m : 5;
        var request = new MsgOwlRequest(
            to, SenderId,
            $"{code} is your invites.blog code. It expires in {minutes} minute{(minutes == 1 ? "" : "s")}.");

        try
        {
            using var http = httpClientFactory.CreateClient();
            http.DefaultRequestHeaders.TryAddWithoutValidation("Authorization", $"AccessKey {ApiKey}");
            using var response = await http.PostAsJsonAsync(Endpoint, request, ct);

            if (response.IsSuccessStatusCode)
            {
                var ok = await response.Content.ReadFromJsonAsync<MsgOwlAccepted>(cancellationToken: ct);
                return DeliveryResult.Ok(ok?.Id?.ToString());
            }

            // 429 carries Retry-After; surface it plainly so the caller can tell the user to wait
            // rather than reporting a generic failure they'd only retry into.
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                logger.LogWarning("MsgOwl rate-limited the OTP send (429).");
                return DeliveryResult.Fail("Too many codes requested just now — try again in a minute.");
            }

            var body = await response.Content.ReadAsStringAsync(ct);
            logger.LogError("MsgOwl rejected the OTP send: {Status} {Body}", (int)response.StatusCode, body);
            return DeliveryResult.Fail("We couldn't send the code to that number.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogError(ex, "MsgOwl was unreachable while sending an OTP.");
            return DeliveryResult.Fail("We couldn't reach the SMS service. Please try again.");
        }
    }

    private sealed record MsgOwlRequest(
        [property: JsonPropertyName("recipients")] string Recipients,
        [property: JsonPropertyName("sender_id")] string SenderId,
        [property: JsonPropertyName("body")] string Body);

    private sealed record MsgOwlAccepted(
        [property: JsonPropertyName("id")] long? Id,
        [property: JsonPropertyName("message")] string? Message);
}
