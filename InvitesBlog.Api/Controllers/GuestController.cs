using InvitesBlog.Api.Rendering;
using InvitesBlog.Application.Dtos.Invites;
using InvitesBlog.Application.Dtos.Otp;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Services.Invites;
using InvitesBlog.Application.Services.Photos;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Infrastructure.Rendering;
using InvitesBlog.TemplateCompiler;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InvitesBlog.Api.Controllers;

/// <summary>
/// The server-rendered guest path — the whole thing a guest touches, as HTML rather than as an API.
///
/// <para><b>Why it exists.</b> A guest's invitation used to be an Angular page that created a
/// sandboxed iframe and posted the data into it. That cost us two classes of bug by construction:
/// binding ran on load AND on every host message, so anything that cloned had to be idempotent (a
/// gallery that wasn't turned six photos into thirty-six, then two hundred and sixteen); and `vh`
/// units lived inside a frame the phone's URL bar resizes mid-scroll, which threw readers backwards.
/// Bound once on the server, in one top-level document, neither can happen.</para>
///
/// <para><b>How access works.</b> Admission happens at <c>/i/{token}</c> — possession of the token
/// plus the existing IP-trust check, with an OTP challenge from an unrecognised network. On success
/// the guest gets an HttpOnly cookie and is redirected to <c>/r/{renderId}</c>, which carries no
/// credential at all. That split is not decoration: templates may ship their own JavaScript, and a
/// top-level document can read its own URL and navigate away with it. The cookie it cannot read.</para>
/// </summary>
[ApiController]
[AllowAnonymous]
public sealed class GuestController(
    IInviteService invites,
    IEventPhotoService photos,
    InviteRenderService renderer,
    RenderedInvitations rendered,
    RenderTickets tickets) : ControllerBase
{
    // ---------- admission ----------

    /// <summary>The personal link, as sent to the guest. Everything starts here.</summary>
    [HttpGet("/i/{token}")]
    public async Task<IActionResult> Open(string token, CancellationToken ct)
    {
        object result;
        try { result = await invites.GetByTokenAsync(token, ClientIp(), Render, ct); }
        catch (AppException) { return Html(GuestPages.NotFound(), StatusCodes.Status404NotFound); }

        return result switch
        {
            InviteCancelledResponse c => Html(GuestPages.Cancelled(c.Message)),
            InviteRequiresOtpResponse => Html(GuestPages.Reauth(token, null, null, codeSent: false)),
            InviteViewResponse view => Admit(view.InviteId),
            _ => Html(GuestPages.Unavailable(), StatusCodes.Status500InternalServerError)
        };
    }

    /// <summary>Sends the reauth code. No contact is taken from the caller — see the service.</summary>
    [HttpPost("/i/{token}/reauth")]
    public async Task<IActionResult> Reauth(string token, CancellationToken ct)
    {
        try
        {
            var challenge = await invites.RequestReauthAsync(token, ct);
            Response.Cookies.Append(ChallengeCookie, challenge.ChallengeId.ToString(), ChallengeCookieOptions());
            return Html(GuestPages.Reauth(token, challenge.Channel, null, codeSent: true));
        }
        catch (AppException e)
        {
            return Html(GuestPages.Reauth(token, null, e.Message, codeSent: false));
        }
    }

    /// <summary>Verifies the code, trusts this network for the invite, and admits the guest.</summary>
    [HttpPost("/i/{token}/verify")]
    public async Task<IActionResult> Verify(string token, [FromForm] string? code, CancellationToken ct)
    {
        if (!Guid.TryParse(Request.Cookies[ChallengeCookie], out var challengeId))
            return Html(GuestPages.Reauth(token, null, "That code has expired. Send a new one.", codeSent: false));

        try
        {
            await invites.VerifyReauthAsync(
                token, ClientIp(), new VerifyOtpRequest(challengeId, code ?? string.Empty), Render, ct);
        }
        catch (AppException e)
        {
            return Html(GuestPages.Reauth(token, "email", e.Message, codeSent: true));
        }

        Response.Cookies.Delete(ChallengeCookie);

        // Re-read through the normal path now that the IP is trusted, so admission has exactly one
        // implementation and the invite id comes from the same place either way.
        var opened = await invites.GetByTokenAsync(token, ClientIp(), Render, ct);
        return opened is InviteViewResponse view
            ? Admit(view.InviteId)
            : Html(GuestPages.Unavailable(), StatusCodes.Status500InternalServerError);
    }

    // ---------- the invitation itself ----------

    /// <summary>
    /// The invitation: one pre-filled top-level document, no iframe. Served with the CSP that gives
    /// it an opaque origin, so a template's own script can reach neither the cookie nor the API.
    /// </summary>
    [HttpGet("/r/{renderId}")]
    public async Task<IActionResult> Invitation(string renderId, CancellationToken ct)
    {
        // The cookie says which invitation; the URL must agree, or one admitted guest could read
        // another's invitation just by editing the address bar.
        var inviteId = Admitted(renderId);
        if (inviteId is null) return Html(GuestPages.Expired(), StatusCodes.Status401Unauthorized);

        // The invitation's own links point back at the path the guest arrived by, so its RSVP button
        // stays inside the server-rendered flow instead of bouncing to a page that wants a session.
        var payload = await invites.RenderAuthorizedAsync(inviteId.Value, PublicUrl($"/r/{renderId}"), Render, ct);
        if (payload is null) return Html(GuestPages.NotFound(), StatusCodes.Status404NotFound);

        var html = await rendered.BuildAsync(payload.PackageUrl, payload.Data, ct);
        if (html is null) return Html(GuestPages.Unavailable(), StatusCodes.Status500InternalServerError);

        Response.Headers["Content-Security-Policy"] = TemplateRuntime.ContentSecurityPolicy;
        Response.Headers["Referrer-Policy"] = TemplateRuntime.ReferrerPolicy;
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        // Personalised, and short-lived by nature: never let a shared cache hold one guest's copy.
        Response.Headers.CacheControl = "private, no-store";
        return Content(html, "text/html; charset=utf-8");
    }

    /// <summary>
    /// The colours the guest's own invitation is painted in. These pages sit either side of it, so
    /// wearing one fixed palette made every campaign that wasn't dark-and-gold feel like a hand-off to
    /// somewhere else. Falls back to the original palette when the template declared nothing.
    /// </summary>
    private async Task<GuestPalette> PaletteAsync(Guid inviteId, CancellationToken ct)
    {
        var theme = await invites.GuestThemeAsync(inviteId, ct);
        return theme is null
            ? GuestPalette.Fallback
            : GuestPalette.From(theme.Accent, theme.Background, theme.Text);
    }

    // ---------- rsvp ----------

    /// <summary>RSVP from inside a rendered invitation. The cookie is the authorization.</summary>
    [HttpGet("/r/{renderId}/rsvp")]
    public async Task<IActionResult> RsvpFormByRender(string renderId, CancellationToken ct)
    {
        var inviteId = Admitted(renderId);
        if (inviteId is null) return Html(GuestPages.Expired(), StatusCodes.Status401Unauthorized);

        var payload = await invites.RenderAuthorizedAsync(inviteId.Value, PublicUrl($"/r/{renderId}"), Render, ct);
        if (payload is null) return Html(GuestPages.NotFound(), StatusCodes.Status404NotFound);

        return Html(GuestPages.Rsvp($"/r/{renderId}/rsvp",
            payload.Data["guest"]?["name"]?.ToString() ?? "Friend",
            payload.Data["event"]?["title"]?.ToString() ?? "You're invited", null,
            await PaletteAsync(inviteId.Value, ct)));
    }

    [HttpPost("/r/{renderId}/rsvp")]
    public async Task<IActionResult> RsvpSubmitByRender(
        string renderId, [FromForm] string status, [FromForm] int? guestCount, [FromForm] string? comment,
        CancellationToken ct)
    {
        var inviteId = Admitted(renderId);
        if (inviteId is null) return Html(GuestPages.Expired(), StatusCodes.Status401Unauthorized);

        try
        {
            var result = await invites.RsvpAuthorizedAsync(inviteId.Value,
                new RsvpRequest(status, guestCount, null, comment, null, null, null), ct);
            return Html(GuestPages.RsvpDone(result.Rsvp, renderId, await PaletteAsync(inviteId.Value, ct)));
        }
        catch (AppException e)
        {
            return Html(GuestPages.Rsvp($"/r/{renderId}/rsvp", "Friend", "You're invited", e.Message,
                await PaletteAsync(inviteId.Value, ct)));
        }
    }

    // ---------- the event photo box ----------

    /// <summary>
    /// What everyone shot at the event (§5). Reached from the invitation's own
    /// <c>[data-href="photos.link"]</c>, for the same reason RSVP is: the rendered invitation is
    /// sandboxed under <c>default-src 'none'</c> and cannot upload — or fetch — anything itself. A
    /// link out to an ordinary page is the only door, and it is enough.
    /// </summary>
    [HttpGet("/r/{renderId}/photos")]
    public async Task<IActionResult> PhotoBox(string renderId, CancellationToken ct)
    {
        var inviteId = Admitted(renderId);
        if (inviteId is null) return Html(GuestPages.Expired(), StatusCodes.Status401Unauthorized);
        return await RenderBoxAsync(renderId, inviteId.Value, null, ct);
    }

    [HttpPost("/r/{renderId}/photos")]
    // No ceiling: a phone picker sends several photos at once and each one is somebody's own
    // photograph. Both framework defaults have to go — Kestrel's 30 MB body limit and the 24 MB
    // multipart cap configured globally for template images, which stays in force everywhere else.
    [DisableRequestSizeLimit]
    [Microsoft.AspNetCore.Mvc.RequestFormLimits(MultipartBodyLengthLimit = long.MaxValue)]
    public async Task<IActionResult> AddPhotos(
        string renderId, [FromForm] IFormFileCollection? files, CancellationToken ct)
    {
        var inviteId = Admitted(renderId);
        if (inviteId is null) return Html(GuestPages.Expired(), StatusCodes.Status401Unauthorized);

        var subject = await invites.InviteSubjectAsync(inviteId.Value, ct);
        if (subject is null) return Html(GuestPages.NotFound(), StatusCodes.Status404NotFound);

        string? error = null;
        try
        {
            foreach (var upload in (files ?? (IFormFileCollection)new FormFileCollection()).Where(f => f.Length > 0))
            {
                await using var stream = upload.OpenReadStream();
                using var buffer = new MemoryStream();
                await stream.CopyToAsync(buffer, ct);
                await photos.AddAsync(subject.Value.CampaignId, subject.Value.GuestId,
                    buffer.ToArray(), upload.ContentType, upload.FileName, ct);
            }
        }
        catch (AppException e)
        {
            // One bad file in a multi-select must not lose the ones that already uploaded, and the
            // guest is standing at a party — re-render the box with what worked and say what didn't.
            error = e.Message;
        }

        return await RenderBoxAsync(renderId, inviteId.Value, error, ct);
    }

    /// <summary>
    /// A POST rather than a DELETE: this page has no JavaScript, and a form is the only thing a plain
    /// document can send.
    /// </summary>
    [HttpPost("/r/{renderId}/photos/{photoId:guid}/delete")]
    public async Task<IActionResult> RemovePhoto(string renderId, Guid photoId, CancellationToken ct)
    {
        var inviteId = Admitted(renderId);
        if (inviteId is null) return Html(GuestPages.Expired(), StatusCodes.Status401Unauthorized);

        var subject = await invites.InviteSubjectAsync(inviteId.Value, ct);
        if (subject is null) return Html(GuestPages.NotFound(), StatusCodes.Status404NotFound);

        string? error = null;
        try { await photos.DeleteAsync(subject.Value.CampaignId, photoId, subject.Value.GuestId, ct); }
        catch (AppException e) { error = e.Message; }

        return await RenderBoxAsync(renderId, inviteId.Value, error, ct);
    }

    private async Task<IActionResult> RenderBoxAsync(
        string renderId, Guid inviteId, string? error, CancellationToken ct)
    {
        var subject = await invites.InviteSubjectAsync(inviteId, ct);
        if (subject is null) return Html(GuestPages.NotFound(), StatusCodes.Status404NotFound);

        var box = await photos.GetAsync(subject.Value.CampaignId, subject.Value.GuestId, ct);

        var html = GuestPages.Photos(
            $"/r/{renderId}/photos",
            $"/r/{renderId}",
            box.EventTitle,
            box.Photos
                .Select(p => (p.Id, p.ThumbUrl, p.Url, p.OriginalUrl, p.UploaderName, p.CanDelete))
                .ToList(),
            box.CanUpload,
            error,
            await PaletteAsync(inviteId, ct));

        // Unlike the other guest pages this one is mostly photographs, so it needs img-src — and
        // unlike the invitation it is NOT sandboxed, because it holds the session and posts forms.
        return Html(html, imagesFrom: "'self'");
    }

    [HttpGet("/i/{token}/rsvp")]
    public async Task<IActionResult> RsvpForm(string token, CancellationToken ct)
    {
        object result;
        try { result = await invites.GetByTokenAsync(token, ClientIp(), Render, ct); }
        catch (AppException) { return Html(GuestPages.NotFound(), StatusCodes.Status404NotFound); }

        if (result is InviteCancelledResponse c) return Html(GuestPages.Cancelled(c.Message));
        if (result is not InviteViewResponse view)
            return Html(GuestPages.Reauth(token, null, null, codeSent: false));

        var data = view.Data;
        return Html(GuestPages.Rsvp($"/i/{token}/rsvp",
            data["guest"]?["name"]?.ToString() ?? "Friend",
            data["event"]?["title"]?.ToString() ?? "You're invited",
            null,
            await PaletteAsync(view.InviteId, ct)));
    }

    [HttpPost("/i/{token}/rsvp")]
    public async Task<IActionResult> RsvpSubmit(
        string token, [FromForm] string status, [FromForm] int? guestCount, [FromForm] string? comment,
        CancellationToken ct)
    {
        try
        {
            var result = await invites.RsvpAsync(token, ClientIp(),
                new RsvpRequest(status, guestCount, null, comment, null, null, null), ct);

            // "Back to the invitation" only if this browser is actually admitted to one.
            var admitted = tickets.ReadTicket(Request.Cookies[RenderTickets.CookieName], DateTimeOffset.UtcNow);
            var back = admitted.Count == 0 ? string.Empty : tickets.RenderId(admitted[0]);
            return Html(GuestPages.RsvpDone(result.Rsvp, back));
        }
        catch (AppException e)
        {
            return Html(GuestPages.Rsvp($"/i/{token}/rsvp", "Friend", "You're invited", e.Message));
        }
    }

    // ---------- plumbing ----------

    /// <summary>
    /// Redeems a cross-host handoff: the app holding the session minted it, this host turns it into a
    /// cookie. Exists because a cookie's Domain may name the setting host or a parent, never a
    /// sibling, so `invites.blog` cannot admit anyone to `me.invites.blog` directly.
    /// </summary>
    [HttpGet("/h/{handoff}")]
    public IActionResult Handoff(string handoff)
    {
        var inviteId = tickets.ReadHandoff(handoff, DateTimeOffset.UtcNow);
        return inviteId is null
            ? Html(GuestPages.Expired(), StatusCodes.Status401Unauthorized)
            : Admit(inviteId.Value);
    }

    /// <summary>Issues the cookie and sends the guest on to a URL that authorizes nothing.</summary>
    private IActionResult Admit(Guid inviteId)
    {
        Response.Cookies.Append(
            RenderTickets.CookieName,
            // Added to whatever this browser was already admitted to, so opening a second invitation
            // does not lock the guest out of the first.
            tickets.Admit(Request.Cookies[RenderTickets.CookieName], inviteId, DateTimeOffset.UtcNow),
            new CookieOptions
            {
                HttpOnly = true,          // a template's script must never be able to read this
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                // No Domain: host-only, so it never travels to the account app on a sibling host.
                Expires = DateTimeOffset.UtcNow.Add(RenderTickets.TicketLifetime),
            });

        return Redirect($"/r/{tickets.RenderId(inviteId)}");
    }

    /// <summary>
    /// The invitation this request is admitted to, if any of the ones in its cookie matches this
    /// render id. A browser may hold several at once.
    /// </summary>
    private Guid? Admitted(string renderId)
    {
        foreach (var id in tickets.ReadTicket(Request.Cookies[RenderTickets.CookieName], DateTimeOffset.UtcNow))
            if (string.Equals(tickets.RenderId(id), renderId, StringComparison.Ordinal)) return id;
        return null;
    }

    /// <summary>Absolute URL for a path on this host, so links inside the invitation are shareable.</summary>
    private string PublicUrl(string path) => $"{Request.Scheme}://{Request.Host}{path}";

    private const string ChallengeCookie = "ib_challenge";

    private static CookieOptions ChallengeCookieOptions() => new()
    {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        Expires = DateTimeOffset.UtcNow.AddMinutes(15),
    };

    /// <param name="imagesFrom">
    /// An <c>img-src</c> source list, for the one page here that shows pictures. Every other guest
    /// page is text and buttons, and stays on the tighter policy that permits no images at all —
    /// which is the right default when the alternative is a page that renders a remote pixel.
    /// </param>
    private IActionResult Html(string html, int status = StatusCodes.Status200OK, string? imagesFrom = null)
    {
        Response.StatusCode = status;
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers["X-Content-Type-Options"] = "nosniff";
        // These pages hold the session and post forms, so they are NOT sandboxed — but they run no
        // template code either, so everything they need is first-party and inline.
        Response.Headers["Content-Security-Policy"] =
            "default-src 'none'; style-src 'unsafe-inline'; form-action 'self'; base-uri 'none'; "
            + "frame-ancestors 'none'"
            + (imagesFrom is null ? "" : $"; img-src {imagesFrom}");
        return Content(html, "text/html; charset=utf-8");
    }

    private InviteRenderData Render(
        Campaign campaign, Template template, Guest guest, Invite invite, string inviteLink,
        string? inviterName, string? inviterPhone, string? inviterEmail)
    {
        var p = renderer.Build(campaign, template, guest, invite, inviteLink, inviterName, inviterPhone, inviterEmail);
        return new InviteRenderData(p.PackageUrl, p.Data, p.RequiresOtp, p.CampaignStatus);
    }

    // Requires Program.cs's ForwardedHeaders middleware — without it every guest shares Caddy's IP
    // and the personal-link IP binding silently trusts everyone.
    private string? ClientIp() => HttpContext.Connection.RemoteIpAddress?.ToString();
}
