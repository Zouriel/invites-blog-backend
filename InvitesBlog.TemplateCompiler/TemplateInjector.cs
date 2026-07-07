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
            var resolved = Array.isArray(data.resolvedBlocks) ? data.resolvedBlocks : null;
            if (resolved) {
              var blocks = document.querySelectorAll('[data-block]');
              for (var b = 0; b < blocks.length; b++) {
                if (resolved.indexOf(blocks[b].getAttribute('data-block')) === -1 && blocks[b].parentNode) {
                  blocks[b].parentNode.removeChild(blocks[b]);
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
            var sections = document.querySelectorAll('.ib-section, [data-reveal]');
            if (reduce) {
              for (var k = 0; k < sections.length; k++) sections[k].classList.add('ib-visible', 'is-visible');
              return;
            }
            if ('IntersectionObserver' in window) {
              var io = new IntersectionObserver(function (entries) {
                entries.forEach(function (en) {
                  if (en.isIntersecting) { en.target.classList.add('ib-visible', 'is-visible'); io.unobserve(en.target); }
                });
              }, { threshold: 0.15 });
              for (var s2 = 0; s2 < sections.length; s2++) io.observe(sections[s2]);
            } else {
              for (var s3 = 0; s3 < sections.length; s3++) sections[s3].classList.add('ib-visible', 'is-visible');
            }
            var envelope = document.querySelector('.ib-envelope, [data-envelope]');
            if (envelope) {
              window.addEventListener('scroll', function () {
                if (window.scrollY > window.innerHeight * 0.25) envelope.classList.add('ib-opened', 'is-open');
              }, { passive: true });
            }
          }

          var dataEl = document.getElementById('invite-data');
          var inline = {};
          try { inline = JSON.parse((dataEl && dataEl.textContent) || '{}'); } catch (e) { inline = {}; }
          if (inline && (inline.event || inline.guest)) apply(inline); else startAnimation();

          window.addEventListener('message', function (e) {
            if (e.data && e.data.__inviteData) apply(e.data.__inviteData);
          });
          try { if (window.parent && window.parent !== window) window.parent.postMessage({ __inviteReady: true }, '*'); } catch (e) {}
        })();
        """;

    /// <summary>The inert JSON payload placeholder the host app fills (inline or via postMessage).</summary>
    public const string InviteDataScript =
        "<script id=\"invite-data\" type=\"application/json\">" +
        "{\"event\":{},\"guest\":{},\"venue\":{},\"inviter\":{},\"rsvp\":{},\"invite\":{},\"theme\":{},\"resolvedBlocks\":[]}" +
        "</script>";
}
