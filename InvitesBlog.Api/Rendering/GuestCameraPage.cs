using System.Net;
using System.Reflection;

namespace InvitesBlog.Api.Rendering;

/// <summary>
/// The event camera (§5) — a viewfinder a guest can shoot from, in the browser they already have.
///
/// <para><b>Why it is a page and not part of the invitation.</b> The invitation is served
/// <c>sandbox</c> with <c>default-src 'none'</c>, which blocks this twice over: nothing can be
/// uploaded from it, and a sandbox without <c>allow-same-origin</c> puts the document in an opaque
/// origin, which can never hold a camera permission. The invitation links here instead.</para>
///
/// <para><b>Why the script is inlined under a nonce.</b> These pages are assembled as strings and
/// have no bundler; a nonce lets the one script run without opening the page to
/// <c>script-src 'unsafe-inline'</c>, which would be a real weakening for a page that holds the
/// guest's session.</para>
/// </summary>
public static class GuestCameraPage
{
    private static string E(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);

    /// <summary>The client, read once from the assembly rather than pasted into a C# string.</summary>
    private static readonly string Script = Load("camera.js");

    private static string Load(string name)
    {
        var asm = Assembly.GetExecutingAssembly();
        var key = asm.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("." + name, StringComparison.Ordinal));
        if (key is null) return string.Empty;

