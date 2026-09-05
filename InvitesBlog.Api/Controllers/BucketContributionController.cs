using InvitesBlog.Api.Authorization;
using InvitesBlog.Api.MediaBuckets;
using InvitesBlog.Application.Common;
using InvitesBlog.Application.Dtos.Otp;
using InvitesBlog.Application.Services.MediaBuckets;
using InvitesBlog.Application.Services.Otp;
using InvitesBlog.Application.Services.Photos;
using InvitesBlog.Domain.Authorization;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace InvitesBlog.Api.Controllers;

/// <summary>
/// What somebody who scanned a bucket's QR code can do — which is exactly one thing: add to it.
///
/// <para><b>Every route here is anonymous, and that is the design rather than a gap.</b> The caller
/// is a guest at a party who pointed a camera at a card on a table. They have no account and are
/// never going to make one. What authorizes them is the printed token, and it authorizes adding to
/// one bucket and nothing else — they cannot read the bucket back, cannot see who else contributed,
/// and cannot remove anything, including their own. A code that could do more than write would be a
/// code that anyone who photographed the table card could do more with.</para>
///
/// <para><b>Two doors, chosen by whoever printed the card.</b> An <b>anonymous</b> code asks for a
/// name, believes it, and credits the photographs to it — right for a room where everyone present was
/// invited by the person holding the party. A <b>verified</b> code sends a one-time code to an email
/// or phone, and admits only contacts the owner has put on the bucket's list; the credit is then the
/// owner's name for that person, not one the contributor typed. The difference is recorded per code
/// rather than per bucket — the cards on the tables and the link in the follow-up email are the same
/// bucket and want opposite answers.</para>
///
/// <para><b>Neither door reads.</b> A bucket's photographs are visible to its owner and to the
/// event's guest list, and to nobody else — contributing is not a way in. See
/// <c>IMediaBucketService.MayViewAsync</c>.</para>
/// </summary>
[ApiController]
[Route("api/q")]
[AllowAnonymous]
public sealed class BucketContributionController(
    IMediaBucketService buckets,
    IEventPhotoService photos,
    IOtpService otp,
    ContributorTickets tickets) : BaseApiController
{
    /// <summary>
    /// What a scanned code opens: whose bucket, and whether a name alone is enough to add to it.
    ///
    /// <para>A bad token gets a flat 404 — the same answer as a revoked one and a deleted bucket.
    /// Whether a code is real is precisely what somebody guessing would like to be told.</para>
    /// </summary>
    [HttpGet("{token}")]
    public async Task<IActionResult> Scan(string token, CancellationToken ct)
    {
        var admission = await buckets.AdmitAsync(token, ct);
        if (admission is null) return NotFound(ApiResponse<object?>.Fail("That code isn't valid."));

        return Success(new
        {
            bucketTitle = admission.BucketTitle,
            allowAnonymous = admission.AllowAnonymous,
            // False once the night is over or the bucket is full. The page hides its picker on this
            // rather than letting somebody choose twenty photographs and then be refused.
            canUpload = admission.CanUpload,
            isOpen = admission.IsOpen,
            eventDate = admission.EventDate,
        });
    }

    /// <summary>
    /// Starts the verified path: sends a one-time code to an email or phone.
    ///
    /// <para>Rate-limited on the shared OTP limiter, because this is a send anybody on the internet
    /// can ask for once they hold a token — the printed card is on a table in public.</para>
    /// </summary>
    [HttpPost("{token}/otp/request")]
    [HasPermission(Permissions.Otp.Request)]
    [EnableRateLimiting("otp")]
    public async Task<IActionResult> RequestCode(
        string token, [FromBody] SendOtpRequest req, CancellationToken ct)
    {
        var admission = await buckets.AdmitAsync(token, ct);
        if (admission is null) return NotFound(ApiResponse<object?>.Fail("That code isn't valid."));

        // Refused rather than quietly allowed: an anonymous code has nothing to verify, and sending a
        // code for one would be a send anyone could trigger against any address, for nothing.
        if (admission.AllowAnonymous)
            return BadRequest(ApiResponse<object?>.Fail("This code doesn't need a verification code."));

        // Gated on the owner's list BEFORE the send, the same way the campaign link is gated on the
        // guest list: a contact that could never get in should not be mailed a code that would not
        // work, and the send is something anybody holding a printed card could otherwise trigger
        // against any address in the world.
        var contact = req.Email ?? req.Phone;
        if (string.IsNullOrWhiteSpace(contact))
            return BadRequest(ApiResponse<object?>.Fail("Enter an email or a phone number."));

        if (await buckets.GuestForContactAsync(admission.BucketId, contact, ct) is null)
            return BadRequest(ApiResponse<object?>.Fail(
                "That contact isn't on the guest list. Ask whoever's hosting to add you."));

        return Success(await otp.RequestAsync(req, ct: ct));
    }

    /// <summary>
    /// Finishes the verified path and hands back the admission the uploads carry.
    ///
    /// <para>One verification covers the evening rather than every file. A phone's picker returns
    /// twenty photographs at once and nobody is typing twenty codes — see
    /// <see cref="ContributorTickets"/> for what the ticket does and does not carry.</para>
    /// </summary>
    [HttpPost("{token}/otp/verify")]
    [HasPermission(Permissions.Otp.Verify)]
    public async Task<IActionResult> VerifyCode(
        string token, [FromBody] VerifyContributorRequest req, CancellationToken ct)
    {
        var admission = await buckets.AdmitAsync(token, ct);
        if (admission is null) return NotFound(ApiResponse<object?>.Fail("That code isn't valid."));

        var verified = await otp.VerifyContactAsync(new VerifyOtpRequest(req.ChallengeId, req.Code), ct);

        // Proving a contact is not the same as being allowed in. The code is checked again here and
        // not only before the send, because the challenge is issued against a contact the caller
        // typed and this is the first moment we know which contact they actually hold.
        var guest = await buckets.GuestForContactAsync(admission.BucketId, verified.Contact, ct);
        if (guest is null)
            return Unauthorized(ApiResponse<object?>.Fail("That contact isn't on the guest list."));

        // THE HOST'S NAME FOR THEM, off the guest list, not one they typed. On a code that exists
        // precisely to demand proof, a self-declared display name would leave the one thing shown on
        // every photograph as the only unproved value in the flow.
        var name = string.IsNullOrWhiteSpace(guest.Name) ? verified.Contact : guest.Name;

        return Success(new
        {
            ticket = tickets.Issue(admission.QrId, name, verified.Contact, DateTimeOffset.UtcNow),
            displayName = name,
        });
    }

    /// <summary>
    /// The anonymous path: a name, and nothing to prove.
    ///
    /// <para>A name is still required. It is not an identity and is never treated as one, but a grid
    /// where every tile says "Guest" is one the host cannot moderate and the people in it cannot find
    /// themselves in.</para>
    /// </summary>
    [HttpPost("{token}/join")]
    public async Task<IActionResult> Join(
        string token, [FromBody] JoinBucketRequest req, CancellationToken ct)
    {
        var admission = await buckets.AdmitAsync(token, ct);
        if (admission is null) return NotFound(ApiResponse<object?>.Fail("That code isn't valid."));

        if (!admission.AllowAnonymous)
            return BadRequest(ApiResponse<object?>.Fail("This code needs you to verify a contact first."));

        var name = (req.DisplayName ?? string.Empty).Trim();
        if (name.Length == 0)
            return BadRequest(ApiResponse<object?>.Fail("Tell us what to call you."));

        return Success(new
        {
            ticket = tickets.Issue(admission.QrId, Truncate(name), verifiedContact: null, DateTimeOffset.UtcNow),
            displayName = Truncate(name),
        });
    }

    /// <summary>
    /// Adds one item. Same media rules as every other door into a box — see
    /// <c>EventPhotoService.StoreAsync</c> — including the still a clip has to arrive with.
    ///
    /// <para>The token is re-checked here and not merely at the start. A ticket is minted for twelve
    /// hours, and inside them a code can be revoked and a member can be taken off the list; the point
    /// of being able to turn either off is that it stops working for the people already holding
    /// one.</para>
    /// </summary>
    [HttpPost("{token}/media")]
    [DisableRequestSizeLimit]
    [RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
    public async Task<IActionResult> Add(
        string token,
        [FromForm] IFormFile? file,
        [FromForm] IFormFile? poster,
        [FromForm(Name = ContributorTickets.FieldName)] string? ticket,
        CancellationToken ct)
    {
        var admission = await buckets.AdmitAsync(token, ct);
        if (admission is null) return NotFound(ApiResponse<object?>.Fail("That code isn't valid."));

        var holder = tickets.Read(ticket, DateTimeOffset.UtcNow);
        if (holder is null)
            return Unauthorized(ApiResponse<object?>.Fail("Tell us who you are before adding."));

        // The ticket has to have been minted for THIS code. Otherwise a ticket earned on a bucket
        // somebody does own would let them write into any other bucket whose token they could read.
        if (holder.QrId != admission.QrId)
            return Unauthorized(ApiResponse<object?>.Fail("Tell us who you are before adding."));

        // A ticket earned by verifying is only good for as long as that contact is still on the
        // guest list. Without this, being taken off took effect only once the ticket expired — and
        // removing somebody means nothing if it does not take effect until tomorrow.
        if (holder.VerifiedContact is { } proved
            && await buckets.GuestForContactAsync(admission.BucketId, proved, ct) is null)
            return Unauthorized(ApiResponse<object?>.Fail("That contact isn't on the guest list."));

        if (!admission.CanUpload)
            return BadRequest(ApiResponse<object?>.Fail(
                admission.IsOpen ? "This bucket is full." : "This one isn't open."));

        if (file is null || file.Length == 0)
            return BadRequest(ApiResponse<object?>.Fail("Pick a photo or a video."));

        var bucket = await buckets.GetBucketForContributionAsync(admission.BucketId, ct);
        if (bucket is null) return NotFound(ApiResponse<object?>.Fail("That code isn't valid."));

        var added = await photos.AddToBucketAsync(
            admission.BucketId,
            bucket.CampaignId,
            holder.DisplayName,
            await ReadAsync(file, ct),
            file.ContentType,
            file.FileName,
            poster is { Length: > 0 } ? await ReadAsync(poster, ct) : null,
            ct);

        await buckets.CountContributionAsync(admission.QrId, ct);

        // Deliberately thin. A contributor is told their photo landed and nothing else about the
        // bucket — not what is in it, not who else has added to it.
        return Created(new { id = added.Id, thumbUrl = added.ThumbUrl });
    }

    private static async Task<byte[]> ReadAsync(IFormFile file, CancellationToken ct)
    {
        await using var stream = file.OpenReadStream();
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    private static string Truncate(string name) => name.Length <= 60 ? name : name[..60];
}

/// <summary>
/// Finishing the verified path: the challenge and its code.
///
/// <para><c>DisplayName</c> is accepted and deliberately IGNORED on this door — the name comes from
/// the owner's list. It stays on the record only so an older client posting it is not rejected.</para>
/// </summary>
public sealed record VerifyContributorRequest(Guid ChallengeId, string Code, string? DisplayName);

/// <summary>Joining an anonymous code: just what to be called.</summary>
public sealed record JoinBucketRequest(string? DisplayName);
