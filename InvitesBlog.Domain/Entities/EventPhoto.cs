namespace InvitesBlog.Domain.Entities;

/// <summary>
/// A photo a guest took at the event, in that campaign's photo box (§5 invites.lens). The invitation
/// gets people to the party; this is what they leave with.
///
/// <para><b>Why not <see cref="CampaignAsset"/>.</b> A campaign asset is content the HOST chose — a
/// cover, a couple photo — bound into the invitation itself, and there are a handful per campaign. A
/// photo box is guest-generated, unbounded (a wedding with 80 guests shooting freely is thousands of
/// files), needs to know WHO took each one so they can remove their own, and must never end up bound
/// into anybody's invitation by a slot lookup. Same storage, different lifecycle.</para>
///
/// <para><b>Three URLs, on purpose.</b> A grid of a thousand thumbnails, a photo someone opens
/// full-screen, and the file they want back afterwards have nothing in common but the shot they came
/// from. Serving the viewing size into a 120px tile is what makes a photo grid crawl on a phone at an
/// event, which is exactly where it gets used — and serving the original there would be far worse.</para>
/// </summary>
public sealed class EventPhoto
{
    public Guid Id { get; set; }

    /// <summary>
    /// The event this was shot at, when there was one.
    ///
    /// <para>Null for a photograph in a STANDALONE bucket — one somebody bought for a trip or a
    /// reunion, with no invitation behind it. That is a real case rather than a defect, and the
    /// honest model for it is an absent campaign rather than an empty guid standing in for one: every
    /// query here compares against a campaign that exists, and null correctly matches none of
    /// them.</para>
    /// </summary>
    public Guid? CampaignId { get; set; }

    /// <summary>
    /// The <see cref="MediaBucket"/> this belongs to — what is charged for the space it takes, and
    /// what a QR contributor is adding to.
    ///
    /// <para>Nullable only for rows written before buckets existed. Everything new has one, and a
    /// campaign's bucket is provisioned on demand, so the null case is history rather than a state
    /// anything creates.</para>
    /// </summary>
    public Guid? BucketId { get; set; }

    /// <summary>
    /// The guest who took it, when a guest did. Null for a photo the host added themselves — the host
    /// is an account, not a row on their own guest list.
    /// </summary>
    public Guid? GuestId { get; set; }

    /// <summary>
    /// Denormalised at upload so the box still reads correctly after a guest row is edited, renamed,
    /// or removed under privacy rules. A photo credit should not be able to change retroactively.
    /// </summary>
    public string? UploaderName { get; set; }

    /// <summary>
    /// Every pixel as uploaded, camera metadata stripped. Nothing in the app loads this — it exists to
    /// be downloaded. An event photo is somebody's own photograph and the copy they may want back is
    /// the one they took, so this box keeps it rather than only a copy sized for a screen.
    /// </summary>
    public string OriginalUrl { get; set; } = default!;

    /// <summary>Viewing size — what opens when someone taps a tile.</summary>
    public string Url { get; set; } = default!;

    /// <summary>Grid size. The only thing the photo box itself ever loads.</summary>
    public string ThumbUrl { get; set; } = default!;

    public string ContentType { get; set; } = default!;
    public long SizeBytes { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }

    /// <summary>
    /// Set when the host removes a photo, or the guest who took it does. Soft, because the delete is
    /// one tap on a phone at a party and the mis-tap is otherwise unrecoverable — and because the
    /// stored objects outlive the row either way, so a hard delete would buy nothing back until
    /// retention sweeps the campaign.
    /// </summary>
    public DateTimeOffset? DeletedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
