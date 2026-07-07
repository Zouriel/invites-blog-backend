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
        var html =
            $"<p>{System.Net.WebUtility.HtmlEncode(m.MessageText)}</p>" +
            $"<p><a href=\"{m.InviteLink}\" style=\"background:#8a6d1a;color:#fff;padding:12px 20px;border-radius:8px;text-decoration:none\">Open Invite</a></p>" +
            $"<hr><p style=\"font-size:12px;color:#888\">Sent via invites.blog on behalf of {System.Net.WebUtility.HtmlEncode(m.InviterName)} · " +
            "<a href=\"https://invites.blog/privacy\">Privacy</a> · Remove my data</p>"; // §15.2 footer
        return await email.SendAsync(m.RecipientAddress, subject, html, ct);
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
