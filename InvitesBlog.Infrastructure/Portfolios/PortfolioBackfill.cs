namespace InvitesBlog.Infrastructure.Portfolios;

/// <summary>One-off data fixes for the Studio portfolio, run by migrations (kept here so tests run the same SQL).</summary>
public static class PortfolioBackfill
{
    /// <summary>
    /// Host uploads from before 2026-11-03 have no uploader recorded (the column didn't exist), so the "Studio's own
    /// uploads only" rule skipped them all as guests'. In events organised by a Studio account, a photo with no guest
    /// and no uploader was added by the host (dashboard or app): record the organiser. Guest-link uploads keep their
    /// guest and stay the guests'.
    /// </summary>
    public const string HostUploadsSql = """
        UPDATE event_photos ep
        SET uploader_user_id = c.created_by_user_id
        FROM campaigns c
        JOIN users u ON u.id = c.created_by_user_id
        WHERE ep.campaign_id = c.id
          AND ep.guest_id IS NULL
          AND ep.uploader_user_id IS NULL
          AND ep.created_at < '2026-11-04T00:00:00Z'
          AND u.subscription_tier = 3;
        """;
}
