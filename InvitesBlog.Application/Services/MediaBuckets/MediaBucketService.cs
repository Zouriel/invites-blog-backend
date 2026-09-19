using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Campaigns;
using InvitesBlog.Application.Dtos.MediaBuckets;
using InvitesBlog.Application.Events;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.MediaBuckets;
using InvitesBlog.Application.Phones;
using InvitesBlog.Application.Security;
using InvitesBlog.Application.Plans;
using InvitesBlog.Application.Services.Campaigns;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using InvitesBlog.Application.Common;

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
    /// The bucket a campaign's media goes into, creating it on the free tier the first time.
    ///
    /// <para>This is what keeps every event photo box working after buckets existed: a campaign that
    /// predates them has no row, so the first upload makes one rather than failing. Nobody is billed
    /// for what they already had.</para>
    /// </summary>
    Task<MediaBucket> ForCampaignAsync(Guid campaignId, CancellationToken ct = default);

    /// <summary>Where the event's photos are in their lifetime. See MediaPhase.</summary>
    Task<MediaPhase> PhaseForCampaignAsync(Guid campaignId, CancellationToken ct = default);

    /// <summary>
    /// How many days this event's default bucket collects for — 1 when it has no bucket yet.
    ///
    /// <para>Separate from <see cref="ForCampaignAsync"/> because the callers are READS: whether to
    /// offer a guest the camera, and whether the box says it is closed. Asking the provisioning path
    /// would create a bucket as a side effect of drawing a page, for every event anybody looked at.</para>
    /// </summary>
    Task<int> WindowForCampaignAsync(Guid campaignId, CancellationToken ct = default);

    /// <summary>
    /// Renames a bucket.
    ///
    /// <para>Gated on the same right as keeping more than one, because that is the only situation
    /// the name exists for: somebody with a single bucket has nothing to tell it apart FROM, and the
    /// default already reads correctly on their dashboard.</para>
    /// </summary>
    Task<MediaBucketDto> RenameAsync(
        Guid bucketId, RenameMediaBucketRequest req, CancellationToken ct = default);

    /// <summary>
    /// Changes how many days a bucket collects for. Up to what the event's plan allows now — so a
    /// bucket made on the free night can be stretched after buying a pass — or up to
    /// what it already has, since a window already given is never taken away by a plan ending.
    /// </summary>
    Task<MediaBucketDto> SetWindowAsync(Guid bucketId, SetBucketWindowRequest req, CancellationToken ct = default);

    /// <summary>
    /// Brings every album on an event up to the days its plan now allows — called when a pass is
    /// given, so a Wedding pass means five days without the host changing anything. Never shortens
    /// one: a window already given is kept.
    /// </summary>
    Task RaiseWindowsToPlanAsync(Guid campaignId, CancellationToken ct = default);

    /// <summary>What all of an event's albums hold together.</summary>
    Task<long> EventUsedBytesAsync(Guid campaignId, CancellationToken ct = default);

    /// <summary>The space a venue's events share, for the venue's owner and staff. Empty for everyone else.</summary>
    Task<StorageSummaryDto> StorageSummaryAsync(CancellationToken ct = default);
    /// <summary>Every guest on the bucket's event, with whether they may see it.</summary>
    Task<BucketAccessDto> AccessAsync(Guid bucketId, CancellationToken ct = default);

    /// <summary>Allows or stops some guests from seeing the bucket. See the implementation for the two states.</summary>
    Task<BucketAccessDto> SetAccessAsync(Guid bucketId, SetBucketAccessRequest req, CancellationToken ct = default);
    /// <summary>The buckets on an event that the CALLER may look into. Their own view, not the owner's.</summary>
    /// <summary>
    /// Which of the event's buckets <paramref name="guestId"/> may look into, answered from the guest
    /// row rather than from the caller's proved contacts. The campaign photo box is reached by guests
    /// who hold no account at all (the render cookie names the guest), so it cannot ask "who is
    /// signed in"; the caller must already have established that the guest is on this event.
    /// </summary>
    Task<GuestBucketView> GuestViewAsync(Guid campaignId, Guid guestId, CancellationToken ct = default);

    Task<IReadOnlyList<MediaBucketDto>> VisibleForCampaignAsync(
        Guid campaignId, CancellationToken ct = default);

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
    /// Deletes an event's buckets and everything in them. For a cancelled event: its photos go at once
    /// rather than waiting out a plan, and the space they were given goes back to the account.
    /// </summary>
    Task RemoveForCampaignAsync(Guid campaignId, CancellationToken ct = default);

    /// <summary>
    /// Refuses an upload that would not fit, and is the ONLY place that decides that.
    /// </summary>
    /// <param name="incomingBytes">Everything the upload will write, derivatives included.</param>
    Task EnsureRoomAsync(Guid bucketId, long incomingBytes, CancellationToken ct = default);

    /// <summary>
    /// <see cref="EnsureRoomAsync"/> and the claim on the space as ONE step: checks the upload fits and
    /// counts <paramref name="bytes"/> against the bucket before a single object is written, holding a
    /// lock that every other upload against the same space waits for. What an upload path calls —
    /// checking and counting separately let parallel uploads all pass the check on the same figure.
    /// Settle it afterwards with <see cref="CountUsageAsync"/>: the difference to what was really
    /// stored, or the whole reservation back if the upload failed.
    /// </summary>
    Task ReserveRoomAsync(Guid bucketId, long bytes, CancellationToken ct = default);

    /// <summary>
    /// Adds <paramref name="bytes"/> to what the bucket holds, atomically — or, when negative, gives
    /// some back (never below zero).
    /// </summary>
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

