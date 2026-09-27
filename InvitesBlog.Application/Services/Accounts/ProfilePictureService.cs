using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Common;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Application.Services.Accounts;

/// <summary>
/// An account's profile picture: taken from Google at sign-in until the person picks their own, then
/// theirs to upload or remove.
/// </summary>
public interface IProfilePictureService
{
    /// <summary>
    /// Copies the sign-in provider's picture onto an account that has none and has never chosen one.
    /// Never throws: a picture that can't be fetched must not stop anybody signing in.
    /// </summary>
    Task ImportFromProviderAsync(AppUser user, string? pictureUrl, CancellationToken ct = default);

    /// <summary>Replaces the picture with an uploaded one.</summary>
    Task SetAsync(AppUser user, byte[] content, string contentType, CancellationToken ct = default);

    /// <summary>Back to initials, and a later Google sign-in leaves it that way.</summary>
    Task RemoveAsync(AppUser user, CancellationToken ct = default);
}

public sealed class ProfilePictureService(
    IStorageService storage,
    IImageOptimizer imageOptimizer,
    IRemoteImageFetcher fetcher,
    IUnitOfWork uow,
    ILogger<ProfilePictureService> logger) : IProfilePictureService
{
    /// <summary>Uploads over this are refused; a phone photo is well under it.</summary>
    public const int MaxBytes = 10 * 1024 * 1024;

    /// <summary>
    /// Stored at this edge. It's drawn at most a few dozen CSS pixels across, so this covers a sharp
    /// screen with room to spare.
    /// </summary>
    private const int Edge = 512;

    public async Task ImportFromProviderAsync(AppUser user, string? pictureUrl, CancellationToken ct = default)
    {
        if (user.AvatarUrl is not null || user.AvatarChosenAt is not null) return;
        var url = ProviderPicture(pictureUrl);
        if (url is null) return;

        try
        {
            var fetched = await fetcher.FetchAsync(url, MaxBytes, ct);
            if (fetched is not { } image || !ImageSniffer.IsPhoto(image.Content, image.ContentType)) return;
            user.AvatarUrl = await StoreAsync(user, image.Content, image.ContentType, ct);
            await uow.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Couldn't copy the sign-in provider's picture for account {UserId}", user.Id);
        }
    }

    public async Task SetAsync(AppUser user, byte[] content, string contentType, CancellationToken ct = default)
    {
        if (content.Length == 0) throw new BusinessRuleException("The image file is empty.", "empty_image");
        if (content.Length > MaxBytes)
            throw new BusinessRuleException("A profile picture must be 10 MB or smaller.", "image_too_large");
        // By the bytes as well as the label: the picture is served on the app's own origin, and an SVG
        // there is a script-bearing document, not a picture.
        if (!ImageSniffer.IsPhoto(content, contentType))
            throw new BusinessRuleException(ImageSniffer.Refusal, ImageSniffer.RefusalCode);

        user.AvatarUrl = await StoreAsync(user, content, contentType, ct);
        user.AvatarChosenAt = DateTimeOffset.UtcNow;
        await uow.SaveChangesAsync(ct);
    }

    public async Task RemoveAsync(AppUser user, CancellationToken ct = default)
    {
        user.AvatarUrl = null;
        user.AvatarChosenAt = DateTimeOffset.UtcNow;
        await uow.SaveChangesAsync(ct);
    }

    /// <summary>
    /// A new key per picture, so a browser holding the old one in its cache can't keep showing it.
    /// </summary>
    private async Task<string> StoreAsync(AppUser user, byte[] content, string contentType, CancellationToken ct)
    {
        var optimized = imageOptimizer.Optimize(content, contentType, Edge);
        var ext = MediaFileTypes.ExtensionFor(optimized.ContentType);
        return await storage.PutAsync(
            $"avatars/{user.Id:N}/{Guid.NewGuid():N}{ext}", optimized.Content, optimized.ContentType, ct);
    }

    /// <summary>
    /// The provider's picture as a URL worth fetching, or null. Only Google's own image host: the
    /// server fetches this, and a URL that could point anywhere would let a token aim it at our own
    /// network. Google hands out a 96px picture; its size suffix asks for one big enough to keep.
    /// </summary>
    public static Uri? ProviderPicture(string? pictureUrl)
    {
        if (!Uri.TryCreate(pictureUrl, UriKind.Absolute, out var url)) return null;
        if (url.Scheme != Uri.UriSchemeHttps) return null;
        if (!url.Host.EndsWith(".googleusercontent.com", StringComparison.OrdinalIgnoreCase)) return null;

        var text = url.ToString();
        var size = text.LastIndexOf("=s", StringComparison.Ordinal);
        return size > text.LastIndexOf('/') ? new Uri(text[..size] + $"=s{Edge}-c") : url;
    }
}
