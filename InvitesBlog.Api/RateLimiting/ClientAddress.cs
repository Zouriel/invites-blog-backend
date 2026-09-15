using System.Net;
using System.Net.Sockets;

namespace InvitesBlog.Api.RateLimiting;

/// <summary>
/// Who a request really came from — the one answer every per-client limit and the personal-link IP
/// trust are keyed on.
///
/// <para><b>Why not just <c>Connection.RemoteIpAddress</c>.</b> In production a request passes through
/// Caddy, and <c>UseForwardedHeaders</c> turns Caddy's <c>X-Forwarded-For</c> into the remote address.
/// That is right when Caddy's peer is the visitor. When the site is proxied by Cloudflare, Caddy's peer
/// is a Cloudflare EDGE server, so the "client" becomes an edge address — and a visitor's requests
/// leave through many edge addresses, often a different one per request. Every limiter partitioned on
/// it then gives each request a fresh quota. That is the most likely reason 90 hits to an open link
/// and 10 OTP requests in a row all passed in production (2026-09-09) while the same limits bit at
/// once locally: the forwarded-header handling itself was checked and does honour Caddy's header.</para>
///
/// <para>Cloudflare puts the visitor's address in <c>CF-Connecting-IP</c>. It is believed ONLY when the
/// hop that sent it is inside Cloudflare's published ranges; anybody reaching Caddy directly (the
/// sslip.io hosts, the bare IP) can set the header too, and trusting it from them would let them pick
/// their own partition.</para>
/// </summary>
public static class ClientAddress
{
    public const string CloudflareHeader = "CF-Connecting-IP";

    /// <summary>
    /// Cloudflare's edge ranges, from https://www.cloudflare.com/ips/. They change rarely; a range
    /// missing here only means requests through it are keyed on the edge address again, as before.
    /// </summary>
    private static readonly IPNetwork[] Cloudflare =
    [
        .. new[]
        {
            "173.245.48.0/20", "103.21.244.0/22", "103.22.200.0/22", "103.31.4.0/22", "141.101.64.0/18",
            "108.162.192.0/18", "190.93.240.0/20", "188.114.96.0/20", "197.234.240.0/22", "198.41.128.0/17",
            "162.158.0.0/15", "104.16.0.0/13", "104.24.0.0/14", "172.64.0.0/13", "131.0.72.0/22",
            "2400:cb00::/32", "2606:4700::/32", "2803:f800::/32", "2405:b500::/32", "2405:8100::/32",
            "2a06:98c0::/29", "2c0f:f248::/32",
        }.Select(IPNetwork.Parse),
    ];

    /// <summary>The visitor's address, or null when the connection has none.</summary>
    public static IPAddress? Of(HttpContext context)
    {
        var peer = Normalize(context.Connection.RemoteIpAddress);
        if (peer is null) return null;

        if (IsCloudflare(peer)
            && IPAddress.TryParse(context.Request.Headers[CloudflareHeader].ToString().Trim(), out var visitor))
            return Normalize(visitor);

        return peer;
    }

    /// <summary>
    /// What a per-client limit is keyed on. An IPv6 visitor is grouped by their /64: one connection is
    /// routinely handed a whole /64, and keying on the full address would give them 2^64 quotas.
    /// </summary>
    public static string PartitionKey(HttpContext context)
    {
        var address = Of(context);
        if (address is null) return "unknown";
        if (address.AddressFamily != AddressFamily.InterNetworkV6) return address.ToString();

        var bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return $"{new IPAddress(bytes)}/64";
    }

    public static bool IsCloudflare(IPAddress address) => Cloudflare.Any(range => range.Contains(address));

    private static IPAddress? Normalize(IPAddress? address) =>
        address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address;
}
