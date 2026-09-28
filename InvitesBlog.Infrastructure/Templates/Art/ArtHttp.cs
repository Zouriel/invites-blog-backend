using System.Net;
using System.Net.Sockets;
using InvitesBlog.Application.Exceptions;

namespace InvitesBlog.Infrastructure.Templates.Art;

/// <summary>
/// The HTTP client the art library downloads with. A library's search results name file URLs on hosts
/// we don't control (Openverse indexes Wikimedia, Flickr and more), so every connection — redirects
/// included — is checked where it is made: the address it resolved to must be public. That closes the
/// "a result points at 169.254.169.254" hole whatever the URL looked like.
/// </summary>
public static class ArtHttp
{
    public const string ClientName = "art-library";
    public const string UserAgent = "invites.blog-designer/1.0 (+https://invites.blog)";

    public static SocketsHttpHandler Handler() => new()
    {
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 5,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        ConnectTimeout = TimeSpan.FromSeconds(8),
        ConnectCallback = async (context, ct) =>
        {
            var addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            var target = addresses.FirstOrDefault(IsPublic)
                         ?? throw new HttpRequestException($"{context.DnsEndPoint.Host} isn't a public address.");
            var socket = new Socket(target.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(new IPEndPoint(target, context.DnsEndPoint.Port), ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        },
    };

    /// <summary>Not loopback, private, link-local, carrier-grade NAT, multicast or reserved.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        if (IPAddress.IsLoopback(address)) return false;
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = address.GetAddressBytes();
            return !(b[0] == 0 || b[0] == 10 || b[0] == 127 || b[0] >= 224
                     || b[0] == 100 && b[1] >= 64 && b[1] <= 127
                     || b[0] == 169 && b[1] == 254
                     || b[0] == 172 && b[1] >= 16 && b[1] <= 31
                     || b[0] == 192 && b[1] == 168
                     || b[0] == 192 && b[1] == 0 && b[2] == 0
                     || b[0] == 198 && b[1] is 18 or 19);
        }
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast || address.Equals(IPAddress.IPv6None)) return false;
            var b = address.GetAddressBytes();
            if ((b[0] & 0xfe) == 0xfc) return false; // unique local fc00::/7
            return true;
        }
        return false;
    }

    /// <summary>
    /// Downloads at most <paramref name="maxBytes"/>, reading no further, over https only. Throws a
    /// message for the designer when it can't.
    /// </summary>
    public static async Task<(byte[] Content, string ContentType)> DownloadAsync(
        HttpClient http, Uri url, int maxBytes, CancellationToken ct, Action<HttpRequestMessage>? configure = null)
    {
        if (url.Scheme != Uri.UriSchemeHttps) throw new BusinessRuleException("That file isn't available over a secure link.", "art_unavailable");
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        configure?.Invoke(request);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new BusinessRuleException("The library didn't answer — try again in a moment.", "art_unavailable");
        }
        using (response)
        {
            if (response.RequestMessage?.RequestUri is { } final && final.Scheme != Uri.UriSchemeHttps)
                throw new BusinessRuleException("That file isn't available over a secure link.", "art_unavailable");
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
                throw new BusinessRuleException("The library is busy — try again in a minute.", "art_busy");
            if (!response.IsSuccessStatusCode)
                throw new BusinessRuleException($"The library couldn't send that file ({(int)response.StatusCode}).", "art_unavailable");
            if (response.Content.Headers.ContentLength > maxBytes) throw TooLarge(maxBytes);
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > maxBytes) throw TooLarge(maxBytes);
                buffer.Write(chunk, 0, read);
            }
            return (buffer.ToArray(), response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream");
        }
    }

    private static BusinessRuleException TooLarge(int maxBytes) =>
        new($"That file is over {maxBytes / 1024 / 1024}MB — too big to put in an invitation.", "art_too_large");
}