/// <summary>
/// Which of an event's photographs one guest may see, by bucket.
/// </summary>
/// <param name="BucketIds">The event's buckets this guest is allowed to look into.</param>
/// <param name="SeesUnbucketed">
/// Whether they may see photographs that carry no bucket at all. Those predate buckets and belong to
/// the event's DEFAULT bucket (its oldest), so they follow that bucket's audience — and an event with
/// no bucket yet has nothing restricting them.
/// </param>
public sealed record GuestBucketView(IReadOnlySet<Guid> BucketIds, bool SeesUnbucketed)
{
    public bool MaySee(Guid? bucketId) => bucketId is { } id ? BucketIds.Contains(id) : SeesUnbucketed;
}

/// <summary>What a valid scanned code admits someone to.</summary>
/// <param name="AllowAnonymous">Whether they may contribute without proving who they are.</param>
/// <param name="CanUpload">
/// Whether anything may be added right now — the night is open AND there is room. This is what the
/// contributor page hides its button on: an upload control that is going to be refused is worse than
/// no control, because somebody at a party will pick twenty photographs before finding out.
/// </param>
/// <param name="IsOpen">Whether it is the night, separately, so the page can say WHICH reason.</param>
/// <param name="Branded">A Free event: the page carries a small "Made with invites.blog".</param>
/// <param name="VenueName">The venue the event is at, whose name and logo the page carries.</param>
public sealed record MediaBucketQrAdmission(
    Guid QrId, Guid BucketId, string BucketTitle, bool AllowAnonymous, bool CanUpload,
    bool IsOpen, DateTimeOffset EventDate, bool Branded = true, string? VenueName = null, string? VenueLogoUrl = null,
    DateTimeOffset? OpensAt = null, DateTimeOffset? ClosesAt = null);

