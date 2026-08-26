namespace InvitesBlog.TemplateCompiler;

/// <summary>
/// What is left of <see cref="TemplateInjector"/> once <see cref="ServerBinder"/> has bound the
/// document: scroll state, and nothing else. It never reads a value out of the payload or writes one
/// into the DOM, so there is no second implementation of the binding contract to drift from the first.
///
/// It cannot be dropped entirely. <c>.is-visible</c>, <c>.is-open</c> and <c>--ib-progress</c> are
/// scroll state, which no amount of server rendering can precompute. The CSS-only replacement is
/// <c>animation-timeline: view()</c> plus <c>@property --ib-progress</c>, but iOS Safari only got
/// scroll-driven animations in Safari 26 — a guest on an older iPhone would receive a completely
/// static invitation, silently, and motion is the entire product. Revisit when that floor moves.
/// </summary>
public static class TemplateRuntime
{
    public const string Js = """
        (function () {
          'use strict';
          // The payload is still inlined so a template's own script can read it, even though nothing
          // here binds it. Same event and same shape the injector raised.
          var el = document.getElementById('invite-data');
          var data = {};
          try { data = JSON.parse((el && el.textContent) || '{}'); } catch (e) { data = {}; }
          try {
            window.invite = window.invite || {};
            window.invite.data = data;
            window.dispatchEvent(new CustomEvent('invite:data', { detail: data }));
          } catch (e) {}

          var sections = [];
          var envelope = null;

          // Scroll position as a CSS custom property, so a template can scrub an animation in plain
          // CSS — calc(var(--ib-progress) * …) — without writing a scroll handler of its own.
          function publish(p) {
            document.documentElement.style.setProperty('--ib-progress', p.toFixed(4));
            try {
              window.invite.progress = p;
              window.dispatchEvent(new CustomEvent('invite:progress', { detail: p }));
            } catch (e) {}
          }

          // Position-based rather than IntersectionObserver: a fast or programmatic scroll can never
          // "skip" a section and leave it stuck invisible.
          function revealInView() {
            var vh = window.innerHeight || document.documentElement.clientHeight || 0;
            for (var i = 0; i < sections.length; i++) {
              var s = sections[i];
              if (s.__ibShown) continue;
              if (s.getBoundingClientRect().top < vh * 0.9) {
                s.classList.add('ib-visible', 'is-visible');
                s.__ibShown = true;
              }
            }
          }

          function start() {
            sections = Array.prototype.slice.call(
              document.querySelectorAll('.ib-section, [data-reveal]'));
            envelope = document.querySelector('.ib-envelope, [data-envelope]');
            revealInView(); // whatever is already on screen
            publish(0);
            window.addEventListener('scroll', function () {
              revealInView();
              var max = (document.documentElement.scrollHeight || 0) - window.innerHeight;
              publish(max > 0 ? window.scrollY / max : 0);
              if (envelope && window.scrollY > window.innerHeight * 0.25) {
                envelope.classList.add('ib-opened', 'is-open');
              }
            }, { passive: true });
          }

          if (document.readyState === 'loading') {
            document.addEventListener('DOMContentLoaded', start);
          } else {
            start();
          }
        })();
        """;

    /// <summary>
    /// The response headers a bound invitation must be served with. The <c>sandbox</c> directive is
    /// the load-bearing one: it applies to a TOP-LEVEL document, not just to frames, so the invitation
    /// keeps the opaque origin the sandboxed iframe used to give it — verified in Chrome, where the
    /// rendered document reports <c>origin: null</c> and both <c>document.cookie</c> and
    /// <c>localStorage</c> throw <c>SecurityError</c> — while still being one document with no nested
    /// viewport.
    ///
    /// The flags are exactly the iframe's existing grants. <c>allow-popups</c> covers the
    /// <c>target="_blank"</c> venue links, and <c>allow-popups-to-escape-sandbox</c> is what stops the
    /// opened map inheriting the sandbox and breaking.
    ///
    /// NOT included, after measuring: <c>allow-top-navigation-by-user-activation</c>. Those flags
    /// govern a NESTED document navigating its top-level ancestor; a top-level document navigating
    /// ITSELF is not gated by them. Tested both ways — the RSVP link follows a click without the flag,
    /// and adding the flag does not stop a script navigating away on a timer with no user gesture.
    /// So it buys nothing here.
    ///
    /// Which means a hostile template CAN navigate away and take <c>location.href</c> with it. That is
    /// survivable only because nothing in the URL authorizes anything: the render id is opaque and
    /// useless without the HttpOnly cookie, and <see cref="ReferrerPolicy"/> keeps the invite token out
    /// of <c>document.referrer</c> on the way in. Do not put a credential in this URL.
    /// </summary>
    public const string ContentSecurityPolicy =
        "sandbox allow-scripts allow-popups allow-popups-to-escape-sandbox; " +
        "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; " +
        "img-src 'self' data:; font-src 'self' data:; base-uri 'none'; form-action 'none'";

    /// <summary>Keeps an invite token out of <c>document.referrer</c> on the way into the render.</summary>
    public const string ReferrerPolicy = "no-referrer";
}
