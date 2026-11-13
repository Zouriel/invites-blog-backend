using System.Globalization;
using System.Net;
using System.Text;
using InvitesBlog.Application.Services.Portfolios;

namespace InvitesBlog.Api.Rendering;

/// <summary>
/// A Studio's public portfolio at <c>invites.blog/@handle</c>, rendered on the server: link previews and search see the
/// real page, and a phone gets one small HTML document (inline CSS, a few lines of script) with lazy pictures. The
/// same page loads in the app's embedded browser. Everything interpolated goes through <see cref="E"/>.
/// </summary>
public static class PortfolioPage
{
    private static string E(string? text) => WebUtility.HtmlEncode(text ?? string.Empty);

    private static string Digits(string? phone) => new((phone ?? "").Where(char.IsDigit).ToArray());

    /// <param name="preview">The studio's own preview: not indexed, and a banner says it may not be published.</param>
    public static string Render(PublicPortfolioDto p, string nonce, string siteBase, bool preview)
    {
        var theme = PortfolioChoices.Theme(p.Theme);
        var font = PortfolioChoices.Font(p.Font);
        var accent = PortfolioChoices.AccentColour(p.Theme, p.Accent);
        var onAccent = ReadableOn(accent);
        var line = Mix(theme.Bg, theme.Text, theme.Dark ? 0.16 : 0.14);
        var description = p.Tagline ?? $"Photos and work by {p.DisplayName}.";
        var title = $"{p.DisplayName} (@{p.Handle})";
        // Stored pictures can be site-relative (/assets/...); link previews and search need full addresses.
        string? Abs(string? url) => url is null ? null : url.StartsWith('/') && !url.StartsWith("//") ? siteBase + url : url;

        var sb = new StringBuilder(32_000);
        // email_off: Cloudflare's Email Obfuscation would rewrite the Email button into a link that needs a script our
        // CSP (rightly) refuses, leaving "[email protected]". The page shows the address on purpose.
        sb.Append("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><!--email_off-->");
        sb.Append("<meta name=\"viewport\" content=\"width=device-width, initial-scale=1, viewport-fit=cover\">");
        sb.Append($"<title>{E(title)}</title>");
        sb.Append($"<meta name=\"description\" content=\"{E(description)}\">");
        sb.Append(preview ? "<meta name=\"robots\" content=\"noindex, nofollow\">" : $"<link rel=\"canonical\" href=\"{E(p.Url)}\">");
        sb.Append($"<meta name=\"theme-color\" content=\"{theme.Bg}\">");
        sb.Append("<meta property=\"og:type\" content=\"profile\">");
        sb.Append($"<meta property=\"og:site_name\" content=\"invites.blog\"><meta property=\"og:title\" content=\"{E(p.DisplayName)}\">");
        sb.Append($"<meta property=\"og:description\" content=\"{E(description)}\"><meta property=\"og:url\" content=\"{E(p.Url)}\">");
        var ogImage = Abs(p.CoverUrl ?? p.LogoUrl);
        if (ogImage is not null)
        {
            sb.Append($"<meta property=\"og:image\" content=\"{E(ogImage)}\"><meta name=\"twitter:image\" content=\"{E(ogImage)}\">");
            sb.Append($"<meta property=\"og:image:alt\" content=\"{E(p.DisplayName)}\">");
        }
        sb.Append($"<meta name=\"twitter:card\" content=\"{(p.CoverUrl is null ? "summary" : "summary_large_image")}\">");
        sb.Append($"<meta name=\"twitter:title\" content=\"{E(p.DisplayName)}\"><meta name=\"twitter:description\" content=\"{E(description)}\">");
        sb.Append($"<link rel=\"icon\" href=\"{E(p.LogoUrl ?? siteBase + "/icon.svg?v=3")}\">");
        if (font.Key != "simple")
        {
            sb.Append("<link rel=\"preconnect\" href=\"https://fonts.googleapis.com\"><link rel=\"preconnect\" href=\"https://fonts.gstatic.com\" crossorigin>");
            sb.Append($"<link rel=\"stylesheet\" href=\"{E(FontsUrl(font))}\">");
        }
        if (p.CoverUrl is not null) sb.Append($"<link rel=\"preload\" as=\"image\" href=\"{E(p.CoverUrl)}\">");
        sb.Append(JsonLd(p, nonce, Abs));
        sb.Append("<style>");
        sb.Append($":root{{color-scheme:{(theme.Dark ? "dark" : "light")};--bg:{theme.Bg};--surface:{theme.Surface};--text:{theme.Text};--muted:{theme.Muted};");
        sb.Append($"--accent:{accent};--on-accent:{onAccent};--line:{line};--head:{FontStack(font.Heading, serif: font.Heading != font.Body)};--body:{FontStack(font.Body, serif: false)};--head-weight:{(SingleWeight(font.Heading) ? 400 : 600)};}}");
        sb.Append(Css);
        sb.Append("</style></head><body>");
        if (preview)
            sb.Append("<div class=\"preview\">Preview. Only you can see this until you publish.</div>");

        var photoIndex = 0;
        var allPhotos = new List<PortfolioPhotoDto>();
        foreach (var s in p.Sections)
        {
            switch (s.Kind)
            {
                case PortfolioSectionKinds.Cover: Cover(sb, p, s); break;
                case PortfolioSectionKinds.Collection when s.Collection is { } c:
                    Collection(sb, c, ref photoIndex, allPhotos); break;
                case PortfolioSectionKinds.About: About(sb, s); break;
                case PortfolioSectionKinds.Designs: Designs(sb, s); break;
                case PortfolioSectionKinds.Packages: Packages(sb, s); break;
                case PortfolioSectionKinds.Reviews: Reviews(sb, s); break;
                case PortfolioSectionKinds.Contact: Contact(sb, p, s); break;
            }
        }

        sb.Append("<footer class=\"foot\"><button type=\"button\" class=\"linkish\" data-report>")
          .Append(PortfolioIcons.Flag).Append("Ask to remove a photo</button>")
          .Append($"<a class=\"made\" href=\"{E(siteBase)}/?ref=portfolio\">Made with invites.blog</a></footer>");

        // The photo viewer and the remove-request dialog (filled by the script).
        sb.Append("<div class=\"lb\" id=\"lb\" hidden role=\"dialog\" aria-modal=\"true\" aria-label=\"Photo\">")
          .Append($"<button type=\"button\" class=\"lb-btn lb-close\" aria-label=\"Close\">{PortfolioIcons.Close}</button>")
          .Append($"<button type=\"button\" class=\"lb-btn lb-prev\" aria-label=\"Previous photo\">{PortfolioIcons.Prev}</button>")
          .Append("<img class=\"lb-img\" alt=\"\">")
          .Append($"<button type=\"button\" class=\"lb-btn lb-next\" aria-label=\"Next photo\">{PortfolioIcons.Next}</button>")
          .Append($"<button type=\"button\" class=\"lb-report linkish\" data-report-photo>{PortfolioIcons.Flag}Ask to remove this photo</button></div>");
        sb.Append(ReportDialog());

        sb.Append($"<script nonce=\"{nonce}\">window.__pf={{handle:{JsonString(p.Handle)},photos:[");
        sb.Append(string.Join(',', allPhotos.Select(x => $"{{id:{JsonString(x.Id.ToString())},src:{JsonString(x.Url)},t:{JsonString(x.ThumbUrl)}}}")));
        sb.Append("]};").Append(Script).Append("</script><!--/email_off--></body></html>");
        return sb.ToString();
    }

