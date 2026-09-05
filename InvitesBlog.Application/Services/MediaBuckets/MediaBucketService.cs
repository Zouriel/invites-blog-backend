using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Campaigns;
using InvitesBlog.Application.Dtos.MediaBuckets;
using InvitesBlog.Application.Events;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.MediaBuckets;
using InvitesBlog.Application.Phones;
using InvitesBlog.Application.Security;
using InvitesBlog.Application.Services.Campaigns;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;

namespace InvitesBlog.Application.Services.MediaBuckets;

/// <summary>
/// Media buckets: the thing a night's photographs land in, and the thing we sell.
///
/// <para>Two callers reach a bucket and they authorize completely differently. The OWNER holds the
/// account that bought it (or owns the event behind it) and may rename it, resize it, hand out codes
/// and moderate it. A CONTRIBUTOR holds a printed QR token and may do exactly one thing: add. The
/// token is never a way in to anything else, which is why it resolves to a bucket id here and never
/// to a session.</para>
/// </summary>
public interface IMediaBucketService
{
    /// <summary>Every bucket this account owns, newest first.</summary>
    Task<IReadOnlyList<MediaBucketDto>> MineAsync(CancellationToken ct = default);

    /// <summary>
    /// The bucket, for someone who may LOOK at it rather than manage it — the owner, or somebody the
    /// owner put on its list. Throws if the caller is neither.
    /// </summary>
    Task<MediaBucketDto> ViewAsync(Guid bucketId, CancellationToken ct = default);

    /// <summary>Whether this caller may look. Owner, campaign owner, or a member by verified contact.</summary>
    Task<bool> MayViewAsync(Guid bucketId, CancellationToken ct = default);

    /// <summary>Whether this caller may MANAGE it — the narrower right, and what moderation needs.</summary>
    Task<bool> OwnsAsync(Guid bucketId, CancellationToken ct = default);

    /// <summary>
    /// The guest row for a contact somebody has just PROVED on this bucket's event, or null if they
    /// are not on its guest list.
    ///
    /// <para>This is what a verified contribution code checks. Who may take part in an event is its
    /// guest list and nothing else — the credit on those photographs is then the name the host filed
    /// them under, because on a door that exists to demand proof a self-declared name would be the
    /// one unproved value left in the flow.</para>
    /// </summary>
    Task<Guest?> GuestForContactAsync(Guid bucketId, string contact, CancellationToken ct = default);

    Task<MediaBucketDto> GetAsync(Guid bucketId, CancellationToken ct = default);

    Task<MediaBucketDto> CreateAsync(CreateMediaBucketRequest req, CancellationToken ct = default);

    /// <summary>
    /// Moves the bucket onto a tier. <b>Payment is not wired up yet</b> — this grants the capacity and
    /// starts the term outright, so the shape of the product can be built and used before there is a
    /// checkout behind it. When one arrives, this is what it calls after it is paid.
    /// </summary>
    Task<MediaBucketDto> ChooseTierAsync(
        Guid bucketId, ChooseMediaBucketTierRequest req, CancellationToken ct = default);

    /// <summary>The sizes on offer, with this bucket's current one marked.</summary>
    Task<IReadOnlyList<MediaBucketPlanDto>> PlansAsync(Guid? bucketId, CancellationToken ct = default);

    /// <summary>
    /// The bucket a campaign's media goes into, creating it on the free tier the first time.
    ///
    /// <para>This is what keeps every event photo box working after buckets existed: a campaign that
    /// predates them has no row, so the first upload makes one rather than failing. Nobody is billed
    /// for what they already had.</para>
    /// </summary>
    Task<MediaBucket> ForCampaignAsync(Guid campaignId, CancellationToken ct = default);

    /// <summary>
    /// An event's bucket as its host sees it, or <c>null</c> when the event has none.
    ///
    /// <para><b>Deliberately does not create one.</b> Reading a dashboard is not asking for a bucket,
    /// and a read that quietly provisioned would mean every event ever opened acquires one whether
    /// its host wants it or not — and would make "add a media bucket" impossible to offer, because
    /// there would never be an event without one. Creating is <see cref="CreateForCampaignAsync"/>,
    /// and an upload still provisions through <see cref="ForCampaignAsync"/> so nothing that worked
    /// before needs anybody to press anything.</para>
    /// </summary>
    Task<MediaBucketDto?> ForCampaignOwnerAsync(Guid campaignId, CancellationToken ct = default);

