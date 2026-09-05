using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.TemplateCompiler;
using Microsoft.Extensions.Configuration;

namespace InvitesBlog.Api.Rendering;

/// <summary>
/// Turns a resolved payload plus a pinned template package into the one document a guest is sent.
/// The last step of the guest path: everything before it decides WHAT to show, this decides what the
/// bytes look like.
/// </summary>
public sealed class RenderedInvitations(IStorageService storage, IConfiguration config)
{
    /// <summary>
    /// Fetches the package the campaign pinned and binds the payload into it. Returns null when the
    /// package has gone from storage — a campaign can outlive a template version, and a blank page is
    /// a better failure than a half-rendered one.
    /// </summary>
    public async Task<string?> BuildAsync(string packageUrl, JsonObject data, CancellationToken ct)
    {
        var key = StorageKey(packageUrl);
        if (key is null) return null;

        var bytes = await storage.GetAsync(key, ct);
        if (bytes is null || bytes.Length == 0) return null;

        var html = ServerBinder.Bind(Encoding.UTF8.GetString(bytes), data);
        return WithPhotoBox(WithRsvp(html, data), data);
    }

    /// <summary>
    /// Gives an invitation a way to REPLY even when its markup has none of its own.
    ///
    /// <para>The same floor as the photo box below, and it exists for a sharper reason: a design a
    /// customer brought themselves has no <c>[data-href="rsvp.link"]</c> and never will — it is a
    /// picture. Without this, importing your own artwork would silently cost your guests the ability
    /// to answer, which is most of what the platform is for.</para>
    ///
    /// <para>Absent for a guest who has already said they are coming — <c>rsvp.link</c> is null in
    /// that case, exactly as it is for a template's own button — so nobody is asked twice.</para>
    /// </summary>
    private static string WithRsvp(string html, JsonObject data)
    {
        var link = data["rsvp"]?["link"]?.ToString();
        if (string.IsNullOrEmpty(link)) return html;

        // A template that placed the binding itself keeps its own styling and this never fires.
        if (html.Contains("data-href=\"rsvp.link\"", StringComparison.Ordinal)) return html;

        var label = data["rsvp"]?["label"]?.ToString() is { Length: > 0 } l ? l : "Reply now";

        // Same stacking claim as the photo bar: templates build scenery out of full-screen fixed
        // layers, and an unpositioned block paints underneath every one of them.
        var bar = $"""
            <section style="position:relative;z-index:2147483000;
               padding:56px 20px 8px;text-align:center;
               background:var(--ib-bg,#17131a);color:var(--ib-text,#f4eef6)">
              <a href="{WebUtility.HtmlEncode(link)}" style="display:inline-flex;align-items:center;
                 padding:15px 30px;border-radius:999px;text-decoration:none;
                 font:600 15px/1.2 ui-sans-serif,system-ui,-apple-system,Segoe UI,Roboto,sans-serif;
                 letter-spacing:.02em;color:var(--ib-bg,#17131a);
                 background:var(--ib-accent,#c9a227)">{WebUtility.HtmlEncode(label)}</a>
            </section>
            """;

        var close = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return close < 0 ? html + bar : html[..close] + bar + html[close..];
    }