        using var stream = asm.GetManifestResourceStream(key);
        if (stream is null) return string.Empty;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private const string Css = """
        * { box-sizing: border-box; }
        html, body { height: 100%; }
        body { margin:0; background:#000; color:#fff; overflow:hidden;
               font:15px/1.45 ui-sans-serif, system-ui, -apple-system, Segoe UI, Roboto, sans-serif;
               -webkit-user-select:none; user-select:none; }

        /* svh, not vh: the phone's URL bar must not be able to push the shutter off the screen. */
        .stage { position:relative; height:100svh; width:100%; overflow:hidden; background:#000; }
        video { position:absolute; inset:0; width:100%; height:100%; object-fit:cover; background:#000; }
        /* --crop is the fallback zoom, used only where the track cannot zoom itself. Composed with
           the mirror rather than replacing it, so a selfie stays both mirrored and framed in. */
        video { transform: scale(var(--crop, 1)); }
        video.mirror { transform: scaleX(-1) scale(var(--crop, 1)); }

        /* The focus mark. Corner brackets rather than a full square, which is what a phone camera
           draws and what reads as "focusing here" rather than "something is selected". Sits above
           the picture but below the controls, so it can never cover the shutter. */
        .reticle { position:absolute; z-index:2; width:78px; height:78px; margin:-39px 0 0 -39px;
                   pointer-events:none; }
        .reticle[hidden] { display:none; }
        .reticle i { position:absolute; width:20px; height:20px; border:2px solid rgba(255,255,255,.95);
                     /* A dark edge under the light one, so the brackets hold on a pale subject too. */
                     filter: drop-shadow(0 0 1px rgba(0,0,0,.55)); }
        .reticle i:nth-child(1) { top:0; left:0; border-right:0; border-bottom:0; border-radius:3px 0 0 0; }
        .reticle i:nth-child(2) { top:0; right:0; border-left:0; border-bottom:0; border-radius:0 3px 0 0; }
        .reticle i:nth-child(3) { bottom:0; right:0; border-left:0; border-top:0; border-radius:0 0 3px 0; }
        .reticle i:nth-child(4) { bottom:0; left:0; border-right:0; border-top:0; border-radius:0 0 0 3px; }

        /* Snaps in, settles, then dims and holds — the shape of the gesture on a native camera. */
        .reticle.go { animation: snap 260ms cubic-bezier(.2,.9,.2,1), dim 420ms 520ms forwards; }
        @keyframes snap { from { transform: scale(1.5); opacity:0; } to { transform: scale(1); opacity:1; } }
        @keyframes dim  { to { opacity:.55; } }
        @media (prefers-reduced-motion: reduce) { .reticle.go { animation: none; } }

        .flash { position:absolute; inset:0; background:#fff; opacity:0; pointer-events:none; }
        .flash.go { animation: pop 220ms ease-out; }
        @keyframes pop { from { opacity:.85; } to { opacity:0; } }

        /* Above the focus mark (z-index:2): a 78px reticle placed near the edge of the picture
           would otherwise be painted over the shutter. It could not be TAPPED through — the mark
           takes no pointer events — but it would still sit on top of the control. */
        .top { position:absolute; z-index:3; top:0; left:0; right:0; display:flex; align-items:center;
               justify-content:space-between; gap:10px; padding:calc(10px + env(safe-area-inset-top)) 12px 10px;
               background:linear-gradient(rgba(0,0,0,.55), transparent); }
        .title { font-size:.85rem; opacity:.9; overflow:hidden; text-overflow:ellipsis; white-space:nowrap; }

        /* flex:0 0 auto matters: this button shares a row with the zoom slider, and a range input
           has an intrinsic minimum width it will not go below. Left to shrink, the BUTTON gave way
           instead — squeezed under its own label, which then overflowed its centred area and read
           as text sitting off to one side. */
        .btn { display:grid; place-items:center; flex:0 0 auto; white-space:nowrap;
               min-width:44px; height:44px; padding:0 12px;
               border:0; border-radius:999px; background:rgba(0,0,0,.42); color:#fff;
               font:inherit; font-size:.85rem; cursor:pointer; -webkit-backdrop-filter:blur(6px); backdrop-filter:blur(6px); }
        .btn.on { background:var(--accent,#c9a227); color:var(--on-accent,#241d06); }
        /* Night mode brightens the picture, so the chrome over it steps back to compensate —
           otherwise the controls end up the brightest thing on a screen someone is using in a
           dark room, and the eye goes to them instead of the subject. */
        body.night .top, body.night .bottom { background:none; }
        body.night .btn:not(.on), body.night .chip:not(.on) { background:rgba(0,0,0,.62); }
        .btn:disabled { opacity:.4; }
        a.btn { text-decoration:none; }

        .bottom { position:absolute; z-index:3; left:0; right:0; bottom:0;
                  padding:10px 12px calc(14px + env(safe-area-inset-bottom));
                  background:linear-gradient(transparent, rgba(0,0,0,.6)); }

        .filters { display:flex; gap:8px; overflow-x:auto; padding:4px 2px 10px;
                   scrollbar-width:none; -webkit-overflow-scrolling:touch; }
        .filters::-webkit-scrollbar { display:none; }
        .chip { flex:0 0 auto; padding:7px 14px; border:0; border-radius:999px; cursor:pointer;
                background:rgba(0,0,0,.42); color:#fff; font:inherit; font-size:.8rem; }
        .chip.on { background:#fff; color:#111; font-weight:600; }

        .row { display:flex; align-items:center; justify-content:space-between; gap:12px; }

        /* The shutter is the one control that must be findable without looking. */
        .shoot { width:74px; height:74px; border-radius:999px; border:4px solid rgba(255,255,255,.9);
                 background:#fff; cursor:pointer; flex:0 0 auto; }
        .shoot:active { transform:scale(.93); }
        body[data-busy="1"] .shoot { opacity:.6; }

        .side { flex:1 1 0; display:flex; align-items:center; gap:8px; }
        .side.right { justify-content:flex-end; }

        /* min-width:0 lets it give up the space instead, which is what should yield here. */
        input[type=range] { width:100%; min-width:0; max-width:130px; accent-color:#fff; }

        /* Shot strip: proof that what you took is going somewhere. */
        .queue { display:flex; gap:6px; overflow-x:auto; padding:0 2px 10px; scrollbar-width:none; }
        .queue::-webkit-scrollbar { display:none; }
        .shot { position:relative; flex:0 0 auto; width:46px; height:46px; border-radius:8px;
                overflow:hidden; background:#222; }
        .shot img { width:100%; height:100%; object-fit:cover; display:block; }
        .mark { position:absolute; inset:auto 3px 3px auto; width:14px; height:14px; border-radius:999px;
                background:rgba(0,0,0,.6); }
        .shot[data-state="sending"] .mark { background:#f0c04a; animation:pulse 1s infinite; }
        .shot[data-state="retry"]   .mark { background:#ff8f8f; }
        .shot[data-state="done"]    .mark { background:#5fbf78; }
        .shot[data-state="rejected"] .mark { background:#888; }
        .shot[data-state="rejected"] { opacity:.4; filter:grayscale(1); }
        .shot[data-state="done"]    { opacity:.55; }
        @keyframes pulse { 50% { opacity:.35; } }

        .badge { min-width:22px; height:22px; padding:0 6px; border-radius:999px; background:#fff; color:#111;
                 font-size:.72rem; font-weight:700; display:grid; place-items:center; }

        /* Denied / unsupported. The camera is the whole page, so this replaces it rather than warning. */
        .gate { position:absolute; inset:0; display:none; flex-direction:column; align-items:center;
                justify-content:center; gap:14px; text-align:center; padding:32px 24px; background:#0d0b0e; }
        body[data-state="denied"] .gate { display:flex; }
        body[data-state="denied"] .bottom, body[data-state="denied"] video { display:none; }

        /* Between load and the guest answering the permission prompt there is nothing to show —
           without this it is a black rectangle under a shutter that does nothing yet. */
        .starting { position:absolute; inset:0; display:none; flex-direction:column; align-items:center;
                    justify-content:center; gap:12px; color:#b9adbf; pointer-events:none; }
        body:not([data-state]) .starting { display:flex; }
        body:not([data-state]) .bottom { opacity:.35; pointer-events:none; }
        .spin { width:26px; height:26px; border-radius:999px; border:2px solid rgba(255,255,255,.25);
                border-top-color:#fff; animation:spin .9s linear infinite; }
        @keyframes spin { to { transform:rotate(360deg); } }
        .gate p { margin:0; color:#b9adbf; max-width:34ch; }
        @media (prefers-reduced-motion: reduce) { * { animation:none !important; } }
        """;

