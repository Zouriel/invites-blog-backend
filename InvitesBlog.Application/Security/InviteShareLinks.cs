using System.Security.Cryptography;
using System.Text;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Domain.Entities;
using Microsoft.Extensions.Configuration;

namespace InvitesBlog.Application.Security;

/// <summary>
/// A guest's personal link for the host to send by hand (WhatsApp, Viber, Telegram), made from the
/// invite's id and a server key so the SAME link comes back every time, without storing anything.
///
/// <para><b>Why not the emailed token.</b> Only that token's hash is stored, so it can't be read back,
/// and minting a new one would kill the link already in the guest's inbox. This is a second,
/// stateless credential for the same invite: <c>"w" + base64url(invite id, 16 bytes) +
/// base64url(first 16 bytes of HMAC-SHA256(key, "invite-share:" + id))</c>, 45 characters. It opens
/// exactly what the emailed link opens (the same IP trust, reauth and removal rules apply).</para>
///
/// <para><b>Key.</b> <c>Invites:ShareLinkKey</c>, else one derived from <c>Jwt:SigningKey</c> by HMAC
/// with a fixed label, so the signing key itself is never used for two things. Changing either key
/// changes every share link (emailed links are unaffected). Never logged.</para>
/// </summary>
public sealed class InviteShareLinks
{
    public const char Prefix = 'w';
    public const int Length = 1 + 22 + 22;
    private const string Purpose = "invite-share:";
    private const string DerivationLabel = "invites.blog invite share link key v1";

    private readonly byte[] _key;

    public InviteShareLinks(IConfiguration config)
    {
        var configured = config["Invites:ShareLinkKey"];
        _key = !string.IsNullOrWhiteSpace(configured)
            ? Encoding.UTF8.GetBytes(configured)
            : HMACSHA256.HashData(
                Encoding.UTF8.GetBytes(config["Jwt:SigningKey"] ?? "change-this-in-production-please-use-a-long-random-value"),
                Encoding.UTF8.GetBytes(DerivationLabel));
    }

    /// <summary>The share token for an invite. Deterministic: the same invite always gets the same one.</summary>
    public string TokenFor(Guid inviteId) =>
        Prefix + Base64Url(inviteId.ToByteArray()) + Base64Url(Mac(inviteId));

    /// <summary>The invite a share token was made for, or null when it isn't one of ours (or was altered).</summary>
    public Guid? Read(string? token)
    {
        if (token is null || token.Length != Length || token[0] != Prefix) return null;
        var idBytes = FromBase64Url(token.AsSpan(1, 22));
        var mac = FromBase64Url(token.AsSpan(23, 22));
        if (idBytes is not { Length: 16 } || mac is not { Length: 16 }) return null;
        var id = new Guid(idBytes);
        // Compared as the whole canonical string, in constant time: base64 has spare bits in its last
        // character, and comparing decoded bytes would let a second spelling of the same token through.
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(token), Encoding.ASCII.GetBytes(TokenFor(id))) ? id : null;
    }

    private byte[] Mac(Guid inviteId) =>
        HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(Purpose + inviteId.ToString("D")))[..16];

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[]? FromBase64Url(ReadOnlySpan<char> text)
    {
        var s = text.ToString().Replace('-', '+').Replace('_', '/');
        s += (s.Length % 4) switch { 2 => "==", 3 => "=", _ => "" };
        try { return Convert.FromBase64String(s); }
        catch (FormatException) { return null; }
    }
}

public static class InviteTokenLookup
{
    /// <summary>
    /// The invite a raw link token opens: the emailed token (by its stored hash) first, then a share
    /// token (<see cref="InviteShareLinks"/>) by the invite id it carries. Every place that resolves a
    /// guest's personal link goes through this, so both links always open the same things.
    /// </summary>
    public static async Task<Invite?> FindByTokenAsync(
        this IInviteRepository invites, InviteShareLinks shareLinks, string token, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(token)) return null;
        var byHash = await invites.GetByTokenHashAsync(TokenService.Hash(token), ct);
        if (byHash is not null) return byHash;
        return shareLinks.Read(token) is { } id ? await invites.GetByIdAsync(id, ct) : null;
    }
}
