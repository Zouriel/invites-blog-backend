using System.Text.Json;
using Google.Apis.Auth.OAuth2;
using Microsoft.Extensions.Configuration;

namespace InvitesBlog.Infrastructure.Notifications.Push;

/// <summary>What FCM needs from the service account: which project to send through, and a bearer token.</summary>
public interface IFcmCredentials
{
    Task<string> ProjectIdAsync(CancellationToken ct);
    Task<string> AccessTokenAsync(CancellationToken ct);
}

/// <summary>
/// Reads the service-account file at Push:ServiceAccountPath on first use, so a wrong path is a logged
/// send failure and not a server that won't start. One instance serves the whole app because the
/// credential caches and refreshes its access token.
/// </summary>
public sealed class ServiceAccountFcmCredentials(IConfiguration config) : IFcmCredentials
{
    public const string ConfigKey = "Push:ServiceAccountPath";
    private const string Scope = "https://www.googleapis.com/auth/firebase.messaging";

    private readonly Lazy<Task<(string ProjectId, ITokenAccess Access)>> _loaded =
        new(() => LoadAsync(config[ConfigKey]));

    public async Task<string> ProjectIdAsync(CancellationToken ct) => (await _loaded.Value).ProjectId;

    public async Task<string> AccessTokenAsync(CancellationToken ct) =>
        await (await _loaded.Value).Access.GetAccessTokenForRequestAsync(cancellationToken: ct);

    private static async Task<(string, ITokenAccess)> LoadAsync(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            throw new InvalidOperationException($"{ConfigKey} is not set.");

        var json = await File.ReadAllTextAsync(path);
        using var doc = JsonDocument.Parse(json);
        var projectId = doc.RootElement.TryGetProperty("project_id", out var p) ? p.GetString() : null;
        if (string.IsNullOrWhiteSpace(projectId))
            throw new InvalidOperationException("The service-account file has no project_id.");

        var credential = CredentialFactory.FromJson<ServiceAccountCredential>(json)
            .ToGoogleCredential().CreateScoped(Scope);
        return (projectId, credential);
    }
}