    public static string Unavailable(string siteBase) => $$"""
        <!doctype html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1">
        <title>This portfolio isn't available</title><meta name="robots" content="noindex">
        <style>
        :root{color-scheme:light dark;--bg:#F2EADB;--text:#1C1C1F;--muted:#55504A;--accent:#7A0C1E}
        @media (prefers-color-scheme:dark){:root{--bg:#1C1C1F;--text:#F2EADB;--muted:#BDB4A6;--accent:#F58A99} }
        body{margin:0;min-height:100vh;display:grid;place-items:center;background:var(--bg);color:var(--text);
        font:16px/1.5 system-ui,-apple-system,Segoe UI,Roboto,sans-serif;padding:24px;text-align:center}
        h1{font:600 1.6rem/1.3 Georgia,serif;margin:0 0 .5rem}p{color:var(--muted);margin:0 0 1.5rem}
        a{color:var(--accent);font-weight:600}
        </style></head><body><main><h1>This portfolio isn't available</h1>
        <p>The link may have changed, or the studio has taken the page down.</p>
        <a href="{{E(siteBase)}}/">Go to invites.blog</a></main></body></html>
        """;

    // ---------- sections ----------

    private static void Cover(StringBuilder sb, PublicPortfolioDto p, PublicPortfolioSectionDto s)
    {
        var hasContact = p.Sections.Any(x => x.Kind == PortfolioSectionKinds.Contact);
        var firstCollection = p.Sections.FirstOrDefault(x => x.Collection is not null)?.Collection;
        sb.Append(s.PhotoUrl is null ? "<header class=\"hero plain\">" : "<header class=\"hero\">");
        if (s.PhotoUrl is not null)
            sb.Append($"<img class=\"hero-img\" src=\"{E(s.PhotoUrl)}\" alt=\"\" fetchpriority=\"high\" decoding=\"async\">");
        sb.Append("<div class=\"hero-in\">");
        if (p.LogoUrl is not null) sb.Append($"<img class=\"logo\" src=\"{E(p.LogoUrl)}\" alt=\"{E(p.DisplayName)} logo\" width=\"88\" height=\"88\">");
        sb.Append($"<h1>{E(p.DisplayName)}</h1>");
        if (p.Tagline is not null) sb.Append($"<p class=\"tag\">{E(p.Tagline)}</p>");
        sb.Append("<div class=\"hero-cta\">");
        if (firstCollection is not null) sb.Append($"<a class=\"btn ghost\" href=\"#c-{firstCollection.Id:N}\">See the work</a>");
        if (hasContact) sb.Append("<a class=\"btn\" href=\"#contact\">Get in touch</a>");
        sb.Append("</div></div></header>");
    }

