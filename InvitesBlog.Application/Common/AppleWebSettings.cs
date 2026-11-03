using Microsoft.Extensions.Configuration;

namespace InvitesBlog.Application.Common;

/// <summary>
/// Sign in with Apple on the website. Apple only lets a website sign in through a Services ID (not the
/// iPhone app's bundle id), with a return URL registered against it, so the website has its own client id
/// (<c>OAuth:Apple:WebClientId</c>, blog.invites.web) and return URL (<c>OAuth:Apple:WebRedirectUri</c>,
/// default <c>{Urls:InviterBase}/auth/apple/callback</c>).
/// <para>
/// Off unless <c>OAuth:Apple:WebEnabled</c> is true AND a web client id is set. While off: the website's
/// sign-in options don't list Apple, the web endpoints refuse, and an identity token addressed to the web
/// client id is refused like any other foreign audience. The iPhone app is unaffected either way.
/// </para>
/// </summary>
public sealed record AppleWebSettings(bool Enabled, string? ClientId, string RedirectUri)
{
    public bool IsOn => Enabled && !string.IsNullOrWhiteSpace(ClientId);

    public static AppleWebSettings From(IConfiguration config) => new(
        bool.TryParse(config["OAuth:Apple:WebEnabled"], out var on) && on,
        config["OAuth:Apple:WebClientId"]?.Trim() is { Length: > 0 } id ? id : null,
        config["OAuth:Apple:WebRedirectUri"]?.Trim() is { Length: > 0 } uri
            ? uri
            : $"{config.InviterBase()}/auth/apple/callback");
}
