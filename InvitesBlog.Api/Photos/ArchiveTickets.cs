using System.Collections.Concurrent;
using System.Security.Cryptography;
using InvitesBlog.Application.Services.Photos;

namespace InvitesBlog.Api.Photos;

/// <summary>
/// Short-lived links to a zip of an event's originals.
///
/// <para>A browser saves a large file well only when it navigates to it: its own download manager
/// streams to disk, shows progress and survives the tab moving on. Fetching the zip from script instead
/// holds the whole archive in the page's memory (an album can be 10 GB) and shows nothing until the
/// end. But a navigation can't carry the session, which lives in a header. So the signed-in page asks
/// for a link (authorized then, with the photos chosen then), and the link alone fetches that archive.</para>
///
/// <para>Held in memory: the API runs as one process, a link is used within minutes, and a restart only
/// means pressing Download again. A link names one archive of photos its holder could already see, and
/// nothing else.</para>
/// </summary>
public sealed class ArchiveTickets(TimeProvider clock)
{
    /// <summary>Long enough to start (and restart) a download, short enough that a leaked link is soon worthless.</summary>
    public static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    /// <summary>A ceiling on links held at once, so a flood of requests can't grow memory without bound.</summary>
    public const int MaxHeld = 5000;

    private readonly ConcurrentDictionary<string, (PhotoArchive Archive, DateTimeOffset Until)> _held = new(StringComparer.Ordinal);

    public string Issue(PhotoArchive archive)
    {
        var now = clock.GetUtcNow();
        if (_held.Count >= MaxHeld / 2) Prune(now);
        if (_held.Count >= MaxHeld) throw new InvitesBlog.Application.Exceptions.BusinessRuleException("Too many downloads are waiting. Try again in a few minutes.", "downloads_busy");
        var ticket = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        _held[ticket] = (archive, now + Lifetime);
        return ticket;
    }

    /// <summary>The archive a link names, or null when it is unknown or has expired.</summary>
    public PhotoArchive? Find(string ticket)
    {
        if (string.IsNullOrEmpty(ticket) || !_held.TryGetValue(ticket, out var held)) return null;
        if (held.Until > clock.GetUtcNow()) return held.Archive;
        _held.TryRemove(ticket, out _);
        return null;
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (var (key, held) in _held)
            if (held.Until <= now) _held.TryRemove(key, out _);
    }
}
