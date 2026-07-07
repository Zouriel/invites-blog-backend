using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace InvitesBlog.TemplateCompiler;

/// <summary>
/// Compiles a <see cref="Scene"/> into the trusted HTML/CSS/JS package of §5. All executable code
/// is emitted here — designers never write JS. The generated <c>template.js</c> is a fixed,
/// audited injector shared by every template ("one injector", §5.5): it binds guest/inviter values
/// via <c>textContent</c> only (inert by construction, §5.3), shows only the content blocks the
/// server resolved, and drives the scroll/envelope animation with a reduced-motion variant (§5.4).
/// </summary>
public sealed partial class SceneCompiler
{
    public const int CriticalPathBudgetBytes = 300 * 1024; // §5.4

    [GeneratedRegex(@"\{\{\s*([a-zA-Z0-9_.]+)\s*\}\}")]
    private static partial Regex VariableRegex();

    private static readonly JsonSerializerOptions JsonOut = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public CompiledTemplatePackage Compile(Scene scene)
    {
        var warnings = new List<string>();
        var variables = new SortedSet<string>(StringComparer.Ordinal);
        var editableAreas = new SortedSet<string>(StringComparer.Ordinal);
        var contentBlocks = new SortedSet<string>(StringComparer.Ordinal);

        var body = new StringBuilder();
        body.Append(RenderEnvelope(scene.Envelope));

        foreach (var section in scene.Sections)
        {
            if (string.IsNullOrWhiteSpace(section.Id))
            {
                warnings.Add("A section is missing an id and was skipped.");
                continue;
            }

            var isBlock = section.Visibility.Type.Equals("block", StringComparison.OrdinalIgnoreCase)
                          && !string.IsNullOrWhiteSpace(section.Visibility.Block);
            if (isBlock) contentBlocks.Add(section.Visibility.Block!);

            var blockAttr = isBlock ? $" data-block=\"{Attr(section.Visibility.Block!)}\"" : string.Empty;
            body.Append($"<section id=\"sec-{Attr(section.Id)}\" class=\"ib-section ib-{Attr(section.Type)} ib-layout-{Attr(section.Layout)}\" data-reveal=\"{Attr(section.Reveal)}\"{blockAttr}>");
            body.Append("<div class=\"ib-inner\">");

            foreach (var (key, value) in section.Content)
            {
                editableAreas.Add(key);
                foreach (Match m in VariableRegex().Matches(value))
                    variables.Add(m.Groups[1].Value);
                body.Append(RenderField(section.Type, key, value));
            }

            body.Append("</div></section>");
        }

        foreach (var v in scene.CustomVariables) variables.Add(v);

        var manifest = new TemplateManifest
        {
            Slug = scene.Slug,
            Version = scene.Version,
            Variables = variables.ToList(),
            Roles = scene.Roles,
            GenderVariants = scene.GenderVariants,
            EditableAreas = editableAreas.ToList(),
            ContentBlocks = contentBlocks.ToList()
        };

        var css = RenderCss(scene.Theme, scene.Envelope);
        var js = InjectorJs;
        var html = RenderHtml(scene, body.ToString());

        var bytes = Encoding.UTF8.GetByteCount(html)
                    + Encoding.UTF8.GetByteCount(css)
                    + Encoding.UTF8.GetByteCount(js);

        if (bytes > CriticalPathBudgetBytes)
            warnings.Add($"Template critical path is {bytes / 1024}KB, over the {CriticalPathBudgetBytes / 1024}KB budget (§5.4). Trim inline content or move media to lazy-loaded assets.");

        return new CompiledTemplatePackage
        {
            Manifest = manifest,
            ManifestJson = JsonSerializer.Serialize(manifest, JsonOut),
            IndexHtml = html,
            StylesCss = css,
            TemplateJs = js,
            CriticalPathBytes = bytes,
            Warnings = warnings
        };
    }

    // ----- HTML -----

    private const string HtmlTemplate = """
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>__TITLE__</title>
        <link rel="stylesheet" href="styles.css">
        </head>
        <body>
        <!-- Guest/inviter data is injected here as inert JSON (§5.3). Only the resolved block list ships. -->
        <script id="invite-data" type="application/json">{"event":{},"guest":{},"venue":{},"inviter":{},"rsvp":{},"theme":{},"resolvedBlocks":[]}</script>
        <main id="ib-root" class="ib-intensity-__INTENSITY__">
        __BODY__
        </main>
        <noscript><style>.ib-section{opacity:1 !important;transform:none !important}.ib-envelope{display:none}</style></noscript>
        <script src="template.js"></script>
        </body>
        </html>
        """;

    private static string RenderHtml(Scene scene, string body) => HtmlTemplate
        .Replace("__TITLE__", WebUtility.HtmlEncode(scene.Name))
        .Replace("__INTENSITY__", Attr(scene.Theme.Intensity))
        .Replace("__BODY__", body);

