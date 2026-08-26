using System.Net;
using System.Text;

namespace InvitesBlog.Api.Rendering;

/// <summary>
/// The small server-rendered pages either side of an invitation: the "we don't recognise this
/// device" challenge, the RSVP form, and the handful of dead ends. Deliberately plain HTML with
/// inline CSS and no framework — a guest arriving here is one hop from the thing they actually came
/// for, and shipping an SPA shell to draw a six-digit code box would undo the reason the invitation
/// itself is server-rendered.
///
/// These are ORDINARY documents, not the sandboxed one. They carry the session cookie and post forms,
/// so they must never be served with the invitation's <c>sandbox</c> policy.
/// </summary>
public static class GuestPages
{
    /// <summary>Escapes text for HTML. Everything interpolated below goes through this.</summary>
    private static string E(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);

    /// <summary>The card pages' styles, over whichever palette the guest's invitation uses.</summary>
    private static string Css(GuestPalette p) => p.Root + "\n" + CssBody;

    private const string CssBody = """
        * { box-sizing: border-box; }
        body { margin:0; min-height:100vh; display:grid; place-items:center; padding:24px;
               background:var(--bg); color:var(--ink);
               font:16px/1.55 ui-sans-serif, system-ui, -apple-system, Segoe UI, Roboto, sans-serif; }
        .card { width:100%; max-width:26rem; background:var(--card); border:1px solid var(--line);
                border-radius:16px; padding:32px 28px; }
        h1 { font:600 1.4rem/1.3 Georgia, serif; margin:0 0 .6rem; }
        p { margin:0 0 1rem; color:var(--muted); }
        label { display:block; font-size:.85rem; color:var(--muted); margin:1.2rem 0 .4rem; }
        input { width:100%; padding:.85rem 1rem; font-size:1.35rem; letter-spacing:.35em;
                text-align:center; background:var(--card); color:var(--ink);
                border:1px solid var(--line); border-radius:10px; }
        input.text { font-size:1rem; letter-spacing:normal; text-align:left; }
        button { width:100%; margin-top:1.2rem; padding:.9rem 1rem; font-size:1rem; font-weight:600;
                 color:var(--on-accent); background:var(--accent); border:0; border-radius:10px; cursor:pointer; }
        button.ghost { background:transparent; color:var(--muted); border:1px solid var(--line); }
        .err { color:var(--bad); font-size:.9rem; margin:.8rem 0 0; }
        .foot { margin-top:1.6rem; font-size:.8rem; color:var(--muted); }
        a { color:var(--accent); }
        """;

    /// <summary>
    /// The photo box's own styles. Kept apart from <see cref="Css"/> because everything else here is
    /// a 26rem card centred in the viewport, and a grid of photographs is the one page that wants the
    /// whole screen.
    /// </summary>
    private static string BoxCss(GuestPalette p) => p.Root + "\n" + BoxCssBody;

    private const string BoxCssBody = """
        * { box-sizing: border-box; }
        body { margin:0; min-height:100vh; background:var(--bg); color:var(--ink);
               font:16px/1.55 ui-sans-serif, system-ui, -apple-system, Segoe UI, Roboto, sans-serif; }
        .wrap { max-width:56rem; margin:0 auto; padding:24px 16px 96px; }
        h1 { font:600 1.5rem/1.25 Georgia, serif; margin:0 0 .3rem; }
        .sub { color:var(--muted); font-size:.9rem; margin:0 0 1.4rem; }
        .err { color:var(--bad); font-size:.9rem; margin:0 0 1rem; }
        /* Square tiles, gapless-ish — the shape everyone already reads as "a photo grid". */
        .grid { display:grid; grid-template-columns:repeat(3, 1fr); gap:4px; }
        @media (min-width:40rem) { .grid { grid-template-columns:repeat(4, 1fr); gap:6px; } }
        .tile { position:relative; display:block; aspect-ratio:1; background:var(--card);
                border-radius:4px; overflow:hidden; }
        .tile img { width:100%; height:100%; object-fit:cover; display:block; }
        .who { position:absolute; left:0; right:0; bottom:0; padding:14px 6px 4px; font-size:.7rem;
               color:#fff; background:linear-gradient(transparent, rgba(0,0,0,.6));
               white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
        .rm { position:absolute; top:4px; right:4px; }
        .dl { position:absolute; top:4px; left:4px; width:22px; height:22px; display:grid;
              place-items:center; font-size:.8rem; text-decoration:none; color:#fff;
              background:rgba(0,0,0,.55); border-radius:999px; }
        .rm button { width:auto; margin:0; padding:2px 7px; font-size:.75rem; line-height:1.5;
                     color:#fff; background:rgba(0,0,0,.55); border:0; border-radius:999px; cursor:pointer; }
        .empty { padding:44px 20px; text-align:center; color:var(--muted);
                 border:1px dashed var(--line); border-radius:14px; }
        /* Fixed, because the point of this page is the button and the grid can be a thousand tiles. */
        .bar { position:fixed; left:0; right:0; bottom:0; padding:12px 16px calc(12px + env(safe-area-inset-bottom));
               background:var(--bg); border-top:1px solid var(--line); }
        .acts { max-width:56rem; margin:0 auto; display:flex; flex-direction:column; gap:10px; }
        .bar form { display:flex; gap:10px; align-items:center; }
        .bar input[type=file] { flex:1; min-width:0; font-size:.85rem; color:var(--muted); }
        /* Side by side once there is room; stacked on a phone, camera first. */
        @media (min-width:34rem) {
          .acts { flex-direction:row; align-items:center; }
          .bar form { flex:1; }
        }
        .bar button, .bar .cam { width:auto; margin:0; padding:.7rem 1.1rem; font-size:.95rem; font-weight:600;
                      color:var(--on-accent); background:var(--accent); border:0; border-radius:10px; cursor:pointer; }
        .bar .cam { display:block; text-align:center; text-decoration:none; flex:0 0 auto; }
        .back { display:inline-block; margin-top:1.4rem; font-size:.9rem; color:var(--accent); }
        a { color:var(--accent); }
        """;

