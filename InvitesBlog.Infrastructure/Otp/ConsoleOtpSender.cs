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

/// <summary>Dev email OTP sender — delegates to the email sender.</summary>
public sealed class EmailOtpSender(IEmailSender email) : IOtpSender
{
    public string Channel => "email";
    public async Task<DeliveryResult> SendCodeAsync(string recipient, string code, CancellationToken ct)
    {
        var html = $"<p>Your invites.blog verification code is:</p><h2 style='letter-spacing:4px'>{code}</h2><p>It expires in 5 minutes.</p>";
        return await email.SendAsync(recipient, "Your invites.blog code", html, ct);
    }
}
