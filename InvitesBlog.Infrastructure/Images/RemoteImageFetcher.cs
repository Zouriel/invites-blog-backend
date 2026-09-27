using InvitesBlog.Application.Abstractions;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Images;

/// <summary>
/// Fetches an image by URL with a short timeout and a size cap, reading no further than the cap so an
/// endless response can't fill memory. Any failure is null: callers treat a missing picture as none.
/// </summary>
public sealed class RemoteImageFetcher(HttpClient http, ILogger<RemoteImageFetcher> logger) : IRemoteImageFetcher
{
    public async Task<(byte[] Content, string ContentType)?> FetchAsync(Uri url, int maxBytes, CancellationToken ct = default)
    {
        try
        {
            using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return null;
            if (response.Content.Headers.ContentLength > maxBytes) return null;
            var type = response.Content.Headers.ContentType?.MediaType ?? "application/octet-stream";

            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, ct)) > 0)
            {
                if (buffer.Length + read > maxBytes) return null;
                buffer.Write(chunk, 0, read);
            }
            return (buffer.ToArray(), type);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogInformation(ex, "Couldn't fetch image {Url}", url);
            return null;
        }
    }
}