    private static void Collection(StringBuilder sb, PublicCollectionDto c, ref int index, List<PortfolioPhotoDto> all)
    {
        sb.Append($"<section class=\"sec\" id=\"c-{c.Id:N}\"><h2>{E(c.Title)}</h2>");
        var layout = c.Layout is "masonry" or "slideshow" ? c.Layout : "grid";
        if (layout == "slideshow")
        {
            sb.Append("<div class=\"show\" data-show><div class=\"show-track\">");
            foreach (var ph in c.Photos)
            {
                sb.Append($"<button type=\"button\" class=\"slide\" data-i=\"{index++}\" aria-label=\"Open photo\">")
                  .Append($"<img src=\"{E(ph.Url)}\" alt=\"\" loading=\"lazy\" decoding=\"async\" width=\"{ph.Width}\" height=\"{ph.Height}\"></button>");
                all.Add(ph);
            }
            sb.Append("</div>");
            if (c.Photos.Count > 1)
                sb.Append($"<button type=\"button\" class=\"show-btn prev\" aria-label=\"Previous\">{PortfolioIcons.Prev}</button>")
                  .Append($"<button type=\"button\" class=\"show-btn next\" aria-label=\"Next\">{PortfolioIcons.Next}</button>");
            sb.Append("</div>");
        }
        else
        {
            sb.Append($"<div class=\"{layout}\">");
            foreach (var ph in c.Photos)
            {
                sb.Append($"<button type=\"button\" class=\"tile\" data-i=\"{index++}\" aria-label=\"Open photo\">")
                  .Append($"<img src=\"{E(ph.ThumbUrl)}\" alt=\"\" loading=\"lazy\" decoding=\"async\" width=\"{ph.Width}\" height=\"{ph.Height}\"></button>");
                all.Add(ph);
            }
            sb.Append("</div>");
        }
        sb.Append("</section>");
    }

    private static void About(StringBuilder sb, PublicPortfolioSectionDto s)
    {
        sb.Append($"<section class=\"sec about{(s.PhotoUrl is null ? " solo" : "")}\" id=\"about\">");
        if (s.PhotoUrl is not null) sb.Append($"<img class=\"about-img\" src=\"{E(s.PhotoUrl)}\" alt=\"\" loading=\"lazy\" decoding=\"async\">");
        sb.Append($"<div><h2>{E(s.Title)}</h2>");
        foreach (var para in (s.Body ?? "").Split("\n\n", StringSplitOptions.RemoveEmptyEntries))
            sb.Append($"<p>{E(para).Replace("\n", "<br>")}</p>");
        sb.Append("</div></section>");
    }

    private static void Designs(StringBuilder sb, PublicPortfolioSectionDto s)
    {
        sb.Append($"<section class=\"sec\" id=\"designs\"><h2>{E(s.Title)}</h2><div class=\"designs\">");
        foreach (var d in s.Designs ?? [])
        {
            sb.Append("<figure class=\"design\">");
            if (d.PreviewUrl is not null) sb.Append($"<img src=\"{E(d.PreviewUrl)}\" alt=\"\" loading=\"lazy\" decoding=\"async\">");
            sb.Append($"<figcaption>{E(d.Name)}</figcaption></figure>");
        }
        sb.Append("</div></section>");
    }

    private static void Packages(StringBuilder sb, PublicPortfolioSectionDto s)
    {
        sb.Append($"<section class=\"sec\" id=\"packages\"><h2>{E(s.Title)}</h2><div class=\"cards\">");
        foreach (var pk in s.Packages ?? [])
        {
            sb.Append($"<article class=\"card pack\"><h3>{E(pk.Name)}</h3>");
            if (pk.Price is not null) sb.Append($"<p class=\"price\">{E(pk.Price)}</p>");
            if (pk.Lines is { Count: > 0 })
            {
                sb.Append("<ul>");
                foreach (var l in pk.Lines) sb.Append($"<li>{E(l)}</li>");
                sb.Append("</ul>");
            }
            sb.Append("</article>");
        }
        sb.Append("</div></section>");
    }

