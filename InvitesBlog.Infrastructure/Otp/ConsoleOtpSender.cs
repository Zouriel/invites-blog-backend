using InvitesBlog.Application.Abstractions;
using InvitesBlog.Infrastructure.Email;
using Microsoft.Extensions.Configuration;
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
/// so it's visible from the notification (provider guide §2.6), and in the shared branded card so it
/// looks like the rest of the product.
/// </summary>
public sealed class EmailOtpSender(IEmailSender email, IConfiguration config) : IOtpSender
{
    public string Channel => "email";

    public async Task<DeliveryResult> SendCodeAsync(string recipient, string code, CancellationToken ct)
    {
        // Same key OtpService uses to set ExpiresAt — the copy has to match the real expiry.
        var minutes = int.TryParse(config["Otp:ExpiryMinutes"], out var m) ? m : 5;

        var body =
            EmailLayout.Heading("Confirm it's you") +
            EmailLayout.Paragraph("Use this code to sign in and see the invitations sent to you.") +
            EmailLayout.CodeBlock(code) +
            EmailLayout.Paragraph($"The code expires in {minutes} minute{(minutes == 1 ? "" : "s")}.", muted: true) +
            EmailLayout.Paragraph("If you didn't ask to sign in, you can ignore this email &mdash; nobody can use the code without it.", muted: true) +
            EmailLayout.EndSpacer();

        return await email.SendAsync(new EmailMessage(
            To: recipient,
            Subject: $"{code} is your invites.blog code",
            Html: EmailLayout.Wrap(body, preheader: $"Your code is {code}. It expires in {minutes} minute{(minutes == 1 ? "" : "s")}."),
            Stream: EmailStream.System,
            // Plain-text part written explicitly: the auto-generated fallback strips tags out of the
            // whole card and reads like noise.
            Text: $"Your invites.blog verification code is {code}.\n\n"
                + $"It expires in {minutes} minute{(minutes == 1 ? "" : "s")}.\n\n"
                + "If you didn't ask to sign in, you can ignore this email.",
            Tags: new[] { new KeyValuePair<string, string>("kind", "otp") }), ct);
    }
}
