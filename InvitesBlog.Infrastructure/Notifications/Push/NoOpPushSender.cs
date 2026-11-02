using InvitesBlog.Application.Abstractions;

namespace InvitesBlog.Infrastructure.Notifications.Push;

/// <summary>Used unless Push:Enabled is true: nothing is sent and nothing is contacted.</summary>
public sealed class NoOpPushSender : IPushSender
{
    public Task SendAsync(
        Guid userId, string title, string body, IReadOnlyDictionary<string, string>? data, CancellationToken ct) =>
        Task.CompletedTask;
}
