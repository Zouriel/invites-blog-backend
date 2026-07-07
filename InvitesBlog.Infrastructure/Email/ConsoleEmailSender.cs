using InvitesBlog.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Email;

/// <summary>
/// Dev email sender — logs the message instead of hitting a provider (Email__Provider=Console).
/// Swap for a Resend/SES sender in production by implementing <see cref="IEmailSender"/>.
/// </summary>
public sealed class ConsoleEmailSender(ILogger<ConsoleEmailSender> logger) : IEmailSender
{
    public Task<DeliveryResult> SendAsync(string to, string subject, string htmlBody, CancellationToken ct)
    {
        logger.LogInformation("📧 EMAIL → {To} | {Subject}\n{Body}", to, subject, htmlBody);
        return Task.FromResult(DeliveryResult.Ok($"console-{Guid.NewGuid():N}"));
    }
}
