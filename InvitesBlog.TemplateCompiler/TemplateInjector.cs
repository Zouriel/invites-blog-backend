namespace InvitesBlog.TemplateCompiler;

/// <summary>
/// The single trusted injector shipped with every template package (compiled OR raw-HTML). It binds
/// campaign data into tagged elements and drives the reveal animation. Authors never write JS — this
/// is the only script that runs in the sandbox. Binding contract:
///   data-var="path"    → element.textContent = value        (text, never markup)
///   data-href="path"   → element href attribute             (links: rsvp.link, invite.link, map)
///   data-src="path"    → element src attribute              (images)
///   data-block="id"    → section kept only if the server resolved that block for this guest
///   data-reveal / .ib-section → gets .is-visible when scrolled into view (author styles the rest)
///   [data-envelope] / .ib-envelope → gets .is-open after the first scroll
/// Data arrives inline (#invite-data) or via postMessage({__inviteData}) from the host app.
/// </summary>
public static class TemplateInjector
{
    public const string Js = """
        (function () {
          'use strict';
          function get(data, path) {
            return path.split('.').reduce(function (o, k) {
              return (o && o[k] !== undefined && o[k] !== null) ? o[k] : undefined;
            }, data);
          }

          function apply(data) {
            data = data || {};
            var vars = document.querySelectorAll('[data-var]');
            for (var i = 0; i < vars.length; i++) {
              var v = get(data, vars[i].getAttribute('data-var'));
              vars[i].textContent = (v === undefined) ? '' : String(v); // text only, never markup
            }
            var links = document.querySelectorAll('[data-href]');
            for (var l = 0; l < links.length; l++) {
              var hv = get(data, links[l].getAttribute('data-href'));
              if (hv !== undefined) links[l].setAttribute('href', String(hv));
            }
            var imgs = document.querySelectorAll('[data-src]');
            for (var s = 0; s < imgs.length; s++) {
              var sv = get(data, imgs[s].getAttribute('data-src'));
              if (sv !== undefined) imgs[s].setAttribute('src', String(sv));
            }
            // Hide any element marked [data-optional] whose bound value(s) came back empty, so
            // nullable/omitted fields don't leave stray labels, dead links, or blank rows.
            function hasLink(node) {
              if (node.hasAttribute('data-href')) {
                var h = node.getAttribute('href');
                if (h && h !== '#' && h.indexOf('javascript:') !== 0) return true;
              }
              if (node.hasAttribute('data-src')) {
                var sc = node.getAttribute('src');
                if (sc && sc.length > 0) return true;
              }
              return false;
            }
            var optional = document.querySelectorAll('[data-optional]');
            for (var o = 0; o < optional.length; o++) {
              var el = optional[o];
              var filled = (el.hasAttribute('data-var') && el.textContent.trim().length > 0) || hasLink(el);
              if (!filled) {
                var inner = el.querySelectorAll('[data-var], [data-href], [data-src]');
                for (var q = 0; q < inner.length && !filled; q++) {
                  if ((inner[q].hasAttribute('data-var') && inner[q].textContent.trim().length > 0) || hasLink(inner[q])) {
                    filled = true;
                  }
                }
              }
              el.style.display = filled ? '' : 'none';
            }

            // Show the blocks the server resolved for this guest, hide the rest. NON-destructive
            // (display, not removeChild) so a later apply() with the real payload corrects an earlier
            // pass — the inert placeholder must never permanently strip a guest's role sections.
            var resolved = Array.isArray(data.resolvedBlocks) ? data.resolvedBlocks : null;
            if (resolved) {
              var blocks = document.querySelectorAll('[data-block]');
              for (var b = 0; b < blocks.length; b++) {
                blocks[b].style.display =
                  resolved.indexOf(blocks[b].getAttribute('data-block')) === -1 ? 'none' : '';
              }
            }
            // Expose the resolved invite data to custom template scripts (window.invite.data + an event),
            // so authors can build their own animations on top of the platform binding.
            try {
              window.invite = window.invite || {};
              window.invite.data = data;
              window.dispatchEvent(new CustomEvent('invite:data', { detail: data }));
            } catch (e) {}
            startAnimation();
          }

          var animationStarted = false;
          var revealSections = [];
          // Reveal any section scrolled into view. Position-based (not IntersectionObserver), so a fast
          // or jumpy programmatic scroll can never "skip" a section and leave it stuck invisible.
          function revealInView() {
            var vh = window.innerHeight || document.documentElement.clientHeight || 0;
            for (var i = 0; i < revealSections.length; i++) {
              var el = revealSections[i];
              if (el.__ibShown) continue;
              if (el.getBoundingClientRect().top < vh * 0.9) {
                el.classList.add('ib-visible', 'is-visible');
                el.__ibShown = true;
              }
            }
          }
          function startAnimation() {
            if (animationStarted) return;
            animationStarted = true;
            revealSections = Array.prototype.slice.call(document.querySelectorAll('.ib-section, [data-reveal]'));
            var reduce = window.matchMedia && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
            if (reduce) {
              for (var k = 0; k < revealSections.length; k++) { revealSections[k].classList.add('ib-visible', 'is-visible'); revealSections[k].__ibShown = true; }
              reportHeight(); return;
            }
            revealInView(); // reveal whatever is already on-screen
            window.addEventListener('scroll', revealInView, { passive: true });
            var envelope = document.querySelector('.ib-envelope, [data-envelope]');
            if (envelope) {
              window.addEventListener('scroll', function () {
                if (window.scrollY > window.innerHeight * 0.25) envelope.classList.add('ib-opened', 'is-open');
              }, { passive: true });
            }
            reportHeight();
          }

          // Report our scrollable content height so the host can create a matching scroll spacer.
          // The iframe stays a fixed 100vh box, so vh units (and this height) are stable.
          function reportHeight() {
            try {
              if (window.parent && window.parent !== window) {
                var h = Math.max(document.documentElement.scrollHeight, document.body ? document.body.scrollHeight : 0);
                window.parent.postMessage({ __inviteScrollHeight: h }, '*');
              }
            } catch (e) {}
          }

          // Drive our OWN internal scroll from the host page's scroll progress (0..1). Programmatic
          // scroll works inside iframes on iOS Safari even though touch-scroll does not — this is what
          // makes the reveal/envelope animation run on iPhone.
          function applyProgress(p) {
            if (p < 0) p = 0; else if (p > 1) p = 1;
            var max = (document.documentElement.scrollHeight || 0) - window.innerHeight;
            window.scrollTo(0, p * (max > 0 ? max : 0));
            revealInView(); // catch every section the jump scrolled past
            // Expose scroll progress (0..1) to custom template scripts for scroll-scrubbed animation.
            try {
              window.invite = window.invite || {};
              window.invite.progress = p;
              window.dispatchEvent(new CustomEvent('invite:progress', { detail: p }));
            } catch (e) {}
          }

          var dataEl = document.getElementById('invite-data');
          var inline = {};
          try { inline = JSON.parse((dataEl && dataEl.textContent) || '{}'); } catch (e) { inline = {}; }
          function hasKeys(o) { if (!o) return false; for (var k in o) { if (Object.prototype.hasOwnProperty.call(o, k)) return true; } return false; }
          // Only bind the inline payload if it actually carries data — the inert placeholder
          // (empty event/guest, empty resolvedBlocks) must NOT run apply(), or it would hide every
          // conditional block before the host's real postMessage arrives.
          if (hasKeys(inline.event) || hasKeys(inline.guest) || (Array.isArray(inline.resolvedBlocks) && inline.resolvedBlocks.length)) apply(inline); else startAnimation();

          window.addEventListener('message', function (e) {
            if (!e.data) return;
            if (e.data.__inviteData) apply(e.data.__inviteData);
            if (typeof e.data.__inviteProgress === 'number') applyProgress(e.data.__inviteProgress);
          });
          window.addEventListener('resize', reportHeight, { passive: true });
          window.addEventListener('load', reportHeight);
          setTimeout(reportHeight, 80); setTimeout(reportHeight, 400); setTimeout(reportHeight, 1400);
          try { if (window.parent && window.parent !== window) window.parent.postMessage({ __inviteReady: true }, '*'); } catch (e) {}
        })();
        """;

    /// <summary>The inert JSON payload placeholder the host app fills (inline or via postMessage).</summary>
    public const string InviteDataScript =
        "<script id=\"invite-data\" type=\"application/json\">" +
        "{\"event\":{},\"guest\":{},\"venue\":{},\"inviter\":{},\"rsvp\":{},\"invite\":{},\"theme\":{},\"resolvedBlocks\":[]}" +
        "</script>";
}