    private static void Reviews(StringBuilder sb, PublicPortfolioSectionDto s)
    {
        sb.Append($"<section class=\"sec\" id=\"reviews\"><h2>{E(s.Title)}</h2><div class=\"cards\">");
        foreach (var r in s.Reviews ?? [])
        {
            sb.Append($"<figure class=\"card quote\"><blockquote>{E(r.Quote).Replace("\n", "<br>")}</blockquote>");
            if (r.Name is not null) sb.Append($"<figcaption>{E(r.Name)}</figcaption>");
            sb.Append("</figure>");
        }
        sb.Append("</div></section>");
    }

    private static void Contact(StringBuilder sb, PublicPortfolioDto p, PublicPortfolioSectionDto s)
    {
        var c = p.Contact;
        sb.Append($"<section class=\"sec contact\" id=\"contact\"><h2>{E(s.Title)}</h2><div class=\"buttons\">");
        if (Digits(c.WhatsApp) is { Length: > 0 } wa)
            sb.Append($"<a class=\"cbtn\" style=\"background:{PortfolioIcons.WhatsAppFill};color:{PortfolioIcons.WhatsAppInk}\" href=\"https://wa.me/{wa}\" target=\"_blank\" rel=\"noopener\">{PortfolioIcons.WhatsApp}WhatsApp</a>");
        if (Digits(c.Viber) is { Length: > 0 } vb)
            sb.Append($"<a class=\"cbtn\" style=\"background:{PortfolioIcons.ViberFill};color:#fff\" href=\"viber://chat?number=%2B{vb}\">{PortfolioIcons.Viber}Viber</a>");
        if (c.Instagram is { Length: > 0 } ig)
            sb.Append($"<a class=\"cbtn line\" href=\"https://instagram.com/{E(Uri.EscapeDataString(ig))}\" target=\"_blank\" rel=\"noopener\">{PortfolioIcons.Instagram}Instagram</a>");
        if (c.Phone is { Length: > 0 } ph)
            sb.Append($"<a class=\"cbtn line\" href=\"tel:{E(ph)}\">{PortfolioIcons.Phone}Call</a>");
        if (c.Email is { Length: > 0 } em)
            sb.Append($"<a class=\"cbtn line\" href=\"mailto:{E(em)}\">{PortfolioIcons.Mail}Email</a>");
        sb.Append("</div>");
        if (c.Enquiries)
        {
            sb.Append("<form class=\"enq card\" id=\"enq\" novalidate><h3>Send a message</h3>")
              .Append("<label>Your name<input name=\"name\" autocomplete=\"name\" maxlength=\"80\" required></label>")
              .Append("<label>Phone or email<input name=\"contact\" autocomplete=\"email\" maxlength=\"120\" required></label>")
              .Append($"<label>Event date <span class=\"opt\">(if you know it)</span><input name=\"eventDate\" type=\"date\"></label>")
              .Append("<label>Message<textarea name=\"message\" rows=\"4\" maxlength=\"2000\" required></textarea></label>")
              .Append("<label class=\"hp\" aria-hidden=\"true\">Website<input name=\"website\" tabindex=\"-1\" autocomplete=\"off\"></label>")
              .Append($"<button class=\"btn wide\" type=\"submit\">{PortfolioIcons.Send}Send</button>")
              .Append("<p class=\"note\" role=\"status\" aria-live=\"polite\"></p></form>");
        }
        sb.Append("</section>");
    }

    private static string ReportDialog() => $"""
        <div class="lb dlg" id="rep" hidden role="dialog" aria-modal="true" aria-labelledby="rep-h">
        <form class="card rep" novalidate>
        <button type="button" class="lb-btn x" aria-label="Close" data-close>{PortfolioIcons.Close}</button>
        <h3 id="rep-h">Ask to remove a photo</h3>
        <div class="rep-body">
        <p class="muted rep-which">Tell us which photo, and we'll pass it to the studio. No account needed.</p>
        <img class="rep-thumb" alt="" hidden>
        <div class="rep-pick" role="group" aria-label="Which photo?" hidden></div>
        <fieldset><legend>Why?</legend>
        <label class="radio"><input type="radio" name="reason" value="me" checked> I'm in it and want it removed</label>
        <label class="radio"><input type="radio" name="reason" value="not_agreed"> We didn't agree to it being shown</label>
        <label class="radio"><input type="radio" name="reason" value="other"> Something else</label></fieldset>
        <label>More details <span class="opt">(optional)</span><textarea name="note" rows="3" maxlength="800"></textarea></label>
        <label>Your phone or email <span class="opt">(optional, so we can reply)</span><input name="contact" maxlength="120"></label>
        <button class="btn wide rep-send" type="submit">Send request</button>
        </div>
        <p class="note" role="status" aria-live="polite"></p>
        <button type="button" class="btn wide rep-done" hidden data-close>Close</button>
        </form></div>
        """;

    // ---------- look ----------

