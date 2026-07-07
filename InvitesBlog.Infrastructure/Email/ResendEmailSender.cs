using System.Text;
using System.Text.RegularExpressions;
using InvitesBlog.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Email;

/// <summary>
/// Sends transactional email through Resend (provider guide §2). Maps the message stream to the right
/// from-identity (invites@ vs no-reply@), always sends html + text, and forwards tags so webhook
/// events map back to our records. Failures return a failed <see cref="DeliveryResult"/> (never throw)
/// so the dispatch pipeline records the attempt and can fall back / retry.
/// </summary>
public sealed partial class ResendEmailSender(
    ResendClient client, IConfiguration config, ILogger<ResendEmailSender> logger) : IEmailSender
{
    [GeneratedRegex("<[^>]+>")] private static partial Regex HtmlTagRegex();
    [GeneratedRegex("[^A-Za-z0-9_-]")] private static partial Regex TagCharRegex();

    public async Task<DeliveryResult> SendAsync(EmailMessage message, CancellationToken ct)
    {
        var from = message.Stream == EmailStream.Invites
            ? config["Email:From_Invites"] ?? "invites.blog <invites@mail.invites.blog>"
            : config["Email:From_System"] ?? "invites.blog <no-reply@mail.invites.blog>";

        var tags = message.Tags?
            .Select(t => new ResendTag(SanitizeTag(t.Key), SanitizeTag(t.Value)))
            .Where(t => t.Name.Length > 0)
            .ToList();

        var request = new ResendEmailRequest(
            From: from,
            To: new[] { message.To },
            Subject: message.Subject,
            Html: message.Html,
            Text: message.Text ?? ToPlainText(message.Html),
            ReplyTo: message.ReplyTo,
            Tags: tags is { Count: > 0 } ? tags : null,
            Headers: message.Headers is { Count: > 0 } ? message.Headers : null);

        try
        {
            var id = await client.SendAsync(request, ct);
            return DeliveryResult.Ok(id);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Resend send failed to {To} ({Stream})", message.To, message.Stream);
            return DeliveryResult.Fail(ex.Message);
        }
    }

    // Resend tag rule: ASCII letters/numbers/_/- only, ≤256 chars.
    private static string SanitizeTag(string value)
    {
        var cleaned = TagCharRegex().Replace(value ?? "", "_");
        return cleaned.Length > 256 ? cleaned[..256] : cleaned;
    }

    private static string ToPlainText(string html)
    {
        var text = HtmlTagRegex().Replace(html, " ");
        return WebUtilityDecode(text).Trim();
    }

    private static string WebUtilityDecode(string s)
    {
        var decoded = System.Net.WebUtility.HtmlDecode(s);
        var sb = new StringBuilder(decoded.Length);
        var lastSpace = false;
        foreach (var c in decoded)
        {
            var space = char.IsWhiteSpace(c);
            if (space && lastSpace) continue;
            sb.Append(space ? ' ' : c);
            lastSpace = space;
        }
        return sb.ToString();
    }
}
