using InvitesBlog.Domain.Enums;

namespace InvitesBlog.Domain.Entities;

/// <summary>
/// A media bucket — the place a night's photographs and clips are kept, and a product in its own
/// right.
///
/// <para><b>Why this is not a column on <see cref="Campaign"/>.</b> It began as one: every campaign
/// implicitly owned a photo box, unbounded and free. That shape cannot be sold. A thing you sell
/// needs a name of its own, a face of its own, a size someone chose, and a term that runs out — and
/// it needs to be able to exist for someone who is not running an event here at all. A reunion, a
/// trip, a season of somebody's football club: those are buckets with no invitation attached, and
/// the moment the box is its own row they are all just a bucket with a null
/// <see cref="CampaignId"/>.</para>
///
/// <para><b>Every campaign still gets one.</b> Attaching a bucket is what keeps the event photo box
/// working exactly as it did — the campaign's box IS its bucket. One is provisioned on demand for
/// any campaign that never had a row, on the free tier, so nothing that was working stops working
/// and nobody is billed for what they already had.</para>
/// </summary>
public sealed class MediaBucket
{
    public Guid Id { get; set; }

    /// <summary>
    /// The account that owns it — who may rename it, change its cover, hand out a QR, and moderate
    /// what lands in it. Not the campaign's inviter: an <see cref="Inviter"/> is who is HOSTING and
    /// is a detail of one event, while a bucket outlives the event and is bought by an account.
    /// </summary>
    public Guid OwnerUserId { get; set; }

    /// <summary>
    /// The event this bucket collects for, when there is one. Null is an ordinary bucket, not a
    /// broken one — see the class remarks.
    /// </summary>
    public Guid? CampaignId { get; set; }

    /// <summary>
    /// What it is called. Defaulted from the campaign's title when one is provisioned for an event,
    /// but its own field from then on: "Amira &amp; Yusuf" is the wedding, and the bucket people are
    /// still adding to a month later may as well be called "The wedding weekend".
    /// </summary>
    public string Title { get; set; } = default!;

    /// <summary>
    /// The face of it in a grid. Chosen by the owner rather than taken from the newest upload —
    /// a cover that changes every time somebody adds a photo is not a cover, it is a thumbnail, and
    /// the tile stops being recognisable as the same bucket from one visit to the next.
    /// </summary>
    public string? CoverUrl { get; set; }

    /// <summary>
    /// The night this bucket is for.
    ///
    /// <para>Every bucket has one, including a standalone one — a bucket is a record of an occasion,
    /// not an open-ended drive, and this is what makes it possible to say when it is open. Taken from
    /// the campaign for a bucket attached to one, so the invitation and the bucket can never disagree
    /// about which night they belong to.</para>
    ///
    /// <para>What it gates is <b>adding</b>, through <c>EventDayWindow</c>: the same window that
    /// decides whether a guest is offered the camera on their invitation. Looking is never gated —
    /// the point of the thing is what you have afterwards.</para>
    /// </summary>
    public DateTimeOffset EventDate { get; set; }

    public MediaBucketTier Tier { get; set; } = MediaBucketTier.Free;

    /// <summary>
    /// How many bytes this bucket may hold, FROZEN from the tier when it was bought.
    /// <para>
    /// Frozen for the same reason <see cref="Campaign.DesignerFee"/> is: read live from the tier
    /// table, changing what 20 GB means would silently resize every bucket already sold — upwards is
    /// a giveaway and downwards puts somebody over their limit for photographs they already
    /// uploaded. What someone bought is what they keep for the term they bought it for.
    /// </para>
    /// </summary>
    public long CapacityBytes { get; set; }

    /// <summary>
    /// What is stored, maintained as things are added and removed rather than summed on read.
    /// <para>
    /// A bucket is checked for room on every single upload, and a wedding's bucket is thousands of
    /// rows — re-summing them to answer "is there room for one more photo" would make the check cost
    /// grow with the thing it is protecting.
    /// </para>
    /// <para>
    /// It counts every byte an upload wrote, derivatives included, because that is what is actually
    /// being stored on somebody's behalf. It does NOT go down when a photo is soft-deleted: the
    /// objects behind a removed photo outlive the row, so crediting the space back would sell room
    /// that is still occupied. Retention is what reclaims it.
    /// </para>
    /// </summary>
    public long UsedBytes { get; set; }

