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

    /// <summary>The Android notification channel the app creates (MainActivity.kt); must match.</summary>
    public const string AndroidChannel = "events";

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
                    // High priority so a phone in Doze shows it now, on the app's own channel.
                    android = new { priority = "HIGH", notification = new { channel_id = AndroidChannel } },
                },
            }, options: Json),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        using var response = await client.SendAsync(request, ct);
        if (response.IsSuccessStatusCode) return false;

        var (code, message) = Error(await response.Content.ReadAsStringAsync(ct));
        if (IsGone(code, message)) return true;

        logger.LogWarning("Push to device {DeviceId} refused: {Status} {Code}.",
            device.Id, (int)response.StatusCode, code ?? "no error code");
        return false;
    }

    /// <summary>
    /// Whether FCM's answer means this token will never work: UNREGISTERED (the app was removed or the
    /// token replaced), SENDER_ID_MISMATCH (a token from another Firebase project), or INVALID_ARGUMENT
    /// about the token itself ("The registration token is not a valid FCM registration token"). Any other
    /// INVALID_ARGUMENT is about our message, so the device stays.
    /// </summary>
    public static bool IsGone(string? code, string? message) => code switch
    {
        "UNREGISTERED" or "SENDER_ID_MISMATCH" => true,
        "INVALID_ARGUMENT" => message?.Contains("registration token", StringComparison.OrdinalIgnoreCase) == true,
        _ => false,
    };

    /// <summary>The FcmError code from error.details and error.message; nulls if the body has none.</summary>
    private static (string? Code, string? Message) Error(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("error", out var error)
                || error.ValueKind != JsonValueKind.Object) return (null, null);

            var message = error.TryGetProperty("message", out var m) && m.ValueKind == JsonValueKind.String
                ? m.GetString() : null;
            if (!error.TryGetProperty("details", out var details) || details.ValueKind != JsonValueKind.Array)
                return (null, message);

            foreach (var detail in details.EnumerateArray())
                if (detail.ValueKind == JsonValueKind.Object
                    && detail.TryGetProperty("errorCode", out var code)
                    && code.ValueKind == JsonValueKind.String)
                    return (code.GetString(), message);
            return (null, message);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }
}