    private const string Css = """
        *{box-sizing:border-box}html{scroll-behavior:smooth;-webkit-text-size-adjust:100%}
        body{margin:0;background:var(--bg);color:var(--text);font:16px/1.6 var(--body);-webkit-font-smoothing:antialiased}
        img{display:block;max-width:100%}
        h1,h2,h3{font-family:var(--head);font-weight:var(--head-weight);line-height:1.15;margin:0;letter-spacing:-.01em}
        h2{font-size:clamp(1.6rem,4.5vw,2.4rem);margin-bottom:24px}h3{font-size:1.2rem}
        a{color:inherit}
        .preview{position:sticky;top:0;z-index:20;background:var(--accent);color:var(--on-accent);text-align:center;font-size:.9rem;padding:8px 16px;font-weight:600}
        .hero{position:relative;min-height:min(78vh,760px);display:flex;align-items:flex-end;overflow:hidden;color:#fff;background:#111}
        .hero-img{position:absolute;inset:0;width:100%;height:100%;object-fit:cover}
        .hero::after{content:"";position:absolute;inset:0;background:linear-gradient(180deg,rgba(0,0,0,0) 35%,rgba(0,0,0,.62) 100%)}
        .hero.plain{min-height:auto;background:var(--surface);color:var(--text);border-bottom:1px solid var(--line)}
        .hero.plain::after{display:none}
        .hero-in{position:relative;z-index:1;width:100%;max-width:1120px;margin:0 auto;padding:clamp(48px,10vw,96px) 20px 40px}
        .logo{width:88px;height:88px;border-radius:50%;object-fit:cover;background:var(--surface);border:3px solid rgba(255,255,255,.85);margin-bottom:16px}
        .hero h1{font-size:clamp(2.2rem,8vw,4.4rem)}
        .tag{font-size:clamp(1rem,2.6vw,1.25rem);margin:12px 0 0;max-width:40ch;opacity:.92}
        .hero-cta{display:flex;flex-wrap:wrap;gap:12px;margin-top:24px}
        .btn{display:inline-flex;align-items:center;justify-content:center;gap:8px;min-height:48px;padding:0 24px;border-radius:999px;border:0;
          background:var(--accent);color:var(--on-accent);font:600 1rem var(--body);text-decoration:none;cursor:pointer}
        .btn.ghost{background:rgba(255,255,255,.14);color:#fff;border:1px solid rgba(255,255,255,.6);backdrop-filter:blur(6px)}
        .hero.plain .btn.ghost{background:transparent;color:var(--text);border-color:var(--line)}
        .btn.wide{width:100%}.btn:focus-visible,.cbtn:focus-visible,.tile:focus-visible,.slide:focus-visible{outline:3px solid var(--accent);outline-offset:3px}
        .sec{max-width:1120px;margin:0 auto;padding:clamp(48px,8vw,88px) 20px 0}
        .grid{display:grid;grid-template-columns:repeat(auto-fill,minmax(min(46%,260px),1fr));gap:8px}
        .grid .tile img{aspect-ratio:1;width:100%;height:100%;object-fit:cover}
        .masonry{columns:2 160px;column-gap:8px}@media(min-width:900px){.masonry{columns:3}}
        .masonry .tile{margin-bottom:8px;break-inside:avoid;width:100%}.masonry .tile img{width:100%;height:auto}
        .tile,.slide{display:block;padding:0;border:0;background:var(--surface);cursor:zoom-in;border-radius:6px;overflow:hidden;width:100%}
        .tile img{transition:transform .5s cubic-bezier(.2,.7,.2,1)}@media(hover:hover){.tile:hover img{transform:scale(1.03)}}
        .show{position:relative}.show-track{display:flex;overflow-x:auto;scroll-snap-type:x mandatory;gap:8px;scrollbar-width:none;border-radius:8px}
        .show-track::-webkit-scrollbar{display:none}
        .slide{flex:0 0 100%;scroll-snap-align:center;border-radius:8px;background:#0c0c0d}.slide img{width:100%;height:min(72vh,640px);object-fit:contain}
        .show-btn{position:absolute;top:50%;transform:translateY(-50%);width:48px;height:48px;border-radius:50%;border:0;display:grid;place-items:center;
          background:rgba(0,0,0,.45);color:#fff;cursor:pointer}.show-btn.prev{left:12px}.show-btn.next{right:12px}
        .about{display:grid;gap:32px;align-items:center}@media(min-width:800px){.about{grid-template-columns:5fr 6fr}.about.solo{grid-template-columns:1fr}}
        .about.solo>div{max-width:68ch}
        .about-img{width:100%;aspect-ratio:4/5;object-fit:cover;border-radius:8px}.about p{color:var(--muted);font-size:1.08rem;margin:0 0 16px}
        .designs{display:grid;grid-template-columns:repeat(auto-fill,minmax(min(46%,200px),1fr));gap:16px}
        .design{margin:0}.design img{width:100%;aspect-ratio:9/16;object-fit:cover;border-radius:8px;border:1px solid var(--line)}
        .design figcaption{margin-top:8px;font-size:.95rem;color:var(--muted)}
        .cards{display:grid;grid-template-columns:repeat(auto-fill,minmax(min(100%,280px),1fr));gap:16px}
        .card{background:var(--surface);border:1px solid var(--line);border-radius:16px;padding:24px}
        .price{font:600 1.5rem var(--head);color:var(--accent);margin:8px 0 12px}
        .pack ul{margin:0;padding-left:20px;color:var(--muted)}.pack li{margin:4px 0}
        .quote{margin:0}.quote blockquote{margin:0;font:400 1.15rem/1.55 var(--head)}.quote blockquote::before{content:"\201C"}.quote blockquote::after{content:"\201D"}
        .quote figcaption{margin-top:12px;color:var(--muted);font-size:.95rem}
        .buttons{display:flex;flex-wrap:wrap;gap:12px;margin-bottom:24px}
        .cbtn{display:inline-flex;align-items:center;gap:10px;min-height:52px;padding:0 20px;border-radius:14px;font-weight:600;text-decoration:none}
        .cbtn.line{background:var(--surface);color:var(--text);border:1px solid var(--line)}
        .ic{flex:none}
        .enq{display:grid;gap:14px;max-width:560px}
        label{display:grid;gap:6px;font-size:.92rem;font-weight:600}.opt{font-weight:400;color:var(--muted)}
        input,textarea{width:100%;font:16px var(--body);color:var(--text);background:var(--bg);border:1px solid var(--line);border-radius:10px;padding:12px 14px;min-height:48px}
        input:focus,textarea:focus{outline:2px solid var(--accent);outline-offset:1px}
        .hp{position:absolute;left:-9999px;width:1px;height:1px;overflow:hidden}
        .note{margin:0;min-height:1.4em;font-size:.95rem;color:var(--muted)}.note.err{color:#c0392b}
        .muted{color:var(--muted)}
        .foot{max-width:1120px;margin:0 auto;padding:72px 20px 40px;display:flex;flex-wrap:wrap;gap:16px 24px;justify-content:space-between;align-items:center;
          color:var(--muted);font-size:.9rem}
        .linkish{display:inline-flex;align-items:center;gap:8px;background:none;border:0;padding:8px 0;color:inherit;font:inherit;cursor:pointer;text-decoration:underline;text-underline-offset:3px}
        .linkish .ic{width:18px;height:18px}
        .made{color:var(--muted);text-decoration:none;font-size:.85rem}.made:hover{text-decoration:underline}
        .lb{position:fixed;inset:0;z-index:50;background:rgba(0,0,0,.92);display:flex;align-items:center;justify-content:center}
        .lb[hidden]{display:none}
        .lb-img{max-width:100vw;max-height:calc(100vh - 96px);object-fit:contain;touch-action:pan-y}
        .lb-btn{position:absolute;width:48px;height:48px;border-radius:50%;border:0;display:grid;place-items:center;background:rgba(255,255,255,.12);color:#fff;cursor:pointer}
        .lb-close{top:max(12px,env(safe-area-inset-top));right:12px}.lb-prev{left:12px}.lb-next{right:12px}
        .lb-report{position:absolute;bottom:max(12px,env(safe-area-inset-bottom));left:50%;transform:translateX(-50%);color:#ddd;white-space:nowrap}
        @media(max-width:600px){.lb-prev,.lb-next{top:auto;bottom:max(56px,calc(env(safe-area-inset-bottom) + 44px))}}
        .dlg{background:rgba(0,0,0,.6);padding:16px;overflow-y:auto;align-items:flex-start}
        .rep{position:relative;width:100%;max-width:460px;margin:auto;display:grid;gap:14px;color:var(--text)}
        .rep .x{top:12px;right:12px;background:transparent;color:var(--text)}
        .rep-thumb{width:96px;height:96px;object-fit:cover;border-radius:8px}
        [hidden]{display:none!important}
        .rep-body{display:grid;gap:14px}
        .rep-pick{display:grid;grid-template-columns:repeat(auto-fill,minmax(64px,1fr));gap:6px;max-height:208px;overflow-y:auto;padding:2px}
        .rep-pick button{padding:0;border:2px solid transparent;border-radius:8px;background:none;cursor:pointer;aspect-ratio:1;overflow:hidden}
        .rep-pick button[aria-pressed=true]{border-color:var(--accent);box-shadow:0 0 0 2px var(--accent)}
        .rep-pick img{width:100%;height:100%;object-fit:cover}
        fieldset{border:0;padding:0;margin:0;display:grid;gap:8px}legend{font-weight:600;font-size:.92rem;margin-bottom:6px}
        .radio{display:flex;align-items:center;gap:10px;font-weight:400;min-height:40px}.radio input{width:20px;height:20px;min-height:0}
        @media(prefers-reduced-motion:reduce){html{scroll-behavior:auto}.tile img{transition:none}}
        """;

