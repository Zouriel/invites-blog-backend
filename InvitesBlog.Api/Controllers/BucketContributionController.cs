using System.Security.Cryptography;
using InvitesBlog.Api.Authorization;
using InvitesBlog.Api.Rendering;
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

        return Admitted(
            tickets.Issue(admission.QrId, name, verified.Contact, DateTimeOffset.UtcNow), name);
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

        return Admitted(
            tickets.Issue(admission.QrId, Truncate(name), verifiedContact: null, DateTimeOffset.UtcNow),
            Truncate(name));
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

        // The form field is how the app's own picker sends it; the cookie is how the camera page does,
        // because a server-rendered page was handed nothing to put in a field. Same ticket, same
        // checks below either way.
        var holder = tickets.Read(ticket, DateTimeOffset.UtcNow)
                     ?? tickets.Read(Request.Cookies[ContributorTickets.CookieName], DateTimeOffset.UtcNow);
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

    /// <summary>
    /// Whether this browser is still admitted to this code, and under what name.
    ///
    /// <para>What makes coming back from the camera free. The page holds its ticket in memory and
    /// nowhere else, so a full-page navigation loses it — and asking somebody at a party to type
    /// their name again because they pressed Back is the kind of small insult that stops them
    /// bothering.</para>
    /// </summary>
    [HttpGet("{token}/session")]
    public async Task<IActionResult> Session(string token, CancellationToken ct)
    {
        var admission = await buckets.AdmitAsync(token, ct);
        if (admission is null) return NotFound(ApiResponse<object?>.Fail("That code isn't valid."));

        var holder = tickets.Read(Request.Cookies[ContributorTickets.CookieName], DateTimeOffset.UtcNow);
        if (holder is null || holder.QrId != admission.QrId)
            return Success(new { admitted = false, ticket = (string?)null, displayName = (string?)null });

        // A ticket earned by verifying is only good while that contact is still on the guest list —
        // the same rule the upload applies, applied before offering to skip the door.
        if (holder.VerifiedContact is { } proved
            && await buckets.GuestForContactAsync(admission.BucketId, proved, ct) is null)
            return Success(new { admitted = false, ticket = (string?)null, displayName = (string?)null });

        return Success(new
        {
            admitted = true,
            ticket = Request.Cookies[ContributorTickets.CookieName],
            displayName = holder.DisplayName,
        });
    }

    /// <summary>
    /// The viewfinder, for somebody who scanned a printed code.
    ///
    /// <para>The same page a guest gets — one camera in this product, not two — and for the same
    /// reason: a contributor standing at a party has not "captured" anything yet, and a file picker
    /// asks them to go and find something instead of pointing a phone at the room.</para>
    ///
    /// <para><b>Why it hangs off /api.</b> This is HTML rather than JSON, which does not belong under
    /// an API prefix. It is here anyway because the reverse proxy sends <c>/api/*</c> to this service
    /// and everything else on this host to the single-page app — and <c>/q/{token}</c> itself IS a
    /// page of that app. Serving this from a new top-level path would mean a route change on a proxy
    /// shared with other projects, to gain a tidier URL nobody ever types.</para>
    /// </summary>
    [HttpGet("{token}/camera")]
    public async Task<IActionResult> Camera(string token, CancellationToken ct)
    {
        var admission = await buckets.AdmitAsync(token, ct);
        if (admission is null) return NotFound(ApiResponse<object?>.Fail("That code isn't valid."));

        // Admitted, and admitted to THIS code — a ticket earned on one bucket must not open the
        // camera on another whose token somebody could read off a table.
        var holder = tickets.Read(Request.Cookies[ContributorTickets.CookieName], DateTimeOffset.UtcNow);
        if (holder is null || holder.QrId != admission.QrId)
            return Redirect($"/q/{Uri.EscapeDataString(token)}");

        if (!admission.CanUpload)
            return Redirect($"/q/{Uri.EscapeDataString(token)}");

        var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        var back = $"/q/{Uri.EscapeDataString(token)}";
        var ticket = Request.Cookies[ContributorTickets.CookieName]!;

        var html = GuestCameraPage.Render(
            $"/api/q/{Uri.EscapeDataString(token)}/media",
            back,
            admission.BucketTitle,
            // A bucket carries no theme of its own — it is the event that has a look, and a
            // contributor is not shown the event.
            GuestPalette.Fallback,
            nonce,
            backLabel: "Back",
            gateNote: "You can still add photos straight from this phone's library.",
            gateAction: "Add from your library",
            // The upload carries the ticket in the field it always has. The cookie got it this far,
            // but the writer's own door is the form field and there is no reason for the camera to
            // knock on a different one.
            fields: new Dictionary<string, string> { [ContributorTickets.FieldName] = ticket });

        Response.Headers.CacheControl = "private, no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        // The microphone rides along with the camera: holding the shutter records a clip, and a clip
        // of a party with no sound is half of one.
        Response.Headers["Permissions-Policy"] = "camera=(self), microphone=(self), geolocation=()";
        Response.Headers["Content-Security-Policy"] =
            $"default-src 'none'; script-src 'nonce-{nonce}'; style-src 'unsafe-inline'; "
            + "img-src 'self' blob: data:; media-src 'self' blob:; connect-src 'self'; "
            + "form-action 'self'; base-uri 'none'; frame-ancestors 'none'";
        return Content(html, "text/html; charset=utf-8");
    }

    /// <summary>
    /// Hands the ticket back twice: in the body for the app's picker, and in an HttpOnly cookie for
    /// the camera page, which holds nothing the app could have given it. See
    /// <see cref="ContributorTickets.CookieName"/>.
    /// </summary>
    private IActionResult Admitted(string ticket, string displayName)
    {
        Response.Cookies.Append(ContributorTickets.CookieName, ticket, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            // No Domain: host-only, so it never travels to a sibling host.
            // No Expires: it dies with the browser session, and the ticket's own twelve hours cap it.
        });

        return Success(new { ticket, displayName });
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
