using Microsoft.Extensions.Configuration;

namespace InvitesBlog.Application.Common;

/// <summary>
/// The two public app origins every emailed or handed-back link is built on — <c>Urls:InviterBase</c>
/// (the host's app: dashboard, contribute pages) and <c>Urls:InviteeBase</c> (the guest's app:
/// <c>/i/{token}</c>, <c>/e/{id}</c>, <c>/o/{code}</c>, <c>/h/{handoff}</c>).
///
/// <para>One place for the fallbacks because they used to disagree: most reads fell back to the
/// localhost dev servers, while a few fell back to the PRODUCTION hosts — so a dev or test box with
/// the key missing quietly emailed and redirected people to the live site. A missing key now always
/// means "local development". Production sets both keys, so its links are unchanged.</para>
/// </summary>
public static class AppUrls
{
    /// <summary>The inviter app's dev server — the fallback when <c>Urls:InviterBase</c> is unset.</summary>
    public const string DevInviterBase = "http://localhost:4200";

    /// <summary>The invitee app's dev server — the fallback when <c>Urls:InviteeBase</c> is unset.</summary>
    public const string DevInviteeBase = "http://localhost:4201";

    /// <summary>The host-facing app origin, without a trailing slash.</summary>
    public static string InviterBase(this IConfiguration config) =>
        (config["Urls:InviterBase"] ?? DevInviterBase).TrimEnd('/');

    /// <summary>The guest-facing app origin, without a trailing slash.</summary>
    public static string InviteeBase(this IConfiguration config) =>
        (config["Urls:InviteeBase"] ?? DevInviteeBase).TrimEnd('/');
}
