using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InvitesBlog.Infrastructure.Email;

/// <summary>Thrown when the Resend API returns a non-success status (provider guide §2.6).</summary>
public sealed class ResendException(int statusCode, string body)
    : Exception($"Resend API error {statusCode}: {body}")
{
    public int StatusCode { get; } = statusCode;
}

public sealed record ResendTag(
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("value")] string Value);

public sealed record ResendEmailRequest(
    [property: JsonPropertyName("from")] string From,
    [property: JsonPropertyName("to")] IReadOnlyList<string> To,
    [property: JsonPropertyName("subject")] string Subject,
    [property: JsonPropertyName("html")] string Html,
    [property: JsonPropertyName("text")] string? Text = null,
    [property: JsonPropertyName("reply_to")] string? ReplyTo = null,
    [property: JsonPropertyName("tags")] IReadOnlyList<ResendTag>? Tags = null,
    [property: JsonPropertyName("headers")] IReadOnlyDictionary<string, string>? Headers = null);

public sealed record ResendSendResult([property: JsonPropertyName("id")] string Id);

/// <summary>
/// Thin typed client over the Resend REST API (provider guide §2.6). Registered via
/// <c>AddHttpClient</c> with the base address, bearer auth, and a standard resilience handler.
/// </summary>
public sealed class ResendClient(HttpClient http)
{
    private static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>POST /emails — returns the provider message id to store on the delivery attempt.</summary>
    public async Task<string> SendAsync(ResendEmailRequest request, CancellationToken ct)
    {
        using var response = await http.PostAsJsonAsync("/emails", request, Json, ct);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(ct);
            throw new ResendException((int)response.StatusCode, body);
        }
        var result = await response.Content.ReadFromJsonAsync<ResendSendResult>(cancellationToken: ct);
        return result?.Id ?? string.Empty;
    }
}
