namespace InvitesBlog.Application.Abstractions;

/// <summary>
/// The authenticated principal for the current request, derived from the possession token,
/// the OTP-JWT, or admin credentials. Services use it to enforce resource ownership on top of
/// the permission policies that gate each action (full-RBAC model).
/// </summary>
public interface ICurrentUser
{
    bool IsAuthenticated { get; }
    string Role { get; }

    /// <summary>The campaign a possession token maps to (Inviter principals), else null.</summary>
    Guid? CampaignId { get; }

    /// <summary>Verified invitee identity from an OTP-JWT: "phone" | "email" and its value.</summary>
    string? ContactType { get; }
    string? Contact { get; }

    Guid? UserId { get; }

    /// <summary>
    /// The visitor's country from Cloudflare's <c>CF-IPCountry</c> header, raw (may be "XX"/"T1");
    /// read it through <c>Phones.RequestCountry.Read</c>. Null outside a request.
    /// </summary>
    string? RequestCountry => null;

    bool HasPermission(string permission);
}
