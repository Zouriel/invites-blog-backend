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

    /// <summary>
    /// The fix for <see cref="HostUploadsSql"/> (2026-11-13 night). QR-code and invitation-camera uploads also have no
    /// guest and no account, but they carry the name the person typed; real host uploads carry no name. That backfill
    /// gave every one of them to the organiser (in the owner's event, photos by Rani, Baamira and Rany became his).
    /// Before it ran, no named upload without a guest from before 2026-11-04 had an account (checked against the
    /// pre-backfill backup), so every such row owned by its event's organiser was set by it. Give them back, except
    /// names that are the organiser's own: the account's full name, or its last word ("Imdhah" in "Mohamed Imdhah").
    /// Anything unsure stays unowned; the studio can still say "This is me" for it.
    /// </summary>
    public const string NamedUploadsFixSql = """
        UPDATE event_photos ep
        SET uploader_user_id = NULL
        FROM campaigns c
        JOIN users u ON u.id = c.created_by_user_id
        WHERE ep.campaign_id = c.id
          AND ep.guest_id IS NULL
          AND ep.claim_id IS NULL
          AND ep.uploader_user_id = c.created_by_user_id
          AND ep.created_at < '2026-11-04T00:00:00Z'
          AND btrim(coalesce(ep.uploader_name, '')) <> ''
          AND lower(btrim(ep.uploader_name)) <> lower(btrim(coalesce(u.display_name, '')))
          AND lower(btrim(ep.uploader_name)) <> lower(regexp_replace(btrim(coalesce(u.display_name, '')), '^.*\s', ''));
        """;
}