    /// <summary>Gives an event a bucket, on purpose. Adopts whatever it already had.</summary>
    Task<MediaBucketDto> CreateForCampaignAsync(Guid campaignId, CancellationToken ct = default);

    /// <summary>
    /// Refuses an upload that would not fit, and is the ONLY place that decides that.
    /// </summary>
    /// <param name="incomingBytes">Everything the upload will write, derivatives included.</param>
    Task EnsureRoomAsync(Guid bucketId, long incomingBytes, CancellationToken ct = default);

    /// <summary>Records what an upload actually wrote. Called after the objects are stored.</summary>
    Task CountUsageAsync(Guid bucketId, long bytes, CancellationToken ct = default);

    /// <summary>
    /// Refuses an upload outside the bucket's night. The same window that decides whether a guest is
    /// offered the camera at all — see <see cref="EventDayWindow"/>.
    /// </summary>
    Task EnsureOpenAsync(Guid bucketId, CancellationToken ct = default);

    // ---------- QR codes ----------

    /// <summary>
    /// Makes a code, and returns the only copy of its token that will ever exist.
    /// </summary>
    Task<MediaBucketQrDto> CreateQrAsync(
        Guid bucketId, CreateMediaBucketQrRequest req, CancellationToken ct = default);

    /// <summary>
    /// The codes made for this bucket, newest first — the newest live one is what the dashboard
    /// keeps on show.
    /// </summary>
    Task<IReadOnlyList<MediaBucketQrDto>> QrsAsync(Guid bucketId, CancellationToken ct = default);

    Task RevokeQrAsync(Guid bucketId, Guid qrId, CancellationToken ct = default);

    /// <summary>
    /// Resolves a scanned token to the bucket it opens, or null. Records the scan.
    ///
    /// <para>Never throws for a bad token: whether a code is real is exactly what an attacker is
    /// asking, and a page that 404s uniformly answers nothing.</para>
    /// </summary>
    Task<MediaBucketQrAdmission?> AdmitAsync(string token, CancellationToken ct = default);

    /// <summary>
    /// The bucket row behind an admission, for the contribution path only.
    ///
    /// <para>Separate from <see cref="GetAsync"/> because that one answers "show me my bucket" and
    /// checks ownership to do it. A contributor owns nothing; they have already been admitted by a
    /// token, and the only thing still needed is which event — if any — the bucket belongs to, so the
    /// photograph is filed against it.</para>
    /// </summary>
    Task<MediaBucket?> GetBucketForContributionAsync(Guid bucketId, CancellationToken ct = default);

    /// <summary>Records that a code was actually used to add something, not merely scanned.</summary>
    Task CountContributionAsync(Guid qrId, CancellationToken ct = default);
}

/// <summary>What a valid scanned code admits someone to.</summary>
/// <param name="AllowAnonymous">Whether they may contribute without proving who they are.</param>
/// <param name="CanUpload">
/// Whether anything may be added right now — the night is open AND there is room. This is what the
/// contributor page hides its button on: an upload control that is going to be refused is worse than
/// no control, because somebody at a party will pick twenty photographs before finding out.
/// </param>
/// <param name="IsOpen">Whether it is the night, separately, so the page can say WHICH reason.</param>
public sealed record MediaBucketQrAdmission(
    Guid QrId, Guid BucketId, string BucketTitle, bool AllowAnonymous, bool CanUpload,
    bool IsOpen, DateTimeOffset EventDate);