    private static string Shell(string title, string body, GuestPalette? palette = null) => $"""
        <!doctype html>
        <html lang="en"><head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="robots" content="noindex, nofollow">
        <title>{E(title)}</title>
        <style>{Css(palette ?? GuestPalette.Fallback)}</style>
        </head><body><main class="card">{body}</main></body></html>
        """;

    /// <summary>
    /// The reauth challenge. The guest is never asked WHERE to send the code — the link is already
    /// user-bound, so revealing or accepting a contact here would turn a link into an oracle for the
    /// address behind it.
    /// </summary>
    public static string Reauth(string token, string? channel, string? error, bool codeSent)
    {
        var where = channel switch
        {
            "email" => "your email",
            "sms" => "your phone",
            _ => "you"
        };

        var body = codeSent
            ? $"""
               <h1>Just checking it's you</h1>
               <p>We sent a six-digit code to {E(where)}. It expires in a few minutes.</p>
               <form method="post" action="/i/{E(token)}/verify">
                 <label for="code">Enter the code</label>
                 <input id="code" name="code" inputmode="numeric" autocomplete="one-time-code"
                        pattern="[0-9]*" maxlength="6" required autofocus>
                 {(error is null ? "" : $"<p class=\"err\">{E(error)}</p>")}
                 <button type="submit">Open my invitation</button>
               </form>
               <form method="post" action="/i/{E(token)}/reauth">
                 <button class="ghost" type="submit">Send a new code</button>
               </form>
               """
            : $"""
               <h1>We don't recognise this device</h1>
               <p>This invitation is personal, so we'll send a short code to check it's really you.</p>
               {(error is null ? "" : $"<p class=\"err\">{E(error)}</p>")}
               <form method="post" action="/i/{E(token)}/reauth">
                 <button type="submit">Send me a code</button>
               </form>
               """;

        return Shell("Your invitation", body);
    }

    /// <summary>The RSVP form. A plain POST — the invitation links here rather than embedding it.</summary>
    /// <param name="action">Where to post — the caller decides, because the path a guest arrived by
    /// (token or cookie) is what authorizes their answer.</param>
    public static string Rsvp(string action, string guestName, string eventTitle, string? error,
        GuestPalette? palette = null)
    {
        var body = $"""
            <h1>{E(eventTitle)}</h1>
            <p>{E(guestName)}, will you be there?</p>
            <form method="post" action="{E(action)}">
              <button type="submit" name="status" value="Going">Yes, I'll be there</button>
              <button class="ghost" type="submit" name="status" value="Maybe">I'm not sure yet</button>
              <button class="ghost" type="submit" name="status" value="NotGoing">Sorry, I can't make it</button>
              <label for="guestCount">How many of you, including yourself?</label>
              <input class="text" id="guestCount" name="guestCount" inputmode="numeric" value="1">
              <label for="comment">Anything the host should know?</label>
              <input class="text" id="comment" name="comment" maxlength="500">
              {(error is null ? "" : $"<p class=\"err\">{E(error)}</p>")}
            </form>
            """;
        return Shell("RSVP", body, palette);
    }