    private const string EnvelopeTemplate = """
        <div class="ib-envelope ib-env-__STYLE__" data-reveal="envelope" aria-hidden="true">
          <div class="ib-env-flap"></div>
          <div class="ib-env-seal"></div>
          <div class="ib-env-label">__LABEL__</div>
          <div class="ib-scroll-hint">Scroll to open ↓</div>
        </div>
        """;

    private static string RenderEnvelope(SceneEnvelope env) => EnvelopeTemplate
        .Replace("__STYLE__", Attr(env.Style))
        .Replace("__LABEL__", WebUtility.HtmlEncode(env.Label));

    /// <summary>
    /// Renders one content field, turning {{var}} tokens into data-var spans the injector fills via
    /// textContent (never innerHTML — guest content is inert, §5.3). Literal text is HTML-escaped.
    /// </summary>
    private static string RenderField(string sectionType, string key, string value)
    {
        var tag = key switch
        {
            "title" => "h1",
            "subtitle" or "heading" => "h2",
            _ => "p"
        };
        var inner = RenderBindable(value);
        return $"<{tag} class=\"ib-field ib-field-{Attr(key)}\">{inner}</{tag}>";
    }

    private static string RenderBindable(string value)
    {
        var sb = new StringBuilder();
        var last = 0;
        foreach (Match m in VariableRegex().Matches(value))
        {
            if (m.Index > last)
                sb.Append(WebUtility.HtmlEncode(value[last..m.Index]));
            sb.Append($"<span data-var=\"{Attr(m.Groups[1].Value)}\"></span>");
            last = m.Index + m.Length;
        }
        if (last < value.Length)
            sb.Append(WebUtility.HtmlEncode(value[last..]));
        return sb.Length == 0 ? WebUtility.HtmlEncode(value) : sb.ToString();
    }

    private static string Attr(string s) => WebUtility.HtmlEncode(s).Replace("\"", "&quot;");

    // ----- CSS -----

    private static string RenderCss(SceneTheme t, SceneEnvelope env) => CssTemplate
        .Replace("__PRIMARY__", t.Primary)
        .Replace("__ACCENT__", t.Accent)
        .Replace("__BG__", t.Background)
        .Replace("__SURFACE__", t.Surface)
        .Replace("__TEXT__", t.Text)
        .Replace("__HEADING__", t.HeadingFont)
        .Replace("__BODY__", t.BodyFont);

    private const string CssTemplate = """
        :root{
          --ib-primary:__PRIMARY__;--ib-accent:__ACCENT__;--ib-bg:__BG__;
          --ib-surface:__SURFACE__;--ib-text:__TEXT__;
          --ib-heading:__HEADING__;--ib-body:__BODY__;
        }
        *{box-sizing:border-box}
        html,body{margin:0;padding:0}
        body{background:var(--ib-bg);color:var(--ib-text);font-family:var(--ib-body);line-height:1.6}
        .ib-section{min-height:70vh;display:flex;align-items:center;justify-content:center;padding:8vh 6vw;
          opacity:0;transform:translateY(40px);transition:opacity .8s ease,transform .8s ease}
        .ib-section.ib-visible{opacity:1;transform:none}
        .ib-inner{max-width:720px;width:100%;text-align:center}
        h1.ib-field{font-family:var(--ib-heading);font-size:clamp(2.4rem,7vw,4.5rem);color:var(--ib-primary);margin:0 0 .3em}
        h2.ib-field{font-family:var(--ib-heading);font-size:clamp(1.4rem,4vw,2.2rem);font-weight:500;margin:0 0 .6em}
        p.ib-field{font-size:clamp(1rem,2.4vw,1.2rem);margin:0 0 1em}
        .ib-hero{background:linear-gradient(160deg,var(--ib-surface),var(--ib-bg))}
        .ib-dressCode,.ib-roleBlock{background:var(--ib-surface)}
        .ib-venue .ib-inner{border-top:1px solid var(--ib-accent);padding-top:2rem}
        .ib-rsvp{background:var(--ib-primary);color:#fff}
        .ib-rsvp h2.ib-field,.ib-rsvp h1.ib-field{color:#fff}
        /* Envelope */
        .ib-envelope{position:relative;height:100vh;display:flex;flex-direction:column;align-items:center;
          justify-content:center;background:var(--ib-primary);color:#fff;text-align:center}
        .ib-env-label{font-family:var(--ib-heading);font-size:clamp(1.8rem,6vw,3.5rem)}
        .ib-env-flap{position:absolute;top:0;left:50%;width:60vmin;height:30vmin;background:var(--ib-accent);
          transform:translateX(-50%);clip-path:polygon(0 0,100% 0,50% 100%);transform-origin:top center;
          transition:transform 1s ease}
        .ib-env-seal{width:64px;height:64px;border-radius:50%;background:var(--ib-accent);margin-bottom:1rem;
          box-shadow:0 0 0 4px rgba(255,255,255,.3)}
        .ib-scroll-hint{position:absolute;bottom:6vh;font-size:.9rem;opacity:.85;animation:ibBounce 1.6s infinite}
        .ib-opened .ib-env-flap{transform:translateX(-50%) rotateX(180deg)}
        @keyframes ibBounce{0%,100%{transform:translateY(0)}50%{transform:translateY(8px)}}
        /* Reveal presets */
        [data-reveal="curtain"]{clip-path:inset(0 0 100% 0);transition:clip-path 1s ease,opacity .8s ease}
        [data-reveal="curtain"].ib-visible{clip-path:inset(0 0 0 0)}
        .ib-intensity-subtle .ib-section{transform:translateY(16px)}
        .ib-intensity-dramatic .ib-section{transform:translateY(64px) scale(.96)}
        /* Progressive enhancement: CSS scroll-driven where supported (§5.4) */
        @supports (animation-timeline: view()){
          .ib-section{animation:ibReveal linear both;animation-timeline:view();animation-range:entry 10% cover 35%}
          @keyframes ibReveal{from{opacity:0;transform:translateY(40px)}to{opacity:1;transform:none}}
        }
        /* Reduced motion: no scrubbing, simple fades, everything reachable (§5.4/§19.2) */
        @media (prefers-reduced-motion: reduce){
          .ib-section{opacity:1;transform:none;transition:none;animation:none}
          .ib-env-flap,.ib-scroll-hint{animation:none;transition:none}
          .ib-envelope{height:auto;padding:12vh 0}
        }
        """;