    /// <summary>Viewer, slideshow buttons, enquiry and remove-request forms. Small and dependency-free.</summary>
    private const string Script = """
        (function(){
        var P=window.__pf,api='/api/portfolio/@'+encodeURIComponent(P.handle);
        var lb=document.getElementById('lb'),img=lb.querySelector('.lb-img'),cur=0,last=null;
        function show(i){cur=(i+P.photos.length)%P.photos.length;img.src=P.photos[cur].src;}
        function open(i){last=document.activeElement;show(i);lb.hidden=false;document.body.style.overflow='hidden';lb.querySelector('.lb-close').focus();}
        function close(d){d.hidden=true;document.body.style.overflow='';if(last)last.focus();}
        document.querySelectorAll('[data-i]').forEach(function(b){b.addEventListener('click',function(){open(+b.dataset.i);});});
        lb.querySelector('.lb-close').onclick=function(){close(lb);};
        lb.querySelector('.lb-prev').onclick=function(){show(cur-1);};
        lb.querySelector('.lb-next').onclick=function(){show(cur+1);};
        lb.addEventListener('click',function(e){if(e.target===lb)close(lb);});
        var sx=null;img.addEventListener('touchstart',function(e){sx=e.touches[0].clientX;},{passive:true});
        img.addEventListener('touchend',function(e){if(sx===null)return;var dx=e.changedTouches[0].clientX-sx;sx=null;if(Math.abs(dx)>50)show(cur+(dx<0?1:-1));});
        document.addEventListener('keydown',function(e){
          if(!rep.hidden){if(e.key==='Escape')close(rep);return;}
          if(lb.hidden)return;if(e.key==='Escape')close(lb);if(e.key==='ArrowLeft')show(cur-1);if(e.key==='ArrowRight')show(cur+1);});
        document.querySelectorAll('[data-show]').forEach(function(s){var t=s.querySelector('.show-track'),p=s.querySelector('.prev'),n=s.querySelector('.next');
          function go(d){t.scrollBy({left:d*t.clientWidth,behavior:'smooth'});}if(p)p.onclick=function(){go(-1);};if(n)n.onclick=function(){go(1);};});
        function post(url,body,note,ok,fail){note.className='note';note.textContent='Sending...';fail=fail||function(){};
          fetch(url,{method:'POST',headers:{'Content-Type':'application/json'},body:JSON.stringify(body)}).then(function(r){
            return r.json().catch(function(){return {};}).then(function(j){
              if(r.ok){note.textContent=(j.data&&j.data.message)||'Sent.';ok();}
              else{note.className='note err';note.textContent=r.status===429?'Too many messages. Please try again later.':(j.message||'That didn’t send. Please try again.');fail();}});
          }).catch(function(){note.className='note err';note.textContent='No connection. Please try again.';fail();});}
        var enq=document.getElementById('enq');
        if(enq)enq.addEventListener('submit',function(e){e.preventDefault();var f=new FormData(enq),note=enq.querySelector('.note');
          var b={name:f.get('name'),contact:f.get('contact'),eventDate:f.get('eventDate')||null,message:f.get('message'),website:f.get('website')};
          if(!b.name||!b.contact||!b.message){note.className='note err';note.textContent='Add your name, a phone or email, and a message.';return;}
          post(api+'/enquiries',b,note,function(){enq.reset();});});
        var rep=document.getElementById('rep'),rf=rep.querySelector('form'),rthumb=rep.querySelector('.rep-thumb'),rpick=rep.querySelector('.rep-pick'),
          rbody=rep.querySelector('.rep-body'),rdone=rep.querySelector('.rep-done'),rsend=rep.querySelector('.rep-send'),rnote=rf.querySelector('.note'),rid=null;
        function mark(){rpick.querySelectorAll('button').forEach(function(b){b.setAttribute('aria-pressed',b.dataset.id===rid?'true':'false');});}
        P.photos.forEach(function(x){var b=document.createElement('button');b.type='button';b.dataset.id=x.id;b.setAttribute('aria-label','This photo');
          var im=document.createElement('img');im.alt='';im.loading='lazy';im.src=x.t||x.src;b.appendChild(im);
          b.onclick=function(){rid=rid===x.id?null:x.id;mark();};rpick.appendChild(b);});
        function report(id){rid=id;var ph=id&&P.photos.find(function(x){return x.id===id;});
          rthumb.hidden=!ph;if(ph)rthumb.src=ph.t||ph.src;rpick.hidden=!!ph||!P.photos.length;mark();
          rep.querySelector('.rep-which').textContent=ph?'We’ll pass this photo to the studio. No account needed.'
            :P.photos.length?'Tap the photo you mean, then tell us why. No account needed.':'Tell us which photo below. No account needed.';
          rbody.hidden=false;rdone.hidden=true;rsend.disabled=false;rnote.className='note';rnote.textContent='';
          last=document.activeElement;rep.hidden=false;document.body.style.overflow='hidden';rep.querySelector('[data-close]').focus();}
        document.querySelectorAll('[data-report]').forEach(function(b){b.onclick=function(){report(null);};});
        lb.querySelector('[data-report-photo]').onclick=function(){var id=P.photos[cur].id;close(lb);report(id);};
        rep.querySelectorAll('[data-close]').forEach(function(b){b.onclick=function(){close(rep);};});
        rep.addEventListener('click',function(e){if(e.target===rep)close(rep);});
        rf.addEventListener('submit',function(e){e.preventDefault();if(rsend.disabled)return;var f=new FormData(rf);
          if(!rid&&!String(f.get('note')||'').trim()){rnote.className='note err';rnote.textContent=P.photos.length?'Tap the photo you mean, or describe it under More details.':'Describe the photo under More details.';return;}
          rsend.disabled=true;
          post(api+'/report',{photoId:rid,reason:f.get('reason'),note:f.get('note'),contact:f.get('contact')},rnote,
            function(){rf.reset();rbody.hidden=true;rdone.hidden=false;rdone.focus();},function(){rsend.disabled=false;});});
        })();
        """;