    /// <param name="uploadPath">Where a captured frame is POSTed. One photo per request.</param>
    /// <param name="galleryPath">Everything the event has collected so far.</param>
    /// <param name="nonce">Per-response value tying the inline script to this document's CSP.</param>
    public static string Render(
        string uploadPath, string galleryPath, string eventTitle, GuestPalette palette, string nonce) => $$"""
        <!doctype html>
        <html lang="en"><head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1, viewport-fit=cover, maximum-scale=1">
        <meta name="robots" content="noindex, nofollow">
        <meta name="theme-color" content="#000000">
        <title>Camera — {{E(eventTitle)}}</title>
        <style>{{palette.Root}}
        {{Css}}</style>
        </head>
        <body>
        <div class="stage">
          <video id="cam" playsinline autoplay muted></video>
          <div class="reticle" id="reticle" hidden><i></i><i></i><i></i><i></i></div>
          <div class="flash" id="flashfx"></div>

          <div class="top">
            <a class="btn" href="{{E(galleryPath)}}">Gallery</a>
            <span class="title">{{E(eventTitle)}}</span>
            <span class="badge" id="pending" hidden></span>
          </div>

          <div class="starting">
            <span class="spin" aria-hidden="true"></span>
            <p style="margin:0;">Starting the camera…</p>
          </div>

          <div class="gate">
            <h1 style="margin:0;font:600 1.3rem/1.3 Georgia,serif;">The camera isn't available</h1>
            <p id="why"></p>
            <p>You can still see everything the night has collected — and add photos straight from
               this phone's library there instead.</p>
            <a class="btn" href="{{E(galleryPath)}}">See the photos</a>
          </div>

          <div class="bottom">
            <div class="queue" id="queue"></div>
            <div class="filters" id="filters"></div>
            <div class="row">
              <div class="side">
                <button class="btn" id="torch" type="button" hidden>Flash</button>
                <input type="range" id="zoom" hidden aria-label="Zoom">
              </div>
              <button class="shoot" id="shoot" type="button" aria-label="Take a photo"></button>
              <div class="side right">
                <button class="btn" id="night" type="button" hidden aria-pressed="false">Night</button>
                <button class="btn" id="flip" type="button" hidden>Flip</button>
              </div>
            </div>
          </div>
        </div>

        <script nonce="{{nonce}}">
        window.__ibCamera = { upload: "{{E(uploadPath)}}" };
        {{Script}}
        </script>
        </body></html>
        """;
}