    public static string RsvpDone(string status, string renderId, GuestPalette? palette = null) =>
        Shell("Thank you", $"""
        <h1>{(status == "Going" ? "Wonderful — see you there" : "Thank you for letting us know")}</h1>
        <p>Your reply has been sent to the host.</p>
        <p><a href="/r/{E(renderId)}">Back to the invitation</a></p>
        """, palette);

    /// <summary>
    /// The event photo box, as a guest on a server-rendered invitation sees it (§5). A plain
    /// multipart form, because the rendered invitation this is reached from is sandboxed under a CSP
    /// with <c>default-src 'none'</c> and could not upload anything itself even if it wanted to — it
    /// links here, exactly as it links to the RSVP.
    /// </summary>
    /// <param name="action">
    /// The base path this box lives at. Upload posts to it, and each delete posts to
    /// <c>{action}/{photoId}/delete</c> — so the caller's own path is what authorizes both, and a
    /// guest who arrived by one route never has their actions routed through another.
    /// </param>
    public static string Photos(
        string action, string backTo, string eventTitle,
        IReadOnlyList<(Guid Id, string ThumbUrl, string Url, string OriginalUrl, string? Who, bool CanDelete)> photos,
        bool canUpload, string? error, string cameraPath, GuestPalette? palette = null)
    {
        var tiles = photos.Count == 0
            ? """
              <p class="empty">No photos yet. Be the first — whatever you shoot tonight lands here for
                 everyone who was there.</p>
              """
            : $"""
               <div class="grid">
                 {string.Concat(photos.Select(p => $"""
                   <div class="tile">
                     <a href="{E(p.Url)}"><img src="{E(p.ThumbUrl)}" alt="" loading="lazy"></a>
                     <a class="dl" href="{E(p.OriginalUrl)}" download title="Save the original">&#8615;</a>
                     {(p.Who is null ? "" : $"<span class=\"who\">{E(p.Who)}</span>")}
                     {(p.CanDelete ? $"""
                       <form class="rm" method="post" action="{E(action)}/{p.Id}/delete">
                         <button type="submit" title="Remove this photo">Remove</button>
                       </form>
                       """ : "")}
                   </div>
                   """))}
               </div>
               """;

        // Both doors. The camera leads, because a guest standing at the party has not "captured"
        // anything yet and a viewfinder is what they actually want — but the shot they care about is
        // often already in their camera roll, and the phone that took it is the one in their hand.
        var bar = canUpload
            ? $"""
               <div class="bar">
                 <div class="acts">
                   <a class="cam" href="{E(cameraPath)}">Open the camera</a>
                   <form method="post" action="{E(action)}" enctype="multipart/form-data">
                     <input type="file" name="files" accept="image/*" multiple required
                            aria-label="Choose photos from this device">
                     <button type="submit">Add</button>
                   </form>
                 </div>
               </div>
               """
            : "";

        var body = $"""
            <div class="wrap">
              <h1>{E(eventTitle)}</h1>
              <p class="sub">{(photos.Count == 1 ? "1 photo" : $"{photos.Count} photos")} from the night</p>
              {(error is null ? "" : $"<p class=\"err\">{E(error)}</p>")}
              {tiles}
              <a class="back" href="{E(backTo)}">Back to the invitation</a>
            </div>
            {bar}
            """;

        return $"""
            <!doctype html>
            <html lang="en"><head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="robots" content="noindex, nofollow">
            <title>{E(eventTitle)} — photos</title>
            <style>{BoxCss(palette ?? GuestPalette.Fallback)}</style>
            </head><body>{body}</body></html>
            """;
    }

    public static string Cancelled(string? message) => Shell("Event cancelled", $"""
        <h1>This event has been cancelled</h1>
        <p>{E(string.IsNullOrWhiteSpace(message) ? "The host has cancelled this event." : message)}</p>
        """);

    public static string NotFound() => Shell("Invitation not found", """
        <h1>This invitation isn't available</h1>
        <p>The link may have expired, or it may have been replaced by a newer one. If someone sent it
           to you, ask them for the latest link.</p>
        """);

    /// <summary>
    /// Shown when a render URL is opened without a valid cookie. It cannot bounce back to the invite
    /// link, because the render URL deliberately carries no token to bounce with — so it asks the
    /// guest for the one thing that still works.
    /// </summary>
    public static string Expired() => Shell("Open your invitation again", """
        <h1>Please open your invitation again</h1>
        <p>For your privacy this page forgets you after a while. Open the original link from your
           message and it will let you straight back in.</p>
        """);

    public static string Unavailable() => Shell("Invitation unavailable", """
        <h1>We couldn't open this invitation</h1>
        <p>Something went wrong preparing it. Please try again in a moment.</p>
        """);
}
