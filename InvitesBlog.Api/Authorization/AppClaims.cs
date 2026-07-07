namespace InvitesBlog.Api.Authorization;

/// <summary>Custom claim types used across authentication + authorization.</summary>
public static class AppClaims
{
    public const string Permission = "permission";
    public const string CampaignId = "campaign_id";
    public const string ContactType = "contact_type";
    public const string Contact = "contact";
}