    /// <summary>When the current term began. Null on the free tier, which has no term.</summary>
    public DateTimeOffset? TermStartAt { get; set; }

    /// <summary>
    /// When the current term runs out — six months from <see cref="TermStartAt"/>. Null on the free
    /// tier.
    /// <para>
    /// Nothing sweeps an expired bucket today and nothing should until somebody has answered what
    /// expiry means for the photographs inside it. Storing the date is what makes that decision
    /// possible later; acting on it is a separate question with somebody's memories on the other
    /// side of it.
    /// </para>
    /// </summary>
    public DateTimeOffset? TermEndAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>
/// A QR code that lets people put things into a bucket.
///
/// <para><b>Why a row and not just a rendered image.</b> The QR is printed onto a table card and put
/// in front of a hundred people; it is the least revocable thing in the product. Storing it makes it
/// possible to say which code someone scanned, to turn one off without turning off the others, and —
/// the thing that was actually asked for — to show the host the last one they made without asking
/// them to keep the picture safe themselves.</para>
///
/// <para><b>The token is stored hashed</b>, the same as <see cref="Campaign.AccessTokenHash"/>. It is
/// a bearer credential for writing into somebody's bucket: whoever holds it can contribute. A
/// database that can be read must not be a database that hands out working codes.</para>
/// </summary>
public sealed class MediaBucketQr
{
    public Guid Id { get; set; }
    public Guid BucketId { get; set; }

    /// <summary>SHA-256 of the token in the printed URL. The token itself is shown once, at creation.</summary>
    public string TokenHash { get; set; } = default!;

    /// <summary>
    /// The rendered code, stored as an image the moment it is made.
    ///
    /// <para><b>This is what makes "the last code is always there" possible at all.</b> The token is
    /// hashed, so the URL it encodes cannot be reconstructed from this table — which means a
    /// dashboard that wanted to redraw the code later had nothing to draw. Rendering once and keeping
    /// the picture gives the host something permanent to reprint, while the thing that authorizes
    /// stays unreadable in the database. A stolen backup yields a PNG someone still has to scan,
    /// not a list of working tokens.</para>
    /// </summary>
    public string ImageUrl { get; set; } = default!;

    /// <summary>
    /// The first characters of the token, kept in the clear so the dashboard can tell two codes
    /// apart. Far too short to guess the rest from, and never sufficient to contribute with.
    /// </summary>
    public string TokenHint { get; set; } = default!;

    /// <summary>
    /// Whether someone may contribute without proving who they are.
    ///
    /// <para>Chosen per code, at the moment it is generated, because it is a decision about a room
    /// rather than about a bucket: the code on the tables at the reception and the code in a
    /// follow-up email to the guest list are the same bucket and want opposite answers.</para>
    /// </summary>
    public bool AllowAnonymous { get; set; }

    /// <summary>What the owner called this code — "Reception tables", "Sent to the group chat".</summary>
    public string? Label { get; set; }

    /// <summary>Set when the owner turns it off. A printed code cannot be recalled, only refused.</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    public int ScanCount { get; set; }
    public int UploadCount { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// Somebody the owner has let into a bucket.
///
/// <para><b>Why a bucket needs its own list at all.</b> A bucket attached to a campaign gets one for
/// free — the campaign's guest list, which already says who was invited and is already matched to
/// accounts by verified contact. A STANDALONE bucket has no campaign and therefore had nobody at all:
/// exactly one account could see it, and the people who filled it could never look at what they had
/// filled. A trip or a reunion is not a private drive; it is the same "everyone who was there" the
/// event case promises.</para>
///
/// <para><b>Contributing does not put anyone here.</b> Adding and looking are different rights, and
/// the person who decides who looks at photographs of an occasion is the person whose occasion it
/// was. A printed code is handed to a room; membership is granted one contact at a time.</para>
/// </summary>
public sealed class MediaBucketMember
{
    public Guid Id { get; set; }
    public Guid BucketId { get; set; }

    /// <summary>
    /// The identifier they will prove: a lowercased email or an E.164 phone. Stored in the clear
    /// rather than hashed, unlike a suppression entry — the owner has to be able to read their own
    /// list back to manage it, and this is a list they typed.
    /// </summary>
    public string Contact { get; set; } = default!;

    /// <summary>"email" or "phone", so a match is never attempted across kinds.</summary>
    public string ContactType { get; set; } = default!;

    /// <summary>What the owner called them, for their own list. Never shown to other members.</summary>
    public string? Name { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