    /// <summary>
    /// Gives an invitation a way into its photo box even when its template has no
    /// <c>[data-href="photos.link"]</c> of its own.
    ///
    /// <para><b>Why this exists at all.</b> A campaign PINS its template package, so every invitation
    /// already sent renders from the markup as it was the day it was booked. Adding the element to the
    /// templates reaches new campaigns and no existing one — a wedding invited last month would show
    /// its guests no photo box, forever. This is that floor: a plain link, appended once, in the
    /// place a template would have put one.</para>
    ///
    /// <para>A link, specifically, and never a form or a fetch — the rendered invitation is served
    /// under a CSP with <c>default-src 'none'</c> and <c>form-action 'none'</c>, so navigation is the
    /// only thing it can do. Designers who place the element themselves get their own styling and
    /// this never fires.</para>
    /// </summary>
    private static string WithPhotoBox(string html, JsonObject data)
    {
        // Prefers the camera: a guest standing at the party has not "captured" anything yet, and the
        // thing they want is a viewfinder, not a file picker.
        //
        // A camera object with no link means the camera is CLOSED for this guest — they are not
        // coming, or it is not the night — and no bar belongs here at all. Only a payload with no
        // camera object predates the camera, and that one still gets the gallery.
        var link = data["camera"] is JsonObject camera
            ? camera["link"]?.ToString()
            : data["photos"]?["link"]?.ToString();
        if (string.IsNullOrEmpty(link)) return html;

        // Safe as a plain string check because the markup has just been through AngleSharp, which
        // normalises every attribute to double quotes — this is not run against author markup.
        // Either binding counts as the designer having placed it: an older template links the gallery,
        // a current one links the camera, and both mean this floor is not needed.
        if (html.Contains("data-href=\"photos.link\"", StringComparison.Ordinal)
            || html.Contains("data-href=\"camera.link\"", StringComparison.Ordinal)) return html;

        // Colours come from the template's OWN custom properties, with the old fixed pair as the
        // fallback. This bar is appended inside the invitation, so `var()` resolves against whatever
        // the template actually declared — a pale invitation stops ending in a black slab, and no
        // server-side guess about the palette is needed.
        // A section with a pill inside it, not a slab across the foot of the page. This is the only
        // camera a template without its own binding will ever show — most uploaded ones — so it has
        // to read as part of the invitation rather than as something appended to it. Colours and
        // type both come from the template's OWN custom properties, so it inherits whatever the
        // designer chose; the fixed pair is only the floor for a template that declared nothing.
        //
        // The stacking is load-bearing, not decoration. Templates build their scenery out of
        // full-screen position:fixed layers — red-curtain-2 has four, the topmost a velvet curtain
        // at z-index 9 — and an unpositioned block paints UNDERNEATH every one of them. So this
        // rendered perfectly and was invisible: present in the markup, covered by the curtain. It is
        // appended from outside the design and cannot know what it is landing on top of, so it
        // claims a z-index no template would reasonably reach for.
        var bar = $"""
            <section style="position:relative;z-index:2147483000;
               padding:56px 20px calc(48px + env(safe-area-inset-bottom));text-align:center;
               background:var(--ib-bg,#17131a);color:var(--ib-text,#f4eef6);
               border-top:1px solid color-mix(in srgb, currentColor 14%, transparent)">
              <a href="{WebUtility.HtmlEncode(link)}" style="display:inline-flex;align-items:center;
                 padding:15px 30px;border-radius:999px;text-decoration:none;
                 font:600 15px/1.2 ui-sans-serif,system-ui,-apple-system,Segoe UI,Roboto,sans-serif;
                 letter-spacing:.02em;color:var(--ib-bg,#17131a);
                 background:var(--ib-accent,#c9a227)"><svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true" focusable="false" style="width:1.05em;height:1.05em;vertical-align:-.16em;margin-right:.55em"><path d="M3 8.6A2.4 2.4 0 0 1 5.4 6.2h1.7a1 1 0 0 0 .83-.45l.9-1.36a1 1 0 0 1 .84-.45h4.66a1 1 0 0 1 .84.45l.9 1.36a1 1 0 0 0 .83.45h1.7A2.4 2.4 0 0 1 21 8.6v8.2a2.4 2.4 0 0 1-2.4 2.4H5.4A2.4 2.4 0 0 1 3 16.8z"/><circle cx="12" cy="12.6" r="3.5"/></svg>Capture moments</a>
              <p style="margin:16px 0 0;font:400 12px/1.5 ui-sans-serif,system-ui,-apple-system,Segoe UI,Roboto,sans-serif;
                 letter-spacing:.14em;text-transform:uppercase;
                 color:color-mix(in srgb, currentColor 62%, transparent)">Everything everyone shoots, in one place</p>
            </section>
            """;

        var close = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return close < 0 ? html + bar : html[..close] + bar + html[close..];
    }

    /// <summary>
    /// Maps a stored package URL back to the storage key holding its <c>index.html</c>. Stored URLs
    /// are relative in production (<c>/assets/templates/x@1.0.0/</c>) but absolute in local dev, so
    /// both shapes have to resolve — this is exactly the property that makes swapping the storage
    /// backend a config change rather than a data migration, and it is worth not breaking.
    /// </summary>
    private string? StorageKey(string packageUrl)
    {
        if (string.IsNullOrWhiteSpace(packageUrl)) return null;

        var path = PathOf(packageUrl);
        // AssetsBase is a path in production (/assets) but absolute in local dev
        // (http://localhost:8080/assets), so reduce both to a path before comparing.
        var prefix = PathOf(config["Urls:AssetsBase"] ?? "/assets").TrimEnd('/');

        var key = prefix.Length > 0 && path.StartsWith(prefix + "/", StringComparison.Ordinal)
            ? path[(prefix.Length + 1)..]
            : path.TrimStart('/');

        return string.IsNullOrWhiteSpace(key) ? null : key.TrimEnd('/') + "/index.html";
    }

    /// <summary>The path part of a value that may be absolute or already just a path.</summary>
    private static string PathOf(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var absolute) ? absolute.AbsolutePath : value;
}
