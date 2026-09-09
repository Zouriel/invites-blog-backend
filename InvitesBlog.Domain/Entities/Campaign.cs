using InvitesBlog.Domain.Enums;

namespace InvitesBlog.Domain.Entities;

/// <summary>A single invitation campaign (§8.2 Campaign).</summary>
public sealed class Campaign
{
    public Guid Id { get; set; }
    public Guid TemplateId { get; set; }
    public string TemplateVersion { get; set; } = default!;   // pinned (§5.6)

    /// <summary>
    /// The pinned template version's FULL manifest, frozen at creation time. Everything downstream (the
    /// wizard's field/theme/role structure, render, send) reads this — never the live <c>Template</c> row —
    /// so re-reviewing or editing a template can't retroactively change an in-progress campaign.
    /// </summary>
    public string TemplateManifestJson { get; set; } = "{}";

    /// <summary>
    /// The pinned version's package URL, frozen at creation. The live <c>Template</c> row's PackageUrl
    /// moves to the newest version whenever an edit is approved, so rendering from it would serve
    /// already-sent invites brand-new markup. Empty only for campaigns created before this was stored.
    /// </summary>
    public string TemplatePackageUrl { get; set; } = string.Empty;

    /// <summary>
    /// The community designer's per-use fee, frozen from the template at creation time along with the
    /// manifest. Read live it would let a designer change the price of a campaign already in progress;
    /// frozen, the inviter pays what they were quoted. Zero for platform templates.
    /// </summary>
    public decimal DesignerFee { get; set; }
    /// <summary>Who the fee is owed to, for the checkout line item and the payouts report.</summary>
    public string? DesignerFeeName { get; set; }
    public Guid? InviterId { get; set; }                      // set at inviter-details step

    /// <summary>
    /// The signed-in account that started this campaign, when there was one.
    /// <para>
    /// Separate from <see cref="InviterId"/> on purpose: the inviter is WHO IS HOSTING, and is only
    /// known once the host-details step is filled in. A draft abandoned before that step had no
    /// owner at all, so it never appeared in "my drafts" and could not be found or deleted by
    /// anyone. This records who it belongs to from the first click.
    /// </para>
    /// </summary>
    public Guid? CreatedByUserId { get; set; }
    public string AccessTokenHash { get; set; } = default!;   // §4.6.2 possession token
    public string? DashboardTokenHash { get; set; }           // §4.6.2 post-payment dashboard link

    /// <summary>
    /// The short code behind this event's OPEN LINK, or null when it has none.
    ///
    /// <para>The open link is one address anybody may follow — no guest list, no OTP, no account —
    /// for a customer who brought their own artwork and wants to paste a link into a group chat.
    /// It is not the same thing as the shared link at <c>/e/{id}</c>, which looks similar and is
    /// gated: that one asks whoever follows it to prove an email or phone that is ON the guest
    /// list.</para>
    ///
    /// <para><b>Stored in the clear, unlike every other token on this entity.</b> Those are secrets
    /// — a possession token, a dashboard link, an invite token — and are kept as SHA-256 precisely
    /// so a database read cannot reveal them. This one is the opposite kind of thing: its entire
    /// purpose is to be posted in a group chat. Hashing it would buy nothing an attacker does not
    /// already get by being in that chat, and would cost the host the thing they actually need —
    /// asking us for their own link again tomorrow. A hash cannot be read back, so "show me my link"
    /// would have to mint a new one, silently breaking the link they had already shared.</para>
    ///
    /// <para><b>Null IS the off switch.</b> There is deliberately no separate boolean beside it: a
    /// flag and a token are two facts that can disagree, and the disagreement that matters here is
    /// "switched off but the old link still opens". Revoking sets this to null and the link stops
    /// resolving; ticking the box again mints a NEW code, so a link already shared cannot be
    /// resurrected by someone changing their mind twice.</para>
    ///
    /// <para>What it does NOT grant: the media bucket. Whoever opens it is on no guest list, so
    /// <c>MediaBucketService.MayViewAsync</c> refuses them — which is only true because the open
    /// link renders with a synthetic guest and writes no Guest row. See
    /// <c>InviteService.RenderOpenAsync</c>.</para>
    /// </summary>
    public string? OpenLinkCode { get; set; }
    public string Title { get; set; } = default!;
    public string Slug { get; set; } = default!;
    public CampaignStatus Status { get; set; }
    public string EventType { get; set; } = "Other";
    public DateTimeOffset EventStartAt { get; set; }
    public DateTimeOffset? EventEndAt { get; set; }
    public int PaidInviteCapacity { get; set; }
    public bool HasDesignerDiscount { get; set; }
    public bool IsSensitive { get; set; }                     // §4.9.1 OTP-before-view
    public int RetentionDays { get; set; } = 90;              // §15.4

    /// <summary>
    /// When the last "new photos" digest went out for this campaign, or null if none ever has.
    /// <para>
    /// This is what makes the digest a digest. Photos newer than this are what the next one covers,
    /// and nothing is sent until a quiet period has passed since it — so a party where fifteen people
    /// upload all evening produces one email, not one per upload and certainly not one per photo.
    /// </para>
    /// </summary>
    public DateTimeOffset? PhotosNotifiedAt { get; set; }
    public string CustomContentJson { get; set; } = "{}";
    public string ThemeOverridesJson { get; set; } = "{}";
    public string DeliverySettingsJson { get; set; } = "{}";
    public string RulesJson { get; set; } = "{\"rules\":[]}"; // §12 personalization rules
    public string RolesJson { get; set; } = "{\"roles\":[]}"; // guest roles → content-block mapping
    /// <summary>
    /// What the RSVP form asks. Empty means "whatever the platform asks by default" — campaigns made
    /// before the question step existed keep the original four questions rather than losing them.
    /// </summary>
    public string RsvpQuestionsJson { get; set; } = "{\"questions\":[]}";
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