/// <inheritdoc cref="IMediaBucketService"/>
public sealed class MediaBucketService(
    IRepository<MediaBucket> buckets,
    IRepository<MediaBucketQr> qrs,
    IRepository<AppUser> users,
    IRepository<EventPhoto> photos,
    ICampaignRepository campaigns,
    IGuestRepository guestRepository,
    ICampaignService campaignService,
    ICampaignOwnershipService ownership,
    ICurrentUser currentUser,
    IStorageService storage,
    IQrCodeRenderer qr,
    PhoneNormalizer phones,
    IConfiguration config,
    IOptions<MediaBucketOptions> options,
    IUnitOfWork uow) : IMediaBucketService
{
    private MediaBucketOptions Options => options.Value;

    /// <summary>
    /// Where a scanned code lands. The printed code has to carry an ABSOLUTE URL — it is read by a
    /// phone camera with no page around it to resolve a relative path against — so this is the one
    /// place in the product that deliberately bakes a hostname into what it stores. Getting it wrong
    /// is expensive in a way nothing else here is: the cards are already printed.
    /// </summary>
    private string ContributeBase => (config["Urls:InviterBase"] ?? "http://localhost:4200").TrimEnd('/');

    /// <summary>Enough of the token to tell two codes apart in a list, and nowhere near enough to use.</summary>
    private const int HintLength = 6;

    public async Task<IReadOnlyList<MediaBucketDto>> MineAsync(CancellationToken ct = default)
    {
        var userId = RequireUser();

        var mine = await buckets.Query()
            .Where(b => b.OwnerUserId == userId)
            .OrderByDescending(b => b.CreatedAt)
            .ToListAsync(ct);

        return await DescribeAsync(mine, ct);
    }

    public async Task<MediaBucketDto> GetAsync(Guid bucketId, CancellationToken ct = default)
    {
        var bucket = await OwnedAsync(bucketId, ct);
        return (await DescribeAsync([bucket], ct))[0];
    }

    public async Task<MediaBucketDto> ViewAsync(Guid bucketId, CancellationToken ct = default)
    {
        var bucket = await buckets.GetByIdAsync(bucketId, ct)
                     ?? throw new NotFoundException("That media bucket no longer exists.");

        if (!await MayViewAsync(bucketId, ct))
            throw new ForbiddenException("This bucket belongs to an event you're not on.");

        return (await DescribeAsync([bucket], ct))[0];
    }

    /// <summary>
    /// Looking is a wider right than managing, and deliberately a separate question.
    ///
    /// <para>Three ways in. The owner. Whoever owns the campaign behind it, if there is one — that is
    /// the same person by a different key. And anybody on the bucket's own list, matched on an
    /// identifier their account has actually PROVED, never on one they typed.</para>
    /// </summary>
    public async Task<bool> MayViewAsync(Guid bucketId, CancellationToken ct = default)
    {
        var bucket = await buckets.GetByIdAsync(bucketId, ct);
        if (bucket is null) return false;

        if (currentUser.UserId is { } me && bucket.OwnerUserId == me) return true;
        if (bucket.CampaignId is not { } campaignId) return false;
        if (await ownership.OwnsAsync(campaignId, ct)) return true;

        // The event's guest list IS who may look. One list, not two — an event that has both an
        // invitation and a bucket shares the same people between them, and a second list beside it
        // would be configuration that decides nothing.
        var proved = await MyContactsAsync(ct);
        if (proved.Count == 0) return false;

        var guests = await guestRepository.ListByCampaignAsync(campaignId, includeOptedOut: false, ct);
        return guests.Any(g => Matches(g, proved));
    }

    public async Task<Guest?> GuestForContactAsync(
        Guid bucketId, string contact, CancellationToken ct = default)
    {
        var bucket = await buckets.GetByIdAsync(bucketId, ct);
        if (bucket?.CampaignId is not { } campaignId) return null;

        var (normalized, _) = NormalizeContact(contact);
        var guests = await guestRepository.ListByCampaignAsync(campaignId, includeOptedOut: false, ct);
        return guests.FirstOrDefault(g => Matches(g, [normalized]));
    }

    /// <summary>
    /// Whether a guest row is one of the identifiers this caller has proved. Compared in the form
    /// each side is stored in — an email lowercased, a phone as E.164 — because the guest list is
    /// typed by a host and the proof comes from an account or a one-time code.
    /// </summary>
    private static bool Matches(Guest guest, IReadOnlyList<string> proved) =>
        (!string.IsNullOrWhiteSpace(guest.Email)
         && proved.Contains(guest.Email.Trim().ToLowerInvariant()))
        || (!string.IsNullOrWhiteSpace(guest.PhoneE164) && proved.Contains(guest.PhoneE164.Trim()));

    public async Task<bool> OwnsAsync(Guid bucketId, CancellationToken ct = default)
    {
        try
        {
            await OwnedAsync(bucketId, ct);
            return true;
        }
        catch (ForbiddenException)
        {
            return false;
        }
    }

    /// <summary>
    /// The identifiers this caller has PROVED — from their account, and from an OTP session where
    /// there is one. Never anything they merely typed: this is the whole of a member's authorization.
    /// </summary>
    private async Task<IReadOnlyList<string>> MyContactsAsync(CancellationToken ct)
    {
        var proved = new List<string>();

        if (currentUser.UserId is { } userId && await users.GetByIdAsync(userId, ct) is { } me)
        {
            if (!string.IsNullOrWhiteSpace(me.Email)) proved.Add(me.Email.Trim().ToLowerInvariant());
            if (!string.IsNullOrWhiteSpace(me.PhoneE164)) proved.Add(me.PhoneE164.Trim());
        }

        // A guest signed in with a one-time code holds a verified contact and no account at all.
        // Normalised the same way it was stored, rather than blanket-lowercased — a phone goes through
        // the normalizer so it is compared as E.164 against a member row that is also E.164.
        if (!string.IsNullOrWhiteSpace(currentUser.Contact))
        {
            var contact = currentUser.Contact.Trim();
            proved.Add(contact.Contains('@')
                ? contact.ToLowerInvariant()
                : phones.Normalize(contact) is { IsUsable: true, E164: { } e164 } ? e164 : contact);
        }

        return proved;
    }

    /// <summary>
    /// The form a contact is stored and compared in: an email lowercased, a phone in E.164.
    ///
    /// <para><b>The phone half has to go through <see cref="PhoneNormalizer"/>, not a space-strip.</b>
    /// The comparison this feeds is exact, and the other side of it is <c>AppUser.PhoneE164</c> — so
    /// an owner typing "7819157" or "960 781 9157" for somebody whose account proved
    /// "+9607819157" would produce a member row that could never match anybody, and phone membership
    /// would fail silently for every owner who used it. Same normalizer, same default region, as
    /// every other place phone identity is load-bearing.</para>
    /// </summary>
    private (string Contact, string Kind) NormalizeContact(string? raw)
    {
        var value = (raw ?? string.Empty).Trim();
        if (value.Length == 0)
            throw new BusinessRuleException("Add an email or a phone number.", "contact_required");

        if (value.Contains('@')) return (value.ToLowerInvariant(), "email");

        var phone = phones.Normalize(value);
        if (!phone.IsUsable || string.IsNullOrWhiteSpace(phone.E164))
            throw new BusinessRuleException(
                "That doesn't look like an email or a phone number.", "contact_invalid");

        return (phone.E164, "phone");
    }

    public async Task<MediaBucketDto> CreateAsync(
        CreateMediaBucketRequest req, CancellationToken ct = default)
    {
        var userId = RequireUser();

        if (string.IsNullOrWhiteSpace(req.Title))
            throw new BusinessRuleException("Give your event a name.", "title_required");

        // Attaching to an event has to be proved, not asserted. Otherwise anyone could hang a bucket
        // off somebody else's campaign and have its media appear on their dashboard.
        if (req.CampaignId is { } existing)
        {
            if (!await ownership.OwnsAsync(existing, ct))
                throw new ForbiddenException("That event isn't yours.");
            if (await buckets.AnyAsync(b => b.CampaignId == existing, ct))
                throw new BusinessRuleException(
                    "That event already has a media bucket.", "bucket_exists_for_campaign");
        }

        var plan = ParseTier(req.Tier) is { } tier
            ? MediaBucketPlans.For(tier, Options) ?? MediaBucketPlans.Free(Options)
            : MediaBucketPlans.Free(Options);

        // EVERY bucket belongs to a campaign, because the campaign is what holds the title, the
        // cover and the guest list — the three things a bucket deliberately has none of. A bucket
        // bought on its own is therefore a campaign with no invitation, not a loose object.
        var campaignId = req.CampaignId;
        if (campaignId is null)
        {
            if (req.EventDate is not { } night)
                throw new BusinessRuleException("When is it for?", "bucket_date_required");

            // The date the caller gave is the EVENT's, so the event is where it goes — and it goes
            // in as part of creating it, because a second call to set it would check ownership that
            // the caller cannot yet prove. Normalising to UTC happens there too.
            var created = await campaignService.CreateBareAsync(req.Title.Trim(), night, ct);
            campaignId = created.CampaignId;
        }

        // Always the campaign's date, so the invitation and the bucket can never disagree about
        // which night they belong to.
        var eventDate = (await campaigns.GetByIdAsync(campaignId.Value, ct))?.EventStartAt
                        ?? throw new NotFoundException("That event no longer exists.");

        var bucket = NewBucket(userId, campaignId, eventDate, plan);
        await buckets.AddAsync(bucket, ct);
        await uow.SaveChangesAsync(ct);

        return (await DescribeAsync([bucket], ct))[0];
    }

    public async Task<MediaBucketDto> ChooseTierAsync(
        Guid bucketId, ChooseMediaBucketTierRequest req, CancellationToken ct = default)
    {
        var bucket = await OwnedAsync(bucketId, ct, tracking: true);

        var tier = ParseTier(req.Tier)
                   ?? throw new BusinessRuleException("That isn't a size we offer.", "unknown_tier");
        var plan = MediaBucketPlans.For(tier, Options)
                   ?? throw new BusinessRuleException("That isn't a size we offer.", "unknown_tier");

        // Downwards is refused rather than silently accepted. A smaller bucket than what is already
        // in it would put someone permanently over their limit for photographs they have already
        // been told are safe, and there is no good answer to which of them to stop keeping.
        if (plan.CapacityBytes < bucket.UsedBytes)
            throw new BusinessRuleException(
                $"There's already more than {plan.Gb} GB in this bucket.", "tier_below_usage");

        bucket.Tier = plan.Tier;
        bucket.CapacityBytes = plan.CapacityBytes;

        if (plan.IsFree)
        {
            bucket.TermStartAt = null;
            bucket.TermEndAt = null;
        }
        else
        {
            var now = DateTimeOffset.UtcNow;
            // A term that is still running is EXTENDED, not restarted — someone topping up in month
            // five should not lose the month they still had.
            var from = bucket.TermEndAt is { } end && end > now ? end : now;
            bucket.TermStartAt ??= now;
            bucket.TermEndAt = from.AddMonths(plan.TermMonths);
        }

        bucket.UpdatedAt = DateTimeOffset.UtcNow;
        buckets.Update(bucket);
        await uow.SaveChangesAsync(ct);

        return (await DescribeAsync([bucket], ct))[0];
    }

    public async Task<IReadOnlyList<MediaBucketPlanDto>> PlansAsync(
        Guid? bucketId, CancellationToken ct = default)
    {
        var current = bucketId is { } id ? (await OwnedAsync(id, ct)).Tier : (MediaBucketTier?)null;

        return MediaBucketPlans.Purchasable(Options)
            .Select(p => new MediaBucketPlanDto(
                p.Tier.ToString(), p.Gb, p.Price, p.Currency, p.TermMonths, p.Tier == current))
            .ToList();
    }

    public async Task<MediaBucket> ForCampaignAsync(Guid campaignId, CancellationToken ct = default)
    {
        var existing = await buckets.FirstOrDefaultAsync(b => b.CampaignId == campaignId, ct);
        if (existing is not null) return existing;

        var campaign = await campaigns.GetByIdAsync(campaignId, ct)
                       ?? throw new NotFoundException("That event no longer exists.");

        // Provisioned for whoever the CAMPAIGN belongs to, not for whoever happens to be calling.
        //
        // The two are usually the same person but not always the same claim: campaign-scoped requests
        // carry the possession token in preference to the session (the browser sends whichever it
        // holds for that campaign, and the possession token wins), and that token proves ownership of
        // one campaign while saying nothing about which ACCOUNT is behind it. Reading the caller's id
        // there gave an ownerless bucket to a host who was signed in the whole time — invisible in
        // their own list of buckets, and reachable only through the event.
        //
        // It can still legitimately be nobody: a campaign booked with a possession link and never
        // claimed has no account behind it, and an empty owner is correct there rather than an error.
        var bucket = NewBucket(
            campaign.CreatedByUserId ?? currentUser.UserId ?? Guid.Empty,
            campaignId, campaign.EventStartAt, MediaBucketPlans.Free(Options));

        await buckets.AddAsync(bucket, ct);

        // ADOPT what the campaign already had. Photographs that predate buckets carry no bucket id,
        // and a bucket that ignored them would under-report its own contents forever: the dashboard
        // would show a box of eleven while the bucket page showed none, and the quota would be
        // measured against a fraction of what is actually stored.
        //
        // Their bytes count. Every byte here is being stored on somebody's behalf whether it arrived
        // before or after the row existed, and a usage figure that quietly omits most of a bucket is
        // worse than no figure. Soft-deleted rows are counted too, for the same reason UsedBytes does
        // not go down on a delete — the objects behind them outlive the row.
        var inherited = await photos.Query(tracking: true)
            .Where(p => p.CampaignId == campaignId && p.BucketId == null)
            .ToListAsync(ct);

        foreach (var photo in inherited)
        {
            photo.BucketId = bucket.Id;
            photos.Update(photo);
        }
        bucket.UsedBytes = inherited.Sum(p => p.SizeBytes);

        await uow.SaveChangesAsync(ct);
        return bucket;
    }

    public async Task<MediaBucketDto?> ForCampaignOwnerAsync(
        Guid campaignId, CancellationToken ct = default)
    {
        if (!await ownership.OwnsAsync(campaignId, ct))
            throw new ForbiddenException("That event isn't yours.");

        var bucket = await buckets.FirstOrDefaultAsync(b => b.CampaignId == campaignId, ct);

        // A campaign that ALREADY HOLDS MEDIA has a bucket in every sense the host cares about —
        // only the row is missing, because those photographs predate buckets existing. Offering to
        // "add a media bucket" to an event whose photographs are on the screen underneath the offer
        // is nonsense, so the row is created to catch up with what is already true. Provisioning
        // adopts them, so it opens showing its real contents and its real usage.
        //
        // An event with nothing in it still gets no bucket and no row: that one is a real choice,
        // and it is the case the offer exists for.
        if (bucket is null)
        {
            var hasMedia = await photos.AnyAsync(
                p => p.CampaignId == campaignId && p.DeletedAt == null, ct);
            if (!hasMedia) return null;

            bucket = await ForCampaignAsync(campaignId, ct);
        }

        return (await DescribeAsync([bucket], ct))[0];
    }

    public async Task<MediaBucketDto> CreateForCampaignAsync(
        Guid campaignId, CancellationToken ct = default)
    {
        if (!await ownership.OwnsAsync(campaignId, ct))
            throw new ForbiddenException("That event isn't yours.");

        var bucket = await ForCampaignAsync(campaignId, ct);
        return (await DescribeAsync([bucket], ct))[0];
    }

    public async Task EnsureRoomAsync(Guid bucketId, long incomingBytes, CancellationToken ct = default)
    {
        var bucket = await buckets.GetByIdAsync(bucketId, ct)
                     ?? throw new NotFoundException("That media bucket no longer exists.");

        if (bucket.UsedBytes + incomingBytes <= bucket.CapacityBytes) return;

        var gb = bucket.CapacityBytes / (double)MediaBucketPlans.BytesPerGb;
        throw new BusinessRuleException(
            bucket.Tier == MediaBucketTier.Free
                ? $"This bucket's free {gb:0.#} GB is full. Choose a bucket size to keep adding."
                : $"This bucket's {gb:0.#} GB is full.",
            "bucket_full");
    }

    public async Task EnsureOpenAsync(Guid bucketId, CancellationToken ct = default)
    {
        var bucket = await buckets.GetByIdAsync(bucketId, ct)
                     ?? throw new NotFoundException("That media bucket no longer exists.");

        if (EventDayWindow.IsOpen(bucket.EventDate, DateTimeOffset.UtcNow)) return;

        // Which side of it they are on, because "closed" means two completely different things to
        // somebody standing at the party a day early and somebody looking a week later.
        throw new BusinessRuleException(
            DateTimeOffset.UtcNow < bucket.EventDate
                ? "This one isn't open yet — it opens on the day."
                : "This one has closed. Everything already added is still here.",
            "bucket_closed");
    }

    public async Task CountUsageAsync(Guid bucketId, long bytes, CancellationToken ct = default)
    {
        var bucket = await buckets.Query(tracking: true).FirstOrDefaultAsync(b => b.Id == bucketId, ct);
        if (bucket is null) return;

        bucket.UsedBytes += bytes;
        bucket.UpdatedAt = DateTimeOffset.UtcNow;
        buckets.Update(bucket);
        await uow.SaveChangesAsync(ct);
    }

    // ---------- QR codes ----------

    public async Task<MediaBucketQrDto> CreateQrAsync(
        Guid bucketId, CreateMediaBucketQrRequest req, CancellationToken ct = default)
    {
        await OwnedAsync(bucketId, ct);

        var token = TokenService.GenerateToken();
        var id = Guid.NewGuid();
        var url = ContributeUrl(token);

        // Rendered and stored NOW, while the token is still in hand. After this method returns
        // nothing can redraw it — the token is only kept hashed — and that is the point: the picture
        // is what stays available to the dashboard forever, not the secret it encodes.
        var image = await storage.PutAsync(
            $"buckets/{bucketId:N}/qr/{id:N}.png", qr.Png(url), "image/png", ct);

        var code = new MediaBucketQr
        {
            Id = id,
            BucketId = bucketId,
            TokenHash = TokenService.Hash(token),
            TokenHint = token[..HintLength],
            ImageUrl = image,
            AllowAnonymous = req.AllowAnonymous,
            Label = string.IsNullOrWhiteSpace(req.Label) ? null : req.Label.Trim(),
            CreatedAt = DateTimeOffset.UtcNow,
        };

        await qrs.AddAsync(code, ct);
        await uow.SaveChangesAsync(ct);

        // The one and only time the token leaves this method.
        return Describe(code, url);
    }

    public async Task<IReadOnlyList<MediaBucketQrDto>> QrsAsync(
        Guid bucketId, CancellationToken ct = default)
    {
        await OwnedAsync(bucketId, ct);

        var rows = await qrs.Query()
            .Where(q => q.BucketId == bucketId)
            .OrderByDescending(q => q.CreatedAt)
            .ToListAsync(ct);

        return rows.Select(row => Describe(row, url: null)).ToList();
    }

    public async Task RevokeQrAsync(Guid bucketId, Guid qrId, CancellationToken ct = default)
    {
        await OwnedAsync(bucketId, ct);

        var qr = await qrs.Query(tracking: true)
            .FirstOrDefaultAsync(q => q.Id == qrId && q.BucketId == bucketId, ct)
            ?? throw new NotFoundException("That code no longer exists.");

        if (qr.RevokedAt is not null) return;   // revoking twice is not an error

        qr.RevokedAt = DateTimeOffset.UtcNow;
        qrs.Update(qr);
        await uow.SaveChangesAsync(ct);
    }

    public async Task<MediaBucketQrAdmission?> AdmitAsync(string token, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;

        // Matched on the hash, which is what is indexed — never by reading every code and comparing.
        var hash = TokenService.Hash(token);
        var code = await qrs.Query(tracking: true).FirstOrDefaultAsync(q => q.TokenHash == hash, ct);
        if (code is null || code.RevokedAt is not null) return null;

        var bucket = await buckets.GetByIdAsync(code.BucketId, ct);
        if (bucket is null) return null;

        code.ScanCount++;
        code.LastUsedAt = DateTimeOffset.UtcNow;
        qrs.Update(code);
        await uow.SaveChangesAsync(ct);

        // A full or closed bucket still ADMITS — the page has to open in order to say why nothing can
        // be added. Refusing at the door would show a scanner a dead link and tell them nothing.
        var room = bucket.UsedBytes < bucket.CapacityBytes;
        var open = EventDayWindow.IsOpen(bucket.EventDate, DateTimeOffset.UtcNow);

        // The name a scanner is shown is the EVENT's — the bucket has none of its own. This is what
        // somebody standing at a party reads to know they are adding to the right night.
        var title = bucket.CampaignId is { } cid
            ? (await campaigns.GetByIdAsync(cid, ct))?.Title ?? "Media bucket"
            : "Media bucket";

        return new MediaBucketQrAdmission(
            code.Id, bucket.Id, title, code.AllowAnonymous, room && open, open, bucket.EventDate);
    }

    public async Task<MediaBucket?> GetBucketForContributionAsync(
        Guid bucketId, CancellationToken ct = default) =>
        await buckets.GetByIdAsync(bucketId, ct);

    public async Task CountContributionAsync(Guid qrId, CancellationToken ct = default)
    {
        var code = await qrs.Query(tracking: true).FirstOrDefaultAsync(q => q.Id == qrId, ct);
        if (code is null) return;

        code.UploadCount++;
        code.LastUsedAt = DateTimeOffset.UtcNow;
        qrs.Update(code);
        await uow.SaveChangesAsync(ct);
    }

    // ---------- shared ----------

    private MediaBucket NewBucket(
        Guid ownerId, Guid? campaignId, DateTimeOffset eventDate, MediaBucketPlan plan)
    {
        var now = DateTimeOffset.UtcNow;
        return new MediaBucket
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerId,
            CampaignId = campaignId,
            EventDate = eventDate.ToUniversalTime(),
            Tier = plan.Tier,
            CapacityBytes = plan.CapacityBytes,
            UsedBytes = 0,
            TermStartAt = plan.IsFree ? null : now,
            TermEndAt = plan.IsFree ? null : now.AddMonths(plan.TermMonths),
            CreatedAt = now,
            UpdatedAt = now,
        };
    }

    /// <summary>
    /// The owner's door. Two ways through it, because a bucket can be reached before its owner has an
    /// account: the account that owns the row, or whoever can prove they own the event behind it.
    /// </summary>
    private async Task<MediaBucket> OwnedAsync(
        Guid bucketId, CancellationToken ct, bool tracking = false)
    {
        var bucket = tracking
            ? await buckets.Query(tracking: true).FirstOrDefaultAsync(b => b.Id == bucketId, ct)
            : await buckets.GetByIdAsync(bucketId, ct);

        if (bucket is null) throw new NotFoundException("That media bucket no longer exists.");

        if (currentUser.UserId is { } me && bucket.OwnerUserId == me) return bucket;
        if (bucket.CampaignId is { } campaignId && await ownership.OwnsAsync(campaignId, ct))
            return bucket;

        throw new ForbiddenException("That media bucket isn't yours.");
    }

    private Guid RequireUser() =>
        currentUser.UserId ?? throw new ForbiddenException("Sign in to manage media buckets.");

    private async Task<IReadOnlyList<MediaBucketDto>> DescribeAsync(
        IReadOnlyList<MediaBucket> rows, CancellationToken ct)
    {
        if (rows.Count == 0) return [];

        var ids = rows.Select(b => b.Id).ToList();

        // One grouped count for the whole page rather than a query per bucket — the list is drawn
        // for every bucket someone owns, and per-row queries are how that becomes slow quietly.
        var counts = await photos.Query()
            .Where(p => p.BucketId != null && ids.Contains(p.BucketId!.Value) && p.DeletedAt == null)
            .GroupBy(p => p.BucketId!.Value)
            .Select(g => new { BucketId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.BucketId, x => x.Count, ct);

        // The title and the cover are the CAMPAIGN's. A bucket holds neither — it would be a second
        // answer to a question the event already answers, and the two would drift the moment somebody
        // renamed one of them.
        var campaignIds = rows.Where(b => b.CampaignId is not null)
            .Select(b => b.CampaignId!.Value).Distinct().ToList();
        var events = campaignIds.Count == 0
            ? []
            : await campaigns.Query()
                .Where(c => campaignIds.Contains(c.Id))
                .Select(c => new EventFace(c.Id, c.Title, c.CustomContentJson))
                .ToDictionaryAsync(x => x.Id, x => x, ct);

        var now = DateTimeOffset.UtcNow;
        return rows.Select(b => new MediaBucketDto(
            b.Id,
            Title(b, events),
            Cover(b, events),
            b.Tier.ToString(),
            (int)Math.Round(b.CapacityBytes / (double)MediaBucketPlans.BytesPerGb),
            b.CapacityBytes,
            b.UsedBytes,
            b.CapacityBytes <= 0
                ? 0
                : (int)Math.Clamp(Math.Round(b.UsedBytes * 100.0 / b.CapacityBytes), 0, 100),
            counts.GetValueOrDefault(b.Id),
            b.CampaignId,
            Title(b, events),
            b.EventDate,
            EventDayWindow.IsOpen(b.EventDate, now),
            b.TermEndAt,
            b.TermEndAt is { } end && end <= now,
            b.CreatedAt)).ToList();
    }

    private string ContributeUrl(string token) => $"{ContributeBase}/q/{token}";

    /// <summary>
    /// <paramref name="url"/> is non-null only for a code just created — see
    /// <see cref="CreateQrAsync"/>. Every later read has the image and not the link, which is
    /// sufficient: the picture is the thing a host reprints, and it still scans.
    /// </summary>
    /// <summary>How an event presents itself: the two things a bucket used to duplicate.</summary>
    private sealed record EventFace(Guid Id, string Title, string? CustomContentJson);

    /// <summary>
    /// The event's name. A bucket has none of its own; where there is somehow no campaign behind one
    /// — only possible for a row predating the rule — it falls back to something sayable rather than
    /// to an empty string in the middle of somebody's list.
    /// </summary>
    private static string Title(MediaBucket bucket, IReadOnlyDictionary<Guid, EventFace> events) =>
        bucket.CampaignId is { } id && events.TryGetValue(id, out var face) ? face.Title : "Media bucket";

    /// <summary>The event's cover, read from where the host's own choice is kept.</summary>
    private static string? Cover(MediaBucket bucket, IReadOnlyDictionary<Guid, EventFace> events) =>
        bucket.CampaignId is { } id && events.TryGetValue(id, out var face)
            ? CampaignCover.Read(face.CustomContentJson)
            : null;

    private static MediaBucketQrDto Describe(MediaBucketQr code, string? url) => new(
        code.Id,
        url,
        code.ImageUrl,
        code.Label,
        code.AllowAnonymous,
        code.TokenHint,
        code.ScanCount,
        code.UploadCount,
        code.RevokedAt is not null,
        code.LastUsedAt,
        code.CreatedAt);

    private static MediaBucketTier? ParseTier(string? raw) =>
        Enum.TryParse<MediaBucketTier>(raw, ignoreCase: true, out var tier) ? tier : null;
}
