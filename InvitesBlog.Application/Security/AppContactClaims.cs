namespace InvitesBlog.Application.Security;

/// <summary>
/// Claim names for the verified contact carried on a session token. Defined here so the Application
/// layer can mint them and the API layer can read them without either depending on the other — they
/// must stay identical to the names the authentication handler looks for.
/// </summary>
public static class AppContactClaims
{
    public const string ContactType = "contact_type";   // "phone" | "email"
    public const string Contact = "contact";
}
