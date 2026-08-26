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

    private const string Css = """
        :root { color-scheme: dark; --bg:#17131a; --card:#211b25; --ink:#f4eef6; --muted:#b9adbf;
                --accent:#c9a227; --line:#372e3d; --bad:#ff8f8f; }
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
                text-align:center; background:#171320; color:var(--ink);
                border:1px solid var(--line); border-radius:10px; }
        input.text { font-size:1rem; letter-spacing:normal; text-align:left; }
        button { width:100%; margin-top:1.2rem; padding:.9rem 1rem; font-size:1rem; font-weight:600;
                 color:#241d06; background:var(--accent); border:0; border-radius:10px; cursor:pointer; }
        button.ghost { background:transparent; color:var(--muted); border:1px solid var(--line); }
        .err { color:var(--bad); font-size:.9rem; margin:.8rem 0 0; }
        .foot { margin-top:1.6rem; font-size:.8rem; color:var(--muted); }
        a { color:var(--accent); }
        """;

    private static string Shell(string title, string body) => $"""
        <!doctype html>
        <html lang="en"><head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <meta name="robots" content="noindex, nofollow">
        <title>{E(title)}</title>
        <style>{Css}</style>
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
    public static string Rsvp(string action, string guestName, string eventTitle, string? error)
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
        return Shell("RSVP", body);
    }

    public static string RsvpDone(string status, string renderId) => Shell("Thank you", $"""
        <h1>{(status == "Going" ? "Wonderful — see you there" : "Thank you for letting us know")}</h1>
        <p>Your reply has been sent to the host.</p>
        <p><a href="/r/{E(renderId)}">Back to the invitation</a></p>
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

    public static string Unavailable() => Shell("Invitation unavailable", """
        <h1>We couldn't open this invitation</h1>
        <p>Something went wrong preparing it. Please try again in a moment.</p>
        """);
}
