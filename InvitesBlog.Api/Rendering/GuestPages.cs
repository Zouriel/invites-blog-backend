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
    /// <summary>
    /// The delete enhancement, read from the assembly. Optional by design: the Remove control is a
    /// link to a confirmation page that works without it, and this only makes the question instant.
    /// </summary>
    private static readonly string Enhancement = LoadAsset("gallery.js");
    private static readonly string UploadEnhancement = LoadAsset("gallery-upload.js");

    private static string LoadAsset(string name)
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        var key = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("." + name, StringComparison.Ordinal));
        if (key is null) return string.Empty;
        using var stream = asm.GetManifestResourceStream(key);
        if (stream is null) return string.Empty;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

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
        .opt { opacity:.75; }
        /* A field people never see, so only a script filling every input fills it in. */
        .hp { position:absolute; left:-10000px; width:1px; height:1px; overflow:hidden; }
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
        /* A clip is a poster frame until it is opened, and a poster frame is indistinguishable from
           a photograph without this. The triangle is the only thing on the tile that says otherwise. */
        .tile[data-kind="video"]::after { content:""; position:absolute; left:50%; top:50%;
            width:44px; height:44px; margin:-22px 0 0 -22px; border-radius:999px; pointer-events:none;
            background:rgba(0,0,0,.45) no-repeat center;
            background-image:url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24' fill='white'%3E%3Cpath d='M8 5v14l11-7z'/%3E%3C/svg%3E");
            background-size:22px 22px; }
        .who { position:absolute; left:0; right:0; bottom:0; padding:14px 6px 4px; font-size:.7rem;
               color:#fff; background:linear-gradient(transparent, rgba(0,0,0,.6));
               white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
        /* No controls on a tile: a tap opens the photo (Download and Remove are there), a long press
           or Select starts choosing several. Nothing to long-press-save or drag in the way. */
        .tile, .tile * { -webkit-touch-callout:none; -webkit-user-select:none; user-select:none; -webkit-tap-highlight-color:transparent; }
        .tile .open { display:block; width:100%; height:100%; }
        .likes { display:inline-flex; align-items:center; gap:3px; padding:2px 7px; border-radius:999px;
          background:rgba(0,0,0,.55); color:#fff; font-size:.72rem; font-weight:600; line-height:1.2; pointer-events:none; }
        .likes svg { width:12px; height:12px; fill:none; stroke:currentColor; stroke-width:2; stroke-linejoin:round; }
        .tile .likes { position:absolute; right:5px; bottom:5px; z-index:1; }
        .tile img { transition:transform .18s ease; }
        .grid.choosing .tile::before { content:""; position:absolute; top:6px; left:6px; z-index:1; width:22px; height:22px;
            box-sizing:border-box; border-radius:999px; border:2px solid #fff; background:rgba(0,0,0,.35);
            box-shadow:0 0 3px rgba(0,0,0,.4); pointer-events:none; }
        .grid.choosing .tile.on::before { background:var(--accent) no-repeat center; border-color:var(--accent);
            background-image:url("data:image/svg+xml,%3Csvg xmlns='http://www.w3.org/2000/svg' viewBox='0 0 24 24'%3E%3Cpath d='M5 12.5l4.5 4.5L19 7.5' fill='none' stroke='white' stroke-width='3' stroke-linecap='round' stroke-linejoin='round'/%3E%3C/svg%3E");
            background-size:14px 14px; }
        .tile.on { outline:3px solid var(--accent); outline-offset:-3px; }
        .tile.on img { transform:scale(.9); }
        @media (prefers-reduced-motion: reduce) { .tile img { transition:none; } }
        .head { display:flex; align-items:center; justify-content:space-between; gap:12px; margin:0 0 1.4rem; }
        .head .sub { margin:0; }
        .pick { width:auto; margin:0; padding:.45rem .9rem; font-size:.85rem; font-weight:600; border-radius:999px;
                cursor:pointer; color:var(--ink); background:transparent; border:1px solid var(--line); }
        .pick[hidden] { display:none; }
        /* The bar while choosing, in place of the upload bar. */
        .sel { position:fixed; left:0; right:0; bottom:0; z-index:5; display:flex; align-items:center; gap:6px;
               padding:10px 12px calc(10px + env(safe-area-inset-bottom)); background:var(--card); border-top:1px solid var(--line); }
        .sel[hidden] { display:none; }
        .sel__n { flex:1 1 0; min-width:0; font-weight:600; font-size:.9rem; white-space:nowrap; overflow:hidden; text-overflow:ellipsis; }
        .sel button { flex:none; width:auto; margin:0; min-height:44px; padding:0 .8rem; font-size:.9rem; font-weight:600;
                      border-radius:10px; cursor:pointer; color:var(--ink); background:transparent; border:1px solid var(--line); }
        .sel button[hidden] { display:none; }
        .sel button:disabled { opacity:.5; }
        .sel .sel__x { border:0; min-width:44px; padding:0; font-size:1rem; }
        .sel .sel__danger { color:var(--bad); border-color:var(--bad); }
        body.choosing .bar { display:none; }
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
        .venue { display:flex; align-items:center; gap:10px; margin-bottom:12px; font-size:.85rem;
                 font-weight:600; letter-spacing:.04em; text-transform:uppercase; opacity:.75; }
        .venue__logo { width:36px; height:36px; object-fit:contain; border-radius:8px; }
        .credit { margin-top:1.6rem; font-size:.78rem; opacity:.6; }
        .credit a { color:inherit; }

        /* Asked in the page rather than on one of its own: a round trip before the question is a
           second of nothing happening, and it lands before any feedback that the tap registered. */
        .ask { position:fixed; inset:0; z-index:10; display:grid; place-items:center; padding:24px;
               background:rgba(0,0,0,.62); }
        .ask[hidden] { display:none; }
        .ask__card { width:100%; max-width:22rem; padding:26px 24px; border-radius:16px;
                     background:var(--card); border:1px solid var(--line); }
        .ask__card h2 { font:600 1.15rem/1.3 Georgia, serif; margin:0 0 .5rem; }
        .ask__card p { margin:0 0 1.2rem; }
        .ask__card button { width:100%; margin-top:.6rem; padding:.8rem 1rem; font-size:1rem; font-weight:600;
                            border:0; border-radius:10px; cursor:pointer;
                            color:var(--on-accent); background:var(--accent); }
        .ask__card button.ghost { background:transparent; color:var(--muted); border:1px solid var(--line); }
        .ask__card button:disabled { opacity:.6; }
        .ask__card a.go { display:block; box-sizing:border-box; width:100%; margin-top:.6rem; padding:.8rem 1rem;
                          text-align:center; text-decoration:none; font-size:1rem; font-weight:600; border-radius:10px;
                          color:var(--on-accent); background:var(--accent); }
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

    /// <summary>
    /// Asked before the camera on a static invitation opens. Most of those are opened from one shared
    /// link, so we don't know who is holding the phone, and the host wants to know who took what.
    /// </summary>
    public static string CameraName(string action, string eventTitle, string? error, GuestPalette? palette = null)
    {
        var body = $"""
            <h1>{E(eventTitle)}</h1>
            <p>What's your name? The host will see it next to the photos you take.</p>
            <form method="post" action="{E(action)}">
              <label for="name">Your name</label>
              <input class="text" id="name" name="name" maxlength="60" autocomplete="name" required autofocus>
              {(error is null ? "" : $"<p class=\"err\">{E(error)}</p>")}
              <button type="submit">Open the camera</button>
            </form>
            """;
        return Shell("Your name", body, palette);
    }

    /// <summary>The camera outside its window: open from the day before the event to the end of the day after.</summary>
    /// <param name="note">Why, in the words the photo box uses: not open yet, closed, or cancelled.</param>
    public static string CameraClosed(string eventTitle, string backPath, GuestPalette? palette = null, string? note = null) =>
        Shell("Camera closed", $"""
            <h1>{E(eventTitle)}</h1>
            <p>{E(note ?? "The camera isn't open right now. It opens the day before the event.")}</p>
            <p><a href="{E(backPath)}">Back to the invitation</a></p>
            """, palette);

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

    /// <summary>
    /// The reply form behind an open guest list. Unlike <see cref="Rsvp"/> we don't know who this
    /// is, so it asks for a name first; the phone or email is optional and only shown to the host.
    /// The answer buttons come last so the name and count are filled in before one is pressed.
    /// </summary>
    public static string Join(string action, string eventTitle, string? error, JoinValues? values = null,
        GuestPalette? palette = null)
    {
        var v = values ?? new JoinValues(null, null, null, null);
        var body = $"""
            <h1>{E(eventTitle)}</h1>
            <p>Let the host know if you're coming.</p>
            <form method="post" action="{E(action)}">
              <label for="name">Your name</label>
              <input class="text" id="name" name="name" maxlength="80" autocomplete="name" required value="{E(v.Name ?? "")}">
              <label for="contact">Phone or email <span class="opt">(optional)</span></label>
              <input class="text" id="contact" name="contact" maxlength="120" value="{E(v.Contact ?? "")}">
              <label for="guestCount">How many of you, including yourself?</label>
              <input class="text" id="guestCount" name="guestCount" inputmode="numeric" value="{E(v.GuestCount ?? "1")}">
              <label for="comment">Anything the host should know? <span class="opt">(optional)</span></label>
              <input class="text" id="comment" name="comment" maxlength="500" value="{E(v.Comment ?? "")}">
              <div class="hp" aria-hidden="true"><label for="website">Leave this empty</label><input id="website" name="website" tabindex="-1" autocomplete="off"></div>
              {(error is null ? "" : $"<p class=\"err\">{E(error)}</p>")}
              <button type="submit" name="status" value="Going">Yes, I'll be there</button>
              <button class="ghost" type="submit" name="status" value="Maybe">I'm not sure yet</button>
              <button class="ghost" type="submit" name="status" value="NotGoing">Sorry, I can't make it</button>
            </form>
            <p class="foot">Only the host sees your name and details.</p>
            """;
        return Shell("Reply", body, palette);
    }

    /// <summary>What was typed into <see cref="Join"/>, put back when it has to be shown again with an error.</summary>
    public sealed record JoinValues(string? Name, string? Contact, string? GuestCount, string? Comment);

    /// <summary>The open guest list is off, or the link was changed: the invitation still opens, replies don't.</summary>
    public static string JoinClosed(string eventTitle, string backPath, GuestPalette? palette = null) => Shell("Replies closed", $"""
        <h1>{E(eventTitle)}</h1>
        <p>This invitation isn't taking replies here any more. If you're coming, let the host know directly.</p>
        <p><a href="{E(backPath)}">Back to the invitation</a></p>
        """, palette);

    public static string RsvpDone(string status, string renderId, GuestPalette? palette = null) =>
        Shell("Thank you", $"""
        <h1>{(status == "Going" ? "Thanks, see you there" : "Thank you for letting us know")}</h1>
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
        IReadOnlyList<(Guid Id, string ThumbUrl, string Url, string OriginalUrl, string? Who,
            bool CanDelete, bool IsVideo)> photos,
        bool canUpload, string? error, string cameraPath, GuestPalette? palette = null,
        string? nonce = null, Application.Dtos.Invites.GuestCredit? credit = null,
        IReadOnlyDictionary<Guid, int>? likes = null, int? selectLimit = null, string? premiumUrl = null)
    {
        var tiles = photos.Count == 0
            ? """
              <p class="empty">No photos yet. Anything you take at the event will show up here for
                 everyone who was invited.</p>
              """
            : $"""
               <div class="grid">
                 {string.Concat(photos.Select(p => $"""
                   <div class="tile"{(p.IsVideo ? " data-kind=\"video\"" : "")} data-id="{p.Id}" data-original="{E(p.OriginalUrl)}"{(p.CanDelete ? " data-mine=\"1\"" : "")}>
                     <a class="open" href="{E(action)}/{p.Id}"><img src="{E(p.ThumbUrl)}" alt="" loading="lazy" draggable="false"></a>
                     {(p.Who is null ? "" : $"<span class=\"who\">{E(p.Who)}</span>")}
                     {LikesPill(likes?.GetValueOrDefault(p.Id) ?? 0)}
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
              {GuestCreditHtml.AlbumHeader(credit)}
              <h1>{E(eventTitle)}</h1>
              <div class="head">
                <p class="sub">{(photos.Count == 1 ? "1 photo" : $"{photos.Count} photos")} from the event</p>
                {(photos.Count > 0 && nonce is not null ? "<button type=\"button\" class=\"pick\" id=\"pick\" hidden>Select</button>" : "")}
              </div>
              {(error is null ? "" : $"<p class=\"err\">{E(error)}</p>")}
              {tiles}
              <a class="back" href="{E(backTo)}">Back to the invitation</a>
              {GuestCreditHtml.AlbumFooter(credit)}
            </div>
            {bar}
            {(nonce is null ? "" : $"""
              <div class="sel" id="sel" role="region" aria-label="Selected photos" hidden{(selectLimit is { } cap ? $" data-limit=\"{cap}\"" : "")}>
                <button type="button" class="sel__x" id="sel-cancel" aria-label="Stop selecting">&#10005;</button>
                <span class="sel__n" id="sel-count" aria-live="polite">0 selected</span>
                <button type="button" class="sel__b" id="sel-all">Select all</button>
                <button type="button" class="sel__b" id="sel-share" hidden>Share</button>
                <button type="button" class="sel__b sel__danger" id="sel-remove" hidden>Remove</button>
              </div>
              <div class="ask" id="busy" hidden>
                <div class="ask__card" role="status">
                  <h2 id="busy-title">Getting your photos ready</h2>
                  <p id="busy-count">0 of 0</p>
                  <button type="button" class="ghost" id="busy-cancel">Cancel</button>
                </div>
              </div>
              <div class="ask" id="ready" hidden>
                <div class="ask__card">
                  <h2 id="ready-title">Ready to share</h2>
                  <p id="ready-text">Tap Share to choose where they go.</p>
                  <button type="button" id="ready-yes">Share</button>
                  <button type="button" class="ghost" id="ready-no">Not now</button>
                </div>
              </div>
              {(selectLimit is { } most ? $$"""
              <div class="ask" id="limit" hidden>
                <div class="ask__card" role="dialog" aria-labelledby="limit-title">
                  <h2 id="limit-title">Up to {{most}} at a time</h2>
                  <p>Download or share more than {{most}} at once with Premium.</p>
                  {{(premiumUrl is null ? "" : $"<a class=\"go\" href=\"{E(premiumUrl)}\" target=\"_blank\" rel=\"noopener\">Get Premium</a>")}}
                  <button type="button" class="ghost" id="limit-no">Not now</button>
                </div>
              </div>
              """ : "")}
              <div class="ask" id="confirm" hidden>
                <div class="ask__card">
                  <h2 id="confirm-title">Remove this photo?</h2>
                  <p id="confirm-text">It disappears from everyone's photo box. This cannot be undone.</p>
                  <button type="button" id="confirm-yes">Remove it</button>
                  <button type="button" class="ghost" id="confirm-no">Keep it</button>
                </div>
              </div>
              <script nonce="{nonce}">
              window.__ibGallery = true;
              {Enhancement}
              {UploadEnhancement}
              </script>
              """)}
            """;

        return $"""
            <!doctype html>
            <html lang="en"><head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="robots" content="noindex, nofollow">
            <title>Photos from {E(eventTitle)}</title>
            <style>{BoxCss(palette ?? GuestPalette.Fallback)}</style>
            </head><body>{body}</body></html>
            """;
    }

    /// <summary>
    /// What a downloaded or shared file is called, the same names the app and the website use:
    /// <c>invitesblog-</c> and the first 8 characters of the id, with the stored file's extension.
    /// </summary>
    public static string DownloadName(Guid id, string originalUrl, bool isVideo)
    {
        var path = Uri.TryCreate(originalUrl, UriKind.Absolute, out var abs) ? abs.AbsolutePath : originalUrl.Split('?')[0];
        var dot = path.LastIndexOf('.');
        var ext = dot < 0 ? "" : path[dot..].ToLowerInvariant();
        if (!System.Text.RegularExpressions.Regex.IsMatch(ext, "^\\.[a-z0-9]{2,5}$")) ext = isVideo ? ".mp4" : ".jpg";
        return $"invitesblog-{id.ToString("N")[..8]}{ext}";
    }

    /// <summary>The viewer page's styles, over the album's.</summary>
    private const string ViewCss = """
        body { background:#000; color:#fff; }
        .view { min-height:100vh; min-height:100svh; display:flex; flex-direction:column; }
        .view__bar, .view__nav { display:flex; align-items:center; gap:8px; padding:10px 12px; flex-wrap:wrap; }
        .view__bar { padding-top:calc(10px + env(safe-area-inset-top)); }
        .view__nav { justify-content:space-between; padding-bottom:calc(10px + env(safe-area-inset-bottom)); }
        .view__sp { flex:1; }
        .view__btn { display:inline-flex; align-items:center; min-height:44px; padding:0 .9rem; border-radius:10px;
        font:600 .9rem/1 ui-sans-serif, system-ui, sans-serif; color:#fff; text-decoration:none;
        background:rgba(255,255,255,.12); border:0; cursor:pointer; width:auto; margin:0; }
        .view__btn[hidden] { display:none; }
        .view__danger { color:#ffb4b4; }
        .view__stage { flex:1; min-height:0; display:grid; place-items:center; padding:0 8px; }
        .media { max-width:100%; max-height:calc(100svh - 140px); object-fit:contain; display:block; }
        .view__who { font-size:.8rem; opacity:.75; }
        """;

    /// <summary>
    /// One photo or clip, full screen: the guest album's viewer. A tap on a tile lands here (it used to
    /// open the bare file). Download, Share (with script) and Remove (for the guest's own, through the
    /// confirmation page) live here and in the album's selection bar, never on a tile.
    /// </summary>
    /// <summary>The heart and count in a tile's corner; nothing at 0. A guest by link sees hearts but can't add one.</summary>
    public static string LikesPill(int count) => count <= 0
        ? ""
        : $"<span class=\"likes\" role=\"img\" aria-label=\"{count} {(count == 1 ? "like" : "likes")}\"><svg viewBox=\"0 0 24 24\" aria-hidden=\"true\"><path d=\"M12 20.5C9 18.6 2.5 14.4 2.5 9 2.5 5.9 4.8 3.75 7.5 3.75c1.9 0 3.5 1.05 4.5 2.65 1-1.6 2.6-2.65 4.5-2.65 2.7 0 5 2.15 5 5.25 0 5.4-6.5 9.6-9.5 11.5z\"/></svg>{CompactCount(count)}</span>";

    /// <summary>1234 -> "1.2k": the same shortening as the apps' like counts.</summary>
    public static string CompactCount(int n)
    {
        if (n < 1000) return n.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var (d, s) = n < 1_000_000 ? (1000.0, "k") : (1_000_000.0, "m");
        var x = n / d;
        var v = x < 10 ? Math.Floor(x * 10) / 10 : Math.Floor(x);
        return v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) + s;
    }

    public static string PhotoView(
        string boxPath, Guid id, string url, string originalUrl, bool isVideo, string? who, bool canDelete,
        string? prevPath, string? nextPath, string downloadName, GuestPalette? palette = null, string? nonce = null,
        int likes = 0)
    {
        var media = isVideo
            ? $"<video class=\"media\" src=\"{E(url)}\" controls playsinline preload=\"metadata\"></video>"
            : $"<img class=\"media\" src=\"{E(url)}\" alt=\"{(who is null ? "Event photo" : "Photo by " + E(who))}\">";
        var noun = isVideo ? "video" : "photo";
        var body = $"""
            <div class="view" data-id="{id}" data-original="{E(originalUrl)}">
              <div class="view__bar">
                <a class="view__btn" href="{E(boxPath)}">&#8592; Album</a>
                <span class="view__sp"></span>
                {LikesPill(likes)}
                <a class="view__btn" href="{E(originalUrl)}" download="{E(downloadName)}">Download</a>
                {(nonce is null ? "" : "<button type=\"button\" class=\"view__btn\" id=\"view-share\" hidden>Share</button>")}
                {(canDelete ? $"<a class=\"view__btn view__danger\" href=\"{E(boxPath)}/{id}/remove\">Remove</a>" : "")}
              </div>
              <div class="view__stage">{media}</div>
              <div class="view__nav">
                {(prevPath is null ? "<span></span>" : $"<a class=\"view__btn\" href=\"{E(prevPath)}\" rel=\"prev\">&#8249; Previous</a>")}
                {(who is null ? "<span></span>" : $"<span class=\"view__who\">From {E(who)}</span>")}
                {(nextPath is null ? "<span></span>" : $"<a class=\"view__btn\" href=\"{E(nextPath)}\" rel=\"next\">Next &#8250;</a>")}
              </div>
            </div>
            {(nonce is null ? "" : $"""
              <div class="ask" id="busy" hidden>
                <div class="ask__card" role="status">
                  <h2 id="busy-title">Getting it ready</h2>
                  <p id="busy-count">0 of 1</p>
                  <button type="button" class="ghost" id="busy-cancel">Cancel</button>
                </div>
              </div>
              <div class="ask" id="ready" hidden>
                <div class="ask__card">
                  <h2 id="ready-title">Ready to share</h2>
                  <p id="ready-text">Tap Share to choose where it goes.</p>
                  <button type="button" id="ready-yes">Share</button>
                  <button type="button" class="ghost" id="ready-no">Not now</button>
                </div>
              </div>
              <script nonce="{nonce}">
              window.__ibGallery = true;
              {Enhancement}
              </script>
              """)}
            """;

        return $"""
            <!doctype html>
            <html lang="en"><head>
            <meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <meta name="robots" content="noindex, nofollow">
            <title>A {noun} from the event</title>
            <style>{BoxCss(palette ?? GuestPalette.Fallback)}
            {ViewCss}
            </style>
            </head><body>{body}</body></html>
            """;
    }

    /// <summary>
    /// Asks before removing a photograph.
    ///
    /// <para>The button sits on the corner of the picture it deletes, which on a phone is the same
    /// place a thumb lands to open it — so a tap meant to look at a photo was destroying it, with no
    /// undo. The app-side box has always asked; this is the same question, as a page, because the
    /// gallery is served under a policy with no script to raise a dialog with.</para>
    /// </summary>
    public static string ConfirmRemove(string postAction, string backTo, GuestPalette? palette = null) =>
        Shell("Remove this photo?", $"""
            <h1>Remove this photo?</h1>
            <p>It disappears from everyone's photo box. This cannot be undone.</p>
            <form method="post" action="{E(postAction)}">
              <button type="submit">Remove it</button>
            </form>
            <p class="foot"><a href="{E(backTo)}">Keep it and go back to the photos</a></p>
            """, palette);

    public static string TooMany() => Shell("Please wait", """
        <h1>Please wait a few minutes</h1>
        <p>Lots of replies have come from this connection just now. Try again in a few minutes.</p>
        """);

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

    /// <summary>
    /// Where every button inside a host's preview ("View invite") leads: the reply, camera, album and calendar
    /// all belong to a real guest's own invitation, so nothing here records anything.
    /// </summary>
    public static string PreviewOnly(string back) => Shell("Preview", $"""
        <h1>This is a preview</h1>
        <p>It shows your invitation the way your guests see it. Replies, photos and the calendar work from each
           guest's own invitation, so nothing you press here is recorded.</p>
        <p><a href="{E(back)}">Back to the invitation</a></p>
        """);

    /// <summary>A preview link that has expired or was opened somewhere else: open it again from the event.</summary>
    public static string PreviewExpired() => Shell("Open the preview again", """
        <h1>Please open the preview again</h1>
        <p>This preview link has expired. Press View invite on your event again.</p>
        """);

    public static string Unavailable() => Shell("Invitation unavailable", """
        <h1>We couldn't open this invitation</h1>
        <p>Something went wrong preparing it. Please try again in a moment.</p>
        """);
}
