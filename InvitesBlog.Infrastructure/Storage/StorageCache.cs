namespace InvitesBlog.Infrastructure.Storage;

/// <summary>
/// How long a stored object may be cached, decided from its key.
///
/// <para><b>Why this lives with the object and not with the proxy.</b> These rules used to be Caddy's:
/// it sat in front of MinIO and added the headers on the way out. That works exactly as long as
/// every request goes through it — and the whole point of moving to R2 is that <c>/assets/*</c> may
/// be served from a bucket domain or a Worker instead, at which point proxy rules silently stop
/// applying and nobody notices until something serves stale for a day. Set on <c>PutObject</c>, the
/// header is a property of the object and travels with it wherever it is served from.</para>
///
/// <para>We were burned by precisely this in August 2026: corrected posters kept serving stale for
/// hours behind a four-hour <c>max-age</c>. The durable fix was content-addressed filenames — which
/// is also what makes the immutable rule below safe.</para>
/// </summary>
public static class StorageCache
{
    /// <summary>
    /// Template packages are REPUBLISHED at the same URL — an approved edit overwrites
    /// <c>templates/x@1.0.0/index.html</c> in place — so they can never be cached as immutable.
    /// </summary>
    private const string Revalidate = "no-cache, must-revalidate";

    /// <summary>
    /// Campaign content is content-addressed or GUID-named: a given key's bytes never change, only
    /// new keys appear. That is what earns the year.
    /// </summary>
    private const string Immutable = "public, max-age=31536000, immutable";

    public static string For(string key)
    {
        var k = (key ?? string.Empty).TrimStart('/');
        return k.StartsWith("templates/", StringComparison.OrdinalIgnoreCase)
            || k.StartsWith("submissions/", StringComparison.OrdinalIgnoreCase)
            ? Revalidate
            : Immutable;
    }
}
