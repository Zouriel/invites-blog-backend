using InvitesBlog.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Otp;

/// <summary>Dev SMS OTP sender — logs the code (never store/log codes in prod, §11.1).</summary>
public sealed class ConsoleSmsOtpSender(ILogger<ConsoleSmsOtpSender> logger) : IOtpSender
{
    public string Channel => "sms";
    public Task<DeliveryResult> SendCodeAsync(string recipient, string code, CancellationToken ct)
    {
        logger.LogInformation("📱 SMS OTP → {Recipient}: {Code}", recipient, code);
        return Task.FromResult(DeliveryResult.Ok($"sms-{Guid.NewGuid():N}"));
    }
}

/// <summary>
/// Email OTP sender — sends the 6-digit code on the System identity (no-reply@), code in the subject
/// so it's visible from the notification (provider guide §2.6). Plain, fast HTML.
/// </summary>
public sealed class EmailOtpSender(IEmailSender email) : IOtpSender
{
    public string Channel => "email";
    public async Task<DeliveryResult> SendCodeAsync(string recipient, string code, CancellationToken ct)
    {
        var html = $"<p>Your invites.blog verification code is:</p><h2 style='letter-spacing:4px'>{code}</h2><p>It expires in 5 minutes.</p>";
        return await email.SendAsync(new EmailMessage(
            To: recipient,
            Subject: $"Your invites.blog code: {code}",
            Html: html,
            Stream: EmailStream.System,
            Tags: new[] { new KeyValuePair<string, string>("kind", "otp") }), ct);
    }
}
