using InvitesBlog.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Delivery;

/// <summary>Email delivery channel — MVP priority 1 (§4.8.1). Renders the delivery message as HTML.</summary>
public sealed class EmailInviteDeliveryProvider(IEmailSender email) : IInviteDeliveryProvider
{
    public string Channel => "email";

    public async Task<DeliveryResult> SendAsync(InviteDeliveryMessage m, CancellationToken ct)
    {
        var subject = m.Subject ?? $"You're invited by {m.InviterName}";
        var removal = m.RemovalLink ?? "https://invites.blog/privacy";
        var text = System.Net.WebUtility.HtmlEncode(m.MessageText);
        var host = System.Net.WebUtility.HtmlEncode(m.InviterName);
        var link = System.Net.WebUtility.HtmlEncode(m.InviteLink);

        // A branded, email-client-safe card (table layout + inline styles). Generous spacing around
        // the CTA so the button never crowds the message text.
        var sans = "-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif";
        var html =
            $"<div style=\"margin:0;padding:0;background:#14100c;\">" +
              $"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background:#14100c;padding:32px 12px;\"><tr><td align=\"center\">" +
                $"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"max-width:520px;background:#1c1611;border:1px solid #3a2f1e;border-radius:16px;\">" +
                  $"<tr><td style=\"padding:36px 40px 4px;text-align:center;font-family:Georgia,'Times New Roman',serif;font-size:22px;color:#f4efe6;\">" +
                    $"<span style=\"color:#d8b25a;\">&#10022;</span> invites<span style=\"color:#d8b25a;\">.</span>blog</td></tr>" +
                  $"<tr><td style=\"padding:12px 40px 0;text-align:center;\">" +
                    $"<p style=\"font-family:{sans};font-size:16px;line-height:1.65;color:#e7ddca;margin:14px 0 0;\">{text}</p></td></tr>" +
                  $"<tr><td style=\"padding:30px 40px 34px;text-align:center;\">" +
                    $"<a href=\"{m.InviteLink}\" style=\"display:inline-block;background:#d8b25a;color:#14100c;font-family:{sans};font-size:16px;font-weight:700;text-decoration:none;padding:15px 36px;border-radius:999px;\">Open your invitation</a>" +
                    $"<p style=\"font-family:{sans};font-size:12px;color:#9c8f78;margin:20px 0 0;line-height:1.6;\">Or open this link:<br><a href=\"{m.InviteLink}\" style=\"color:#b9a88a;word-break:break-all;\">{link}</a></p></td></tr>" +
                  $"<tr><td style=\"padding:18px 40px;border-top:1px solid #3a2f1e;text-align:center;font-family:{sans};font-size:12px;color:#8a7d68;line-height:1.7;\">" +
                    $"Sent via invites.blog on behalf of {host}<br>" +
                    $"<a href=\"https://invites.blog/privacy\" style=\"color:#8a7d68;\">Privacy</a> &middot; <a href=\"{removal}\" style=\"color:#8a7d68;\">Remove my data</a></td></tr>" +
                $"</table></td></tr></table></div>"; // §15.2 footer

        var tags = new List<KeyValuePair<string, string>> { new("kind", "invite") };
        if (m.CampaignId is { } cid) tags.Add(new("campaign_id", cid.ToString()));
        if (m.InviteId is { } iid) tags.Add(new("invite_id", iid.ToString()));

        // List-Unsubscribe → the guest's own data-removal link (provider guide §2.3).
        var headers = new Dictionary<string, string> { ["List-Unsubscribe"] = $"<{removal}>" };

        return await email.SendAsync(new EmailMessage(
            To: m.RecipientAddress,
            Subject: subject,
            Html: html,
            Stream: EmailStream.Invites,
            ReplyTo: m.InviterEmail,
            Tags: tags,
            Headers: headers), ct);
    }
}

/// <summary>
/// Placeholder channel for Telegram/WhatsApp/Viber/SMS (post-MVP, §17.3). Logs the message and
/// reports success so the dispatch pipeline and fallback logic (§13.2) can be exercised end-to-end.
/// </summary>
public sealed class LogInviteDeliveryProvider(string channel, ILogger logger) : IInviteDeliveryProvider
{
    public string Channel => channel;
    public Task<DeliveryResult> SendAsync(InviteDeliveryMessage m, CancellationToken ct)
    {
        logger.LogInformation("📨 {Channel} → {To}: {Text} [{Link}]", channel, m.RecipientAddress, m.MessageText, m.InviteLink);
        return Task.FromResult(DeliveryResult.Ok($"{channel}-{Guid.NewGuid():N}"));
    }
}
