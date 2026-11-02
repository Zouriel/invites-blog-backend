using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Notifications.Push;

/// <summary>
/// Sends through FCM HTTP v1, one request per device, and forgets devices FCM says are gone. Tokens
/// are credentials of a sort, so logs name a device by its row id.
/// </summary>
public sealed class FcmPushSender(
    IHttpClientFactory http,
    IFcmCredentials credentials,
    IRepository<DeviceToken> devices,
    IUnitOfWork uow,
    ILogger<FcmPushSender> logger) : IPushSender
{
    public const string ClientName = "fcm";

    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public async Task SendAsync(
        Guid userId, string title, string body, IReadOnlyDictionary<string, string>? data, CancellationToken ct)
    {
        // Android only: iPhones are reached through APNs directly (ApnsPushSender).
        var mine = await devices.Query(tracking: true)
            .Where(d => d.UserId == userId && d.Platform == DeviceToken.Android).ToListAsync(ct);
        if (mine.Count == 0) return;

        string projectId, accessToken;
        try
        {
            projectId = await credentials.ProjectIdAsync(ct);
            accessToken = await credentials.AccessTokenAsync(ct);
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Push not sent: no FCM credentials.");
            return;
        }

        var client = http.CreateClient(ClientName);
        var gone = new List<DeviceToken>();
        foreach (var device in mine)
        {
            try
            {
                if (await SendOneAsync(client, projectId, accessToken, device, title, body, data, ct))
                    gone.Add(device);
            }
            // One device failing, including a timeout, must not stop the others.
            catch (Exception ex) when (!ct.IsCancellationRequested)
            {
                logger.LogWarning(ex, "Push to device {DeviceId} failed.", device.Id);
            }
        }

        if (gone.Count == 0) return;
        devices.RemoveRange(gone);
        try
        {
            await uow.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            logger.LogInformation("A device FCM reported as gone had already been removed.");
        }
    }

    /// <summary>True when FCM says this token no longer exists.</summary>
    private async Task<bool> SendOneAsync(
        HttpClient client, string projectId, string accessToken, DeviceToken device,
        string title, string body, IReadOnlyDictionary<string, string>? data, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"v1/projects/{projectId}/messages:send")
        {
            Content = JsonContent.Create(new
            {
                message = new
                {
                    token = device.Token,
                    notification = new { title, body },
                    data = data is { Count: > 0 } ? data : null,
                },
            }, options: Json),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await client.SendAsync(request, ct);
        if (response.IsSuccessStatusCode) return false;

        var code = ErrorCode(await response.Content.ReadAsStringAsync(ct));
        if (code == "UNREGISTERED") return true;

        logger.LogWarning("Push to device {DeviceId} refused: {Status} {Code}.",
            device.Id, (int)response.StatusCode, code ?? "no error code");
        return false;
    }

    /// <summary>The FcmError code from error.details, or null if the body has none.</summary>
    private static string? ErrorCode(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.Object
                || !error.TryGetProperty("details", out var details)
                || details.ValueKind != JsonValueKind.Array) return null;

            foreach (var detail in details.EnumerateArray())
                if (detail.ValueKind == JsonValueKind.Object
                    && detail.TryGetProperty("errorCode", out var code)
                    && code.ValueKind == JsonValueKind.String)
                    return code.GetString();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