    // ---------- helpers ----------

    private static string FontsUrl(PortfolioFontChoice f)
    {
        static string Family(string name, string weights) => "family=" + name.Replace(' ', '+') + ":wght@" + weights;
        // Display faces that come in one weight only: asking Google Fonts for another one fails the whole request.
        var parts = new List<string> { Family(f.Heading, SingleWeight(f.Heading) ? "400" : "500;600") };
        if (f.Body != f.Heading) parts.Add(Family(f.Body, "400;600"));
        else parts[0] = Family(f.Heading, "400;500;600");
        return "https://fonts.googleapis.com/css2?" + string.Join('&', parts) + "&display=swap";
    }

    private static bool SingleWeight(string family) => family is "DM Serif Display";

    private static string FontStack(string family, bool serif) =>
        family == "system-ui"
            ? "system-ui,-apple-system,'Segoe UI',Roboto,sans-serif"
            : $"'{family}',{(serif ? "Georgia,serif" : "system-ui,-apple-system,'Segoe UI',Roboto,sans-serif")}";

    private static string JsonString(string? s) => System.Text.Json.JsonSerializer.Serialize(s ?? "")
        .Replace("<", "\\u003c").Replace(">", "\\u003e");

    private static string JsonLd(PublicPortfolioDto p, string nonce, Func<string?, string?> abs)
    {
        var ld = new Dictionary<string, object?>
        {
            ["@context"] = "https://schema.org", ["@type"] = "ProfessionalService", ["name"] = p.DisplayName, ["url"] = p.Url,
            ["description"] = p.Tagline, ["image"] = abs(p.CoverUrl ?? p.LogoUrl), ["logo"] = abs(p.LogoUrl),
            ["email"] = p.Contact.Email, ["telephone"] = p.Contact.Phone,
            ["sameAs"] = p.Contact.Instagram is { } ig ? new[] { $"https://instagram.com/{ig}" } : null,
        };
        var json = System.Text.Json.JsonSerializer.Serialize(ld.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value))
            .Replace("<", "\\u003c");
        return $"<script type=\"application/ld+json\" nonce=\"{nonce}\">{json}</script>";
    }

    private static (double R, double G, double B) Rgb(string hex)
    {
        var h = hex.TrimStart('#');
        return (int.Parse(h[..2], NumberStyles.HexNumber) / 255.0, int.Parse(h[2..4], NumberStyles.HexNumber) / 255.0,
            int.Parse(h[4..6], NumberStyles.HexNumber) / 255.0);
    }

    private static double Luminance(string hex)
    {
        static double L(double c) => c <= 0.03928 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
        var (r, g, b) = Rgb(hex);
        return 0.2126 * L(r) + 0.7152 * L(g) + 0.0722 * L(b);
    }

    /// <summary>White or near-black, whichever reads better on the colour (WCAG contrast).</summary>
    public static string ReadableOn(string hex)
    {
        var l = Luminance(hex);
        var white = 1.05 / (l + 0.05);
        var dark = (l + 0.05) / (Luminance("#141414") + 0.05);
        return white >= dark ? "#FFFFFF" : "#141414";
    }

    private static string Mix(string a, string b, double t)
    {
        var (r1, g1, b1) = Rgb(a);
        var (r2, g2, b2) = Rgb(b);
        int C(double x, double y) => (int)Math.Round((x + (y - x) * t) * 255);
        return $"#{C(r1, r2):X2}{C(g1, g2):X2}{C(b1, b2):X2}";
    }
}
