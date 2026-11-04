using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Domain.Entities;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Phones;

/// <summary>
/// The region a phone number typed without its country code is read in, when the caller didn't pick
/// one: the signed-in account's saved country, else the country Cloudflare reports for the request.
/// Null when neither is known; there is no built-in default country.
/// </summary>
public interface IPhoneRegionSource
{
    Task<string?> DefaultRegionAsync(CancellationToken ct = default);
}

public sealed class PhoneRegionSource(ICurrentUser currentUser, IRepository<AppUser> users) : IPhoneRegionSource
{
    public async Task<string?> DefaultRegionAsync(CancellationToken ct = default)
    {
        if (currentUser.UserId is { } id)
        {
            var saved = await users.Query().Where(u => u.Id == id).Select(u => u.Country).FirstOrDefaultAsync(ct);
            if (PhoneNormalizer.IsKnownRegion(saved)) return saved;
        }
        return RequestCountry.Read(currentUser.RequestCountry);
    }
}

/// <summary>The visitor's country as Cloudflare reports it in <c>CF-IPCountry</c>.</summary>
public static class RequestCountry
{
    public const string Header = "CF-IPCountry";

    /// <summary>
    /// The ISO2 country, or null for none, "XX" (unknown) and "T1" (Tor), and anything that isn't a
    /// region libphonenumber knows. Not authenticated: it only ever picks a default the person can change.
    /// </summary>
    public static string? Read(string? header)
    {
        var v = header?.Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(v) || v is "XX" or "T1") return null;
        return PhoneNormalizer.Region(v);
    }
}
