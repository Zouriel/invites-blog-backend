using System.Text;
using System.Text.Json.Nodes;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.TemplateCompiler;
using Microsoft.Extensions.Configuration;

namespace InvitesBlog.Api.Rendering;

/// <summary>
/// Turns a resolved payload plus a pinned template package into the one document a guest is sent.
/// The last step of the guest path: everything before it decides WHAT to show, this decides what the
/// bytes look like.
/// </summary>
public sealed class RenderedInvitations(IStorageService storage, IConfiguration config)
{
    /// <summary>
    /// Fetches the package the campaign pinned and binds the payload into it. Returns null when the
    /// package has gone from storage — a campaign can outlive a template version, and a blank page is
    /// a better failure than a half-rendered one.
    /// </summary>
    public async Task<string?> BuildAsync(string packageUrl, JsonObject data, CancellationToken ct)
    {
        var key = StorageKey(packageUrl);
        if (key is null) return null;

        var bytes = await storage.GetAsync(key, ct);
        if (bytes is null || bytes.Length == 0) return null;

        return ServerBinder.Bind(Encoding.UTF8.GetString(bytes), data);
    }

    /// <summary>
    /// Maps a stored package URL back to the storage key holding its <c>index.html</c>. Stored URLs
    /// are relative in production (<c>/assets/templates/x@1.0.0/</c>) but absolute in local dev, so
    /// both shapes have to resolve — this is exactly the property that makes swapping the storage
    /// backend a config change rather than a data migration, and it is worth not breaking.
    /// </summary>
    private string? StorageKey(string packageUrl)
    {
        if (string.IsNullOrWhiteSpace(packageUrl)) return null;

        var path = PathOf(packageUrl);
        // AssetsBase is a path in production (/assets) but absolute in local dev
        // (http://localhost:8080/assets), so reduce both to a path before comparing.
        var prefix = PathOf(config["Urls:AssetsBase"] ?? "/assets").TrimEnd('/');

        var key = prefix.Length > 0 && path.StartsWith(prefix + "/", StringComparison.Ordinal)
            ? path[(prefix.Length + 1)..]
            : path.TrimStart('/');

        return string.IsNullOrWhiteSpace(key) ? null : key.TrimEnd('/') + "/index.html";
    }

    /// <summary>The path part of a value that may be absolute or already just a path.</summary>
    private static string PathOf(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var absolute) ? absolute.AbsolutePath : value;
}