    // ----- The one shared injector (§5.5). Trusted, platform-authored. -----

    private const string InjectorJs = """
        (function () {
          'use strict';

          function get(data, path) {
            return path.split('.').reduce(function (o, k) {
              return (o && o[k] !== undefined && o[k] !== null) ? o[k] : undefined;
            }, data);
          }

          // Apply a payload: bind variables (textContent only) and prune unresolved blocks (§5.3 / §12).
          function apply(data) {
            data = data || {};
            var slots = document.querySelectorAll('[data-var]');
            for (var i = 0; i < slots.length; i++) {
              var v = get(data, slots[i].getAttribute('data-var'));
              slots[i].textContent = (v === undefined) ? '' : String(v); // text only, never markup
            }
            var resolved = Array.isArray(data.resolvedBlocks) ? data.resolvedBlocks : null;
            if (resolved) {
              var blocks = document.querySelectorAll('[data-block]');
              for (var j = 0; j < blocks.length; j++) {
                if (resolved.indexOf(blocks[j].getAttribute('data-block')) === -1 && blocks[j].parentNode) {
                  blocks[j].parentNode.removeChild(blocks[j]);
                }
              }
            }
            startAnimation();
          }

          var animationStarted = false;
          function startAnimation() {
            if (animationStarted) return;
            animationStarted = true;
            var reduce = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
            var sections = document.querySelectorAll('.ib-section');
            if (reduce) {
              for (var k = 0; k < sections.length; k++) sections[k].classList.add('ib-visible');
              return; // no scrubbing, content already reachable (§5.4)
            }
            if ('IntersectionObserver' in window) {
              var io = new IntersectionObserver(function (entries) {
                entries.forEach(function (en) {
                  if (en.isIntersecting) { en.target.classList.add('ib-visible'); io.unobserve(en.target); }
                });
              }, { threshold: 0.15 });
              for (var s = 0; s < sections.length; s++) io.observe(sections[s]);
            } else {
              for (var s2 = 0; s2 < sections.length; s2++) sections[s2].classList.add('ib-visible');
            }
            var envelope = document.querySelector('.ib-envelope');
            if (envelope) {
              window.addEventListener('scroll', function () {
                if (window.scrollY > window.innerHeight * 0.25) envelope.classList.add('ib-opened');
              }, { passive: true });
            }
          }

          // Source 1: inline #invite-data (standalone / thumbnail render / preview).
          var dataEl = document.getElementById('invite-data');
          var inline = {};
          try { inline = JSON.parse(dataEl.textContent || '{}'); } catch (e) { inline = {}; }
          if (inline && (inline.event || inline.guest)) apply(inline);
          else startAnimation();

          // Source 2: postMessage from the host app (cross-origin sandboxed iframe, §5.5).
          window.addEventListener('message', function (e) {
            if (e.data && e.data.__inviteData) apply(e.data.__inviteData);
          });

          // Tell the host we're ready to receive data.
          try { if (window.parent && window.parent !== window) window.parent.postMessage({ __inviteReady: true }, '*'); } catch (e) {}
        })();
        """;
}
