using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Push;
using InvitesBlog.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Infrastructure.Notifications.Push;

/// <summary>
/// Drops a notification whose kind the account muted in the app (<see cref="AppUser.PushMuted"/>), before any
/// push service is called. Sits in front of every sender, so every kind of notification, from the queue or a
/// sweep, honours the same switches. Kinds that can't be muted (<see cref="PushKinds.MuteGroup"/> null) and
/// pushes with no kind always go.
/// </summary>
public sealed class MutingPushSender(IPushSender inner, IRepository<AppUser> users) : IPushSender
{
    public async Task SendAsync(
        Guid userId, string title, string body, IReadOnlyDictionary<string, string>? data, CancellationToken ct)
    {
        var kind = data is not null && data.TryGetValue(PushKinds.KindKey, out var k) ? k : null;
        if (PushKinds.MuteGroup(kind) is { } group)
        {
            var muted = await users.Query().Where(u => u.Id == userId).Select(u => u.PushMuted).FirstOrDefaultAsync(ct);
            if (muted is not null && muted.Contains(group)) return;
        }
        await inner.SendAsync(userId, title, body, data, ct);
    }
}
