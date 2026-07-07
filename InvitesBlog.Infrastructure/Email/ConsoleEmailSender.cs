using InvitesBlog.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Email;

/// <summary>
/// Dev email sender — logs the message instead of hitting a provider (Email__Provider=Console).
/// The Resend sender (<see cref="ResendEmailSender"/>) replaces it when Email__Provider=Resend.
/// </summary>
public sealed class ConsoleEmailSender(ILogger<ConsoleEmailSender> logger) : IEmailSender
{
    public Task<DeliveryResult> SendAsync(EmailMessage message, CancellationToken ct)
    {
        logger.LogInformation("📧 EMAIL [{Stream}] → {To} | {Subject}\n{Body}",
            message.Stream, message.To, message.Subject, message.Html);
        return Task.FromResult(DeliveryResult.Ok($"console-{Guid.NewGuid():N}"));
    }
}