/// <inheritdoc cref="IMediaBucketService"/>
public sealed class MediaBucketService(
    IRepository<MediaBucket> buckets,
    IRepository<MediaBucketQr> qrs,
    IRepository<AppUser> users,
    IRepository<EventPhoto> photos,
    ICampaignRepository campaigns,
    IGuestRepository guestRepository,
    IRepository<MediaBucketMember> members,
    ICampaignService campaignService,
    ICampaignOwnershipService ownership,
    ICurrentUser currentUser,
    IStorageService storage,
    IQrCodeRenderer qr,
    PhoneNormalizer phones,
    IConfiguration config,
    IUnitOfWork uow,
    IPlanService plans,
    IRepository<Venue> venues,
    IRepository<VenueStaff> venueStaff,
    IMediaBucketUsageRepository usage) : IMediaBucketService
{
    /// <summary>
    /// Where a scanned code lands. The printed code has to carry an ABSOLUTE URL — it is read by a
    /// phone camera with no page around it to resolve a relative path against — so this is the one
    /// place in the product that deliberately bakes a hostname into what it stores. Getting it wrong
    /// is expensive in a way nothing else here is: the cards are already printed.
    /// </summary>
    private string ContributeBase => config.InviterBase();

    /// <summary>Enough of the token to tell two codes apart in a list, and nowhere near enough to use.</summary>
    private const int HintLength = 6;

    public async Task<MediaBucketDto> GetAsync(Guid bucketId, CancellationToken ct = default)
    {
        var bucket = await OwnedAsync(bucketId, ct);
        return (await DescribeAsync([bucket], ct))[0];
    }

    public async Task<MediaBucketDto> ViewAsync(Guid bucketId, CancellationToken ct = default)
    {
        var bucket = await buckets.GetByIdAsync(bucketId, ct)
                     ?? throw new NotFoundException("That album no longer exists.");

        if (!await MayViewAsync(bucketId, ct))
            throw new ForbiddenException("This album belongs to an event you're not on.");

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
        var campaignId = bucket.CampaignId;

        // After a plan has run out for 30 days only the organiser can still look; after 90 the photos are gone.
        var phase = (await plans.ForCampaignAsync(campaignId, ct)).Phase;
        if (phase == MediaPhase.Deleted) return false;
        if (phase == MediaPhase.OrganiserOnly)
            return await ownership.AccessAsync(campaignId, ct) >= CampaignAccess.Manager;

        // The organiser, and everyone the event is for, see every bucket on it.
        if (await ownership.AccessAsync(campaignId, ct) >= CampaignAccess.Celebrant) return true;

        // The event's guest list is who may look — drawn FROM, never copied. A bucket that has not
        // been restricted is open to all of it, which is what every bucket predating members is.
        var guest = await GuestOnThisEventAsync(campaignId, ct);
        if (guest is null) return false;
        if (!bucket.IsRestricted) return true;

        // Restricted: named guests only, and being on the event is no longer enough.
        return await members.AnyAsync(
            m => m.BucketId == bucket.Id && m.GuestId == guest.Id, ct);
    }

    public async Task<Guest?> GuestForContactAsync(
        Guid bucketId, string contact, CancellationToken ct = default)
    {
        var bucket = await buckets.GetByIdAsync(bucketId, ct);
        if (bucket is null) return null;
        var campaignId = bucket.CampaignId;

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
            var already = await buckets.CountAsync(b => b.CampaignId == existing, ct);
            var existingPlan = await plans.ForCampaignAsync(existing, ct);

            // More albums on one event come with a pass: the ceremony and the after-party, each with
            // its own night and its own audience. Two with a Party pass, five with a Wedding pass.
            if (already > 0 && already >= existingPlan.MaxBuckets && existingPlan.MaxBuckets < MediaBucket.MaxPerCampaign)
                throw new BusinessRuleException(
                    existingPlan.MaxBuckets <= 1
                        ? "That event already has an album. More than one comes with a Party or Wedding pass."
                        : $"That event already has {already} albums, the most its Party pass allows. A Wedding pass allows {MediaBucket.MaxPerCampaign}.",
                    "bucket_exists_for_campaign");

            // And a ceiling above that, which no plan lifts. See MediaBucket.MaxPerCampaign.
            if (already >= MediaBucket.MaxPerCampaign)
                throw new BusinessRuleException(
                    $"An event can hold {MediaBucket.MaxPerCampaign} albums at most. Remove "
                    + "one, or give the extra night an event of its own.",
                    "bucket_limit_reached");
        }

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

        // Settled either way by here: given by the caller, or the bare campaign just made for it.
        var eventId = campaignId.Value;

        // Collecting for longer than the one night comes with a pass. Asked for
        // here and FROZEN onto the row, so a pass running out later cannot shut a bucket
        // somebody has already printed codes for — see MediaBucket.UploadWindowDays.
        var eventPlan = await plans.ForCampaignAsync(eventId, ct);
        // Not asked: the most the plan allows, which is what a pass was bought for.
        var windowDays = WindowFor(req.WindowDays ?? eventPlan.MaxWindowDays, eventPlan.MaxWindowDays);

        var target = await campaigns.GetByIdAsync(eventId, ct)
                     ?? throw new NotFoundException("That event no longer exists.");
        if (target.Status == CampaignStatus.Cancelled)
            throw new BusinessRuleException("This event was cancelled, so it has no photo space.", "event_cancelled");
        var campaignDate = target.EventStartAt;

        // The event's DEFAULT bucket always takes the event's own date, so the invitation and the
        // bucket its camera posts to can never disagree about which night they belong to. A SECOND
        // bucket may carry its own, because that is what a second one is FOR — an evening that is
        // really two, a ceremony and an after-party that do not share a date. Without this they are
        // also indistinguishable in a list, since a bucket takes its title from the event and two on
        // one event therefore read identically.
        var isFirst = !await buckets.AnyAsync(b => b.CampaignId == eventId, ct);
        var eventDate = isFirst ? campaignDate : req.EventDate ?? campaignDate;

        var bucket = NewBucket(userId, eventId, eventDate, windowDays);

        var chosenName = req.Name?.Trim();
        if (!string.IsNullOrWhiteSpace(chosenName) && eventPlan.MaxBuckets > 1)
        {
            bucket.Name = chosenName.Length > 80 ? chosenName[..80] : chosenName;
        }
        else if (!isFirst)
        {
            // A second bucket cannot ALSO be "Night's bucket". Two rows with one name is the exact
            // confusion the name was added to remove — the panel for editing a guest would offer two
            // identical switches. Numbered from how many the event already has, and the owner
            // renames it to something that means something the moment they care.
            var already = await buckets.CountAsync(b => b.CampaignId == eventId, ct);
            bucket.Name = $"{MediaBucket.DefaultName} {already + 1}";
        }
        await buckets.AddAsync(bucket, ct);
        await uow.SaveChangesAsync(ct);

        return (await DescribeAsync([bucket], ct))[0];
    }

    public async Task<BucketAccessDto> AccessAsync(Guid bucketId, CancellationToken ct = default)
    {
        var bucket = await OwnedAsync(bucketId, ct);
        return await DescribeAccessAsync(bucket, ct);
    }

    /// <summary>
    /// Allows or stops some of the event's guests from seeing one bucket.
    ///
    /// <para>The bucket keeps two states. While everyone is allowed it holds no rows at all and is
    /// open to the whole guest list, including anyone added later. The first guest switched off
    /// closes it: rows are written for everybody still allowed, and from then on a new guest starts
    /// switched off. Allowing everyone again reopens it and clears the rows, so new guests are let in
    /// again. Either way the switches on screen are exactly who can look.</para>
    /// </summary>
    public async Task<BucketAccessDto> SetAccessAsync(
        Guid bucketId, SetBucketAccessRequest req, CancellationToken ct = default)
    {
        var bucket = await OwnedAsync(bucketId, ct, tracking: true);

        // Closing an album to some guests comes with a Wedding pass or a venue. Opening one again is
        // always allowed, so an album closed before a pass ran out can still be given back to everyone.
        if (!req.Allowed && !(await plans.ForCampaignAsync(bucket.CampaignId, ct)).PrivateAlbums)
            throw new BusinessRuleException(
                "Private albums, that only some guests can see, come with a Wedding pass.", "private_needs_plan");

        var guests = await guestRepository.ListByCampaignAsync(bucket.CampaignId, includeOptedOut: true, ct);
        var everyone = guests.Select(g => g.Id).ToHashSet();

        var rows = await members.Query(tracking: true).Where(m => m.BucketId == bucket.Id).ToListAsync(ct);
        var allowed = bucket.IsRestricted ? rows.Select(m => m.GuestId).ToHashSet() : new HashSet<Guid>(everyone);

        foreach (var id in (req.GuestIds ?? []).Where(everyone.Contains))
        {
            if (req.Allowed) allowed.Add(id);
            else allowed.Remove(id);
        }

        var open = allowed.SetEquals(everyone);
        foreach (var row in rows.Where(r => open || !allowed.Contains(r.GuestId))) members.Remove(row);
        if (!open)
        {
            var have = rows.Select(r => r.GuestId).ToHashSet();
            var now = DateTimeOffset.UtcNow;
            foreach (var id in allowed.Where(id => !have.Contains(id)))
                await members.AddAsync(new MediaBucketMember
                {
                    BucketId = bucket.Id, GuestId = id, CampaignId = bucket.CampaignId, AddedAt = now,
                }, ct);
        }

        if (bucket.IsRestricted == open)
        {
            bucket.IsRestricted = !open;
            bucket.UpdatedAt = DateTimeOffset.UtcNow;
            buckets.Update(bucket);
        }

        await uow.SaveChangesAsync(ct);
        return await DescribeAccessAsync(bucket, ct);
    }

    private async Task<BucketAccessDto> DescribeAccessAsync(MediaBucket bucket, CancellationToken ct)
    {
        var guests = await guestRepository.ListByCampaignAsync(bucket.CampaignId, includeOptedOut: true, ct);
        var admitted = bucket.IsRestricted
            ? (await members.Query().Where(m => m.BucketId == bucket.Id).Select(m => m.GuestId).ToListAsync(ct))
                .ToHashSet()
            : null;

        return new BucketAccessDto(
            bucket.Id,
            bucket.IsRestricted,
            guests.OrderBy(g => g.Name)
                .Select(g => new BucketAccessGuestDto(g.Id, g.Name, g.AllRoles(), admitted is null || admitted.Contains(g.Id)))
                .ToList());
    }

    public async Task<IReadOnlyList<MediaBucketDto>> VisibleForCampaignAsync(
        Guid campaignId, CancellationToken ct = default)
    {
        var rows = await buckets.Query()
            .Where(b => b.CampaignId == campaignId)
            .OrderBy(b => b.CreatedAt).ThenBy(b => b.Id)
            .ToListAsync(ct);
        if (rows.Count == 0) return [];

        // The owner sees all of them; that is the same door as MayViewAsync's first two checks.
        if ((currentUser.UserId is { } me && rows[0].OwnerUserId == me)
            || await ownership.AccessAsync(campaignId, ct) >= CampaignAccess.Celebrant)
            return await DescribeAsync(rows, ct);

        var guest = await GuestOnThisEventAsync(campaignId, ct);
        if (guest is null) return [];

        var admitted = await members.Query()
            .Where(m => m.CampaignId == campaignId && m.GuestId == guest.Id)
            .Select(m => m.BucketId)
            .ToListAsync(ct);

        var mine = rows.Where(b => !b.IsRestricted || admitted.Contains(b.Id)).ToList();
        return mine.Count == 0 ? [] : await DescribeAsync(mine, ct);
    }

    public async Task<GuestBucketView> GuestViewAsync(
        Guid campaignId, Guid guestId, CancellationToken ct = default)
    {
        // Oldest first, the same order ForCampaignAsync uses to name the DEFAULT bucket.
        var rows = await buckets.Query()
            .Where(b => b.CampaignId == campaignId)
            .OrderBy(b => b.CreatedAt).ThenBy(b => b.Id)
            .Select(b => new { b.Id, b.IsRestricted })
            .ToListAsync(ct);

        // No bucket yet: nothing has been restricted, so the whole (unbucketed) box is theirs to see.
        if (rows.Count == 0) return new GuestBucketView(new HashSet<Guid>(), SeesUnbucketed: true);

        var admitted = (await members.Query()
                .Where(m => m.CampaignId == campaignId && m.GuestId == guestId)
                .Select(m => m.BucketId)
                .ToListAsync(ct))
            .ToHashSet();

        // The same rule as MayViewAsync and VisibleForCampaignAsync: open unless restricted, and a
        // restricted bucket admits only the guests named on it.
        var visible = rows.Where(b => !b.IsRestricted || admitted.Contains(b.Id)).Select(b => b.Id).ToHashSet();
        return new GuestBucketView(visible, SeesUnbucketed: visible.Contains(rows[0].Id));
    }

    /// <summary>The caller's guest row on this event, matched on an identifier they have PROVED.</summary>
    private async Task<Guest?> GuestOnThisEventAsync(Guid campaignId, CancellationToken ct)
    {
        var proved = await MyContactsAsync(ct);
        if (proved.Count == 0) return null;
        var guests = await guestRepository.ListByCampaignAsync(campaignId, includeOptedOut: false, ct);
        return guests.FirstOrDefault(g => Matches(g, proved));
    }

    public async Task<MediaBucketDto> RenameAsync(
        Guid bucketId, RenameMediaBucketRequest req, CancellationToken ct = default)
    {
        var bucket = await OwnedAsync(bucketId, ct, tracking: true);

        if ((await plans.ForCampaignAsync(bucket.CampaignId, ct)).MaxBuckets <= 1)
            throw new BusinessRuleException(
                "Naming albums comes with a Party or Wedding pass.", "rename_needs_subscription");

        // Blank is the default rather than an error: somebody clearing the box means "put it back",
        // and an empty name would render as a gap where the bucket's title should be.
        var name = req.Name?.Trim();
        bucket.Name = string.IsNullOrWhiteSpace(name)
            ? MediaBucket.DefaultName
            : name.Length > 80 ? name[..80] : name;
        bucket.UpdatedAt = DateTimeOffset.UtcNow;

        buckets.Update(bucket);
        await uow.SaveChangesAsync(ct);
        return (await DescribeAsync([bucket], ct))[0];
    }

    public async Task<MediaBucketDto> SetWindowAsync(
        Guid bucketId, SetBucketWindowRequest req, CancellationToken ct = default)
    {
        var bucket = await OwnedAsync(bucketId, ct, tracking: true);
        var plan = await plans.ForCampaignAsync(bucket.CampaignId, ct);
        var most = Math.Min(EventDayWindow.MaxWindowDays, Math.Max(plan.MaxWindowDays, bucket.UploadWindowDays));

        if (req.Days < 1)
            throw new BusinessRuleException("An album collects for at least the one night.", "window_invalid");
        if (req.Days > most)
            throw new BusinessRuleException(
                most <= 1
                    ? "Collecting for more than one night comes with a Party or Wedding pass."
                    : $"This event can collect for up to {most} days.",
                "window_needs_plan");

        bucket.UploadWindowDays = req.Days;
        bucket.UpdatedAt = DateTimeOffset.UtcNow;
        buckets.Update(bucket);
        await uow.SaveChangesAsync(ct);
        return (await DescribeAsync([bucket], ct))[0];
    }

    public async Task RaiseWindowsToPlanAsync(Guid campaignId, CancellationToken ct = default)
    {
        var most = (await plans.ForCampaignAsync(campaignId, ct)).MaxWindowDays;
        var rows = await buckets.Query(tracking: true)
            .Where(b => b.CampaignId == campaignId && b.UploadWindowDays < most)
            .ToListAsync(ct);
        if (rows.Count == 0) return;
        var now = DateTimeOffset.UtcNow;
        foreach (var b in rows)
        {
            b.UploadWindowDays = most;
            b.UpdatedAt = now;
            buckets.Update(b);
        }
        await uow.SaveChangesAsync(ct);
    }

    public Task<long> EventUsedBytesAsync(Guid campaignId, CancellationToken ct = default) =>
        buckets.Query().Where(b => b.CampaignId == campaignId).SumAsync(b => b.UsedBytes, ct);

    public async Task<int> WindowForCampaignAsync(Guid campaignId, CancellationToken ct = default)
    {
        var days = await buckets.Query()
            .Where(b => b.CampaignId == campaignId)
            .OrderBy(b => b.CreatedAt).ThenBy(b => b.Id)
            .Select(b => (int?)b.UploadWindowDays)
            .FirstOrDefaultAsync(ct);
        // No album yet: the one made on the first upload takes the plan's days, so answer with those.
        return days ?? (await PlanOrNullAsync(campaignId, ct))?.MaxWindowDays ?? 1;
    }

    public async Task<MediaBucket> ForCampaignAsync(Guid campaignId, CancellationToken ct = default)
    {
        var existing = await buckets.Query()
            .Where(b => b.CampaignId == campaignId)
            .OrderBy(b => b.CreatedAt).ThenBy(b => b.Id)
            .FirstOrDefaultAsync(ct);
        if (existing is not null) return existing;

        var campaign = await campaigns.GetByIdAsync(campaignId, ct)
                       ?? throw new NotFoundException("That event no longer exists.");
        if (campaign.Status == CampaignStatus.Cancelled)
            throw new BusinessRuleException("This event was cancelled, so it has no photo space.", "event_cancelled");

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
        // As many days as the event's plan gives: one on Free, more with a pass or at a venue.
        var bucket = NewBucket(
            campaign.CreatedByUserId ?? currentUser.UserId ?? Guid.Empty,
            campaignId, campaign.EventStartAt,
            WindowFor((await plans.ForCampaignAsync(campaignId, ct)).MaxWindowDays, EventDayWindow.MaxWindowDays));

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

    public async Task RemoveForCampaignAsync(Guid campaignId, CancellationToken ct = default)
    {
        var now = DateTimeOffset.UtcNow;
        var eventBuckets = await buckets.Query(tracking: true).Where(b => b.CampaignId == campaignId).ToListAsync(ct);
        var bucketIds = eventBuckets.Select(b => b.Id).ToList();

        // Photos are marked deleted, the same way removing one by hand does, so nothing links to them.
        var held = await photos.Query(tracking: true)
            .Where(p => p.DeletedAt == null
                        && (p.CampaignId == campaignId || (p.BucketId != null && bucketIds.Contains(p.BucketId.Value))))
            .ToListAsync(ct);
        foreach (var photo in held) photo.DeletedAt = now;

        qrs.RemoveRange(await qrs.Query(tracking: true).Where(q => bucketIds.Contains(q.BucketId)).ToListAsync(ct));
        // Members go with their bucket (cascade).
        buckets.RemoveRange(eventBuckets);

        if (await campaigns.GetByIdAsync(campaignId, ct) is { } campaign) campaign.MediaDeletedAt = now;
        await uow.SaveChangesAsync(ct);
    }

    public async Task EnsureRoomAsync(Guid bucketId, long incomingBytes, CancellationToken ct = default)
    {
        var bucket = await buckets.GetByIdAsync(bucketId, ct)
                     ?? throw new NotFoundException("That album no longer exists.");
        var plan = await plans.ForCampaignAsync(bucket.CampaignId, ct);

        // Space is per EVENT, shared by all of its albums. Summed fresh rather than read off the
        // entity: GetByIdAsync may hand back a copy this request loaded before the reservation lock
        // was granted, and that figure predates the uploads the lock was waiting on.
        var eventUsed = await buckets.Query()
            .Where(b => b.CampaignId == bucket.CampaignId)
            .SumAsync(b => b.UsedBytes, ct);
        if (eventUsed + incomingBytes > plan.EventBytes)
            throw new BusinessRuleException(
                plan.Kind is PlanKind.WeddingPass or PlanKind.Venue
                    ? $"This event's {Size(plan.EventBytes)} is full."
                    : plan.Kind == PlanKind.PartyPass
                        ? $"This event's {Size(plan.EventBytes)} is full. A Wedding pass gives it more room."
                        : $"This event's {Size(plan.EventBytes)} is full. A Party or Wedding pass gives it more room.",
                "bucket_full");

        // And a venue's limit across all of its events.
        if (plan.AccountBytes is { } cap && plan.VenueId is { } venue
            && await plans.VenueUsedBytesAsync(venue, ct) + incomingBytes > cap)
            throw new BusinessRuleException(
                $"This venue's {Size(cap)} across all its events is full. Remove some photos or talk to us about a bigger plan.",
                "account_full");
    }

    public async Task ReserveRoomAsync(Guid bucketId, long bytes, CancellationToken ct = default)
    {
        var bucket = await buckets.GetByIdAsync(bucketId, ct)
                     ?? throw new NotFoundException("That album no longer exists.");
        var plan = await plans.ForCampaignAsync(bucket.CampaignId, ct);

        // The lock is on whatever the check sums across. A venue's limit spans all of its events, so
        // every upload there queues on the venue; otherwise the space is the event's, shared by its albums.
        await usage.ReserveAsync(
            plan.VenueId ?? bucket.CampaignId, bucketId, bytes,
            c => EnsureRoomAsync(bucketId, bytes, c), ct);
    }

    /// <summary>
    /// The plan for an event, or null when the event has been deleted. Buckets can outlive their
    /// event, and one of those must not break every page that lists its owner's buckets.
    /// </summary>
    private async Task<EventPlan?> PlanOrNullAsync(Guid campaignId, CancellationToken ct)
    {
        try
        {
            return await plans.ForCampaignAsync(campaignId, ct);
        }
        catch (NotFoundException)
        {
            return null;
        }
    }

    /// <summary>What a bucket whose event is gone is described with: the free plan.</summary>
    private static readonly EventPlan OrphanPlan = PlanRules.Evaluate(
        DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, EventPassKind.None, null, null, null, 0, null, null, null);

    public async Task<StorageSummaryDto> StorageSummaryAsync(CancellationToken ct = default)
    {
        var me = RequireUser();
        var venue = await MyVenueAsync(me, ct);
        if (venue is null) return new StorageSummaryDto("None", null, 0, null);

        var owner = await users.GetByIdAsync(venue.OwnerUserId, ct);
        var active = owner is { SubscriptionTier: SubscriptionTier.Venue }
                     && PlanRules.IsActive(owner.SubscriptionTier, owner.SubscriptionEndsAt, DateTimeOffset.UtcNow);
        return new StorageSummaryDto(
            active ? SubscriptionTier.Venue.ToString() : "None",
            active ? PlanCatalog.VenueAccountBytes : null,
            await plans.VenueUsedBytesAsync(venue.Id, ct),
            venue.Name);
    }

    /// <summary>The venue this account owns, or works at.</summary>
    private async Task<Venue?> MyVenueAsync(Guid me, CancellationToken ct)
    {
        var owned = await venues.Query().FirstOrDefaultAsync(v => v.OwnerUserId == me, ct);
        if (owned is not null) return owned;
        var email = (await users.GetByIdAsync(me, ct))?.Email?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(email)) return null;
        var staffAt = await venueStaff.Query().Where(s => s.Email == email).Select(s => s.VenueId).FirstOrDefaultAsync(ct);
        return staffAt == Guid.Empty ? null : await venues.GetByIdAsync(staffAt, ct);
    }

    private static string Size(long bytes) =>
        bytes >= PlanCatalog.Gb
            ? $"{bytes / (double)PlanCatalog.Gb:0.#} GB"
            : $"{bytes / PlanCatalog.Mb} MB";

    public async Task<MediaPhase> PhaseForCampaignAsync(Guid campaignId, CancellationToken ct = default) =>
        (await plans.ForCampaignAsync(campaignId, ct)).Phase;

    public async Task EnsureOpenAsync(Guid bucketId, CancellationToken ct = default)
    {
        var bucket = await buckets.GetByIdAsync(bucketId, ct)
                     ?? throw new NotFoundException("That album no longer exists.");

        if ((await plans.ForCampaignAsync(bucket.CampaignId, ct)).Phase != MediaPhase.Active)
            throw new BusinessRuleException(
                "This event's plan has ended, so nothing new can be added. Everything already here is kept for now.",
                "plan_ended");

        // Through the EVENT, the same way the DTO reads it — see Night(). The copy on the bucket is
        // only reached when there is somehow no event behind it.
        var night = (await campaigns.GetByIdAsync(bucket.CampaignId, ct))?.EventStartAt
                    ?? bucket.EventDate;

        if (EventDayWindow.IsOpen(night, DateTimeOffset.UtcNow, bucket.UploadWindowDays)) return;

        // Which side of it they are on, because "closed" means two completely different things to
        // somebody standing at the party a day early and somebody looking a week later.
        throw new BusinessRuleException(
            DateTimeOffset.UtcNow < night
                ? "This one isn't open yet — it opens the day before the event."
                : "This one has closed. Everything already added is still here.",
            "bucket_closed");
    }

    // One UPDATE in the database. This used to load the bucket, add, and save the total back, so
    // parallel uploads overwrote each other's increments and the bucket under-reported what it held.
    public Task CountUsageAsync(Guid bucketId, long bytes, CancellationToken ct = default) =>
        usage.AddAsync(bucketId, bytes, ct);

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
        var campaign = await campaigns.GetByIdAsync(bucket.CampaignId, ct);
        var night = campaign?.EventStartAt ?? bucket.EventDate;
        var open = EventDayWindow.IsOpen(night, DateTimeOffset.UtcNow, bucket.UploadWindowDays);

        // Space and cover come from the event's plan, exactly as the upload itself checks them. The
        // bucket's own CapacityBytes is a leftover from per-bucket sizes and is 0 on every bucket made
        // since, so reading it here turned every table code into "full".
        var room = false;
        var plan = campaign is null ? null : await plans.ForCampaignAsync(campaign.Id, ct);
        if (plan?.Phase == MediaPhase.Active)
        {
            try
            {
                await EnsureRoomAsync(bucket.Id, 1, ct);
                room = true;
            }
            catch (BusinessRuleException)
            {
                // Full: still admitted, told so on the page.
            }
        }

        // The name a scanner is shown is the EVENT's — the bucket has none of its own. This is what
        // somebody standing at a party reads to know they are adding to the right night.
        var title = campaign?.Title ?? "Album";

        var venue = plan?.VenueId is { } venueId ? await venues.GetByIdAsync(venueId, ct) : null;
        var (opensAt, closesAt) = EventDayWindow.Bounds(night, bucket.UploadWindowDays);
        return new MediaBucketQrAdmission(
            code.Id, bucket.Id, title, code.AllowAnonymous, room && open, open, night,
            plan?.Branded ?? true, venue?.Name, venue?.LogoUrl, opensAt, closesAt);
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

    /// <summary>
    /// How many days a bucket being created may collect for.
    ///
    /// <para>One unless the caller holds the permission, and never past the ceiling whatever they
    /// ask for: what this number really governs is how long a QR code printed onto a table card goes
    /// on working, so it is clamped here as well as in <c>EventDayWindow</c>.</para>
    /// </summary>
    private static int WindowFor(int? asked, int maxDays)
    {
        if (asked is not { } days || days <= 1) return 1;
        return Math.Min(days, Math.Min(maxDays, EventDayWindow.MaxWindowDays));
    }

    private MediaBucket NewBucket(
        Guid ownerId, Guid campaignId, DateTimeOffset eventDate, int windowDays = 1)
    {
        var now = DateTimeOffset.UtcNow;
        return new MediaBucket
        {
            Id = Guid.NewGuid(),
            OwnerUserId = ownerId,
            CampaignId = campaignId,
            EventDate = eventDate.ToUniversalTime(),
            // Space comes from the event's plan now, not from the bucket. See PlanService.
            Tier = MediaBucketTier.Free,
            CapacityBytes = 0,
            UploadWindowDays = windowDays,
            UsedBytes = 0,
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

        if (bucket is null) throw new NotFoundException("That album no longer exists.");

        if (currentUser.UserId is { } me && bucket.OwnerUserId == me) return bucket;
        if (await ownership.OwnsAsync(bucket.CampaignId, ct)) return bucket;

        throw new ForbiddenException("That album isn't yours.");
    }

    private Guid RequireUser() =>
        currentUser.UserId ?? throw new ForbiddenException("Sign in to manage albums.");

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
        var campaignIds = rows.Select(b => b.CampaignId).Distinct().ToList();
        var events = campaignIds.Count == 0
            ? []
            : await campaigns.Query()
                .Where(c => campaignIds.Contains(c.Id))
                .Select(c => new EventFace(c.Id, c.Title, c.CustomContentJson, c.EventStartAt, c.VenueId))
                .ToDictionaryAsync(x => x.Id, x => x, ct);

        // Which bucket each event posts to by default — the oldest, matching ForCampaignAsync. Read
        // per event rather than per row, and across ALL of an event's buckets rather than only the
        // ones on this page: a page showing the second bucket alone must not call it the default.
        var defaults = (await buckets.Query()
                .Where(b => campaignIds.Contains(b.CampaignId))
                .Select(b => new { b.Id, b.CampaignId, b.CreatedAt })
                .ToListAsync(ct))
            .GroupBy(b => b.CampaignId)
            .Select(g => g.OrderBy(b => b.CreatedAt).ThenBy(b => b.Id).First().Id)
            .ToHashSet();

        // The plan is the EVENT's, so every bucket on an event shows the same space and the same end.
        var eventPlans = new Dictionary<Guid, EventPlan>();
        foreach (var id in campaignIds)
            eventPlans[id] = await PlanOrNullAsync(id, ct) ?? OrphanPlan;
        var usedByEvent = (await buckets.Query()
                .Where(b => campaignIds.Contains(b.CampaignId))
                .Select(b => new { b.CampaignId, b.UsedBytes })
                .ToListAsync(ct))
            .GroupBy(x => x.CampaignId)
            .ToDictionary(g => g.Key, g => g.Sum(x => x.UsedBytes));

        // The venue each event is held at, for the name and logo its albums carry.
        var venueIds = events.Values.Select(e => e.VenueId).OfType<Guid>().Distinct().ToList();
        var venueFaces = venueIds.Count == 0
            ? new Dictionary<Guid, Venue>()
            : await venues.Query().Where(v => venueIds.Contains(v.Id)).ToDictionaryAsync(v => v.Id, ct);

        var now = DateTimeOffset.UtcNow;
        return rows.Select(b =>
        {
            var plan = eventPlans[b.CampaignId];
            // The space is the event's, shared by all of its albums.
            var capacity = plan.EventBytes;
            var eventUsed = usedByEvent.GetValueOrDefault(b.CampaignId);
            var venue = events.TryGetValue(b.CampaignId, out var face) && face.VenueId is { } v
                ? venueFaces.GetValueOrDefault(v)
                : null;
            return new MediaBucketDto(
                b.Id,
                b.Name,
                Title(b, events),
                Cover(b, events),
                plan.Kind.ToString(),
                Math.Round(capacity / (double)MediaBucketPlans.BytesPerGb, 1),
                capacity,
                b.UsedBytes,
                capacity <= 0
                    ? (eventUsed > 0 ? 100 : 0)
                    : (int)Math.Clamp(Math.Round(eventUsed * 100.0 / capacity), 0, 100),
                counts.GetValueOrDefault(b.Id),
                b.CampaignId,
                Title(b, events),
                Night(b, events),
                EventDayWindow.IsOpen(Night(b, events), now, b.UploadWindowDays),
                b.UploadWindowDays,
                defaults.Contains(b.Id),
                plan.CoveredUntil,
                plan.Phase != MediaPhase.Active,
                b.CreatedAt,
                plan.MaxBuckets,
                plan.MaxWindowDays,
                plan.Phase.ToString(),
                eventUsed,
                plan.PrivateAlbums,
                plan.Branded,
                venue?.Name,
                venue?.LogoUrl);
        }).ToList();
    }

    private string ContributeUrl(string token) => $"{ContributeBase}/q/{token}";

    /// <summary>How an event presents itself: the things a bucket used to duplicate, and where it is held.</summary>
    private sealed record EventFace(
        Guid Id, string Title, string? CustomContentJson, DateTimeOffset EventStartAt, Guid? VenueId);

    /// <summary>
    /// The night, read from the EVENT rather than from the copy taken when the bucket was made.
    ///
    /// <para>The copy was written once and never updated, so a host who moved their date left the
    /// bucket open on the old night and shut on the new one — the invitation and the bucket
    /// disagreeing about which evening they belong to, which is the thing the copy existed to
    /// prevent. Reading through means there is one answer and it cannot drift.</para>
    /// </summary>
    private static DateTimeOffset Night(
        MediaBucket bucket, IReadOnlyDictionary<Guid, EventFace> events) =>
        events.TryGetValue(bucket.CampaignId, out var face) ? face.EventStartAt : bucket.EventDate;

    /// <summary>
    /// The event's name. A bucket has none of its own; a campaign that could not be loaded falls
    /// back to something sayable rather than to an empty string in the middle of somebody's list.
    /// </summary>
    private static string Title(MediaBucket bucket, IReadOnlyDictionary<Guid, EventFace> events) =>
        events.TryGetValue(bucket.CampaignId, out var face) ? face.Title : "Album";

    /// <summary>The event's cover, read from where the host's own choice is kept.</summary>
    private static string? Cover(MediaBucket bucket, IReadOnlyDictionary<Guid, EventFace> events) =>
        events.TryGetValue(bucket.CampaignId, out var face)
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
}
