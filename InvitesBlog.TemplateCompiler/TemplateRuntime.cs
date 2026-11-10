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

          // The event's date and time, rewritten in the viewer's own locale (day/month order, 12 or 24
          // hour clock) on the EVENT's clock, plus "(9:00 pm your time)" when the viewer's clock reads
          // differently. The server already wrote a plain English default, so a browser without Intl,
          // or a template whose host typed their own wording (dateAuto/timeAuto false), keeps what it has.
          function localiseWhen() {
            try {
              var ev = data && data.event;
              if (!ev || !ev.startsAt || !ev.timeZone || !window.Intl) return;
              var ms = Date.parse(ev.startsAt);
              if (isNaN(ms)) return;
              var zone = ev.timeZone;
              var locale = (navigator.languages && navigator.languages[0]) || navigator.language || undefined;
              var mine = Intl.DateTimeFormat().resolvedOptions().timeZone;
              function fmt(opts, z) {
                opts.timeZone = z;
                return new Intl.DateTimeFormat(locale, opts).format(new Date(ms)).replace(/[  ]/g, ' ');
              }
              function offset(z) {
                var f = new Intl.DateTimeFormat('en-US', { timeZone: z, hourCycle: 'h23', year: 'numeric',
                  month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', second: '2-digit' });
                var p = {};
                f.formatToParts(new Date(ms)).forEach(function (x) { if (x.type !== 'literal') p[x.type] = +x.value; });
                return Math.round((Date.UTC(p.year, p.month - 1, p.day, p.hour % 24, p.minute, p.second) -
                  Math.floor(ms / 1000) * 1000) / 60000);
              }
              function label(z) {
                var parts = new Intl.DateTimeFormat(locale, { timeZone: z, timeZoneName: 'short' }).formatToParts(new Date(ms));
                for (var i = 0; i < parts.length; i++) if (parts[i].type === 'timeZoneName') {
                  return parts[i].value.replace(/^(GMT|UTC)[+-]0(:00)?$/, 'GMT');
                }
                return z;
              }
              var clock = { hour: 'numeric', minute: '2-digit' };
              var dateText = fmt({ weekday: 'long', day: 'numeric', month: 'long', year: 'numeric' }, zone);
              var timeText = fmt(clock, zone) + ' ' + label(zone);
              if (mine && mine !== zone && offset(mine) !== offset(zone)) {
                var dayHere = fmt({ year: 'numeric', month: 'numeric', day: 'numeric' }, zone);
                var dayThere = fmt({ year: 'numeric', month: 'numeric', day: 'numeric' }, mine);
                timeText += ' (' + (dayHere !== dayThere ? fmt({ weekday: 'short' }, mine) + ' ' : '') +
                  fmt(clock, mine) + ' your time)';
              }
              if (ev.dateAuto) {
                Array.prototype.forEach.call(document.querySelectorAll('[data-var="event.date"]'), function (n) { n.textContent = dateText; });
              }
              if (ev.timeAuto && !ev.allDay) {
                Array.prototype.forEach.call(document.querySelectorAll('[data-var="event.time"]'), function (n) { n.textContent = timeText; });
              }
            } catch (e) {}
          }

          // Countdowns ([data-countdown], from the designer) count to the event's start. Days-only counts
          // calendar days on the event's clock, so the day before reads "1 day" whatever the hour; the
          // smaller units count the exact time left. On the event's day each shows its own wording
          // ([data-done]); after that day it hides, since there is nothing left to count to.
          function countdowns() {
            var nodes = Array.prototype.slice.call(document.querySelectorAll('[data-countdown]'));
            var ev = data && data.event;
            if (!nodes.length || !ev || !ev.startsAt) return;
            var at = Date.parse(ev.startsAt);
            if (isNaN(at)) return;
            var days = null;
            try {
              days = new Intl.DateTimeFormat('en-US', { timeZone: ev.timeZone || undefined, year: 'numeric', month: 'numeric', day: 'numeric' });
            } catch (e) { days = null; }
            function dayOf(ms) {
              if (!days) return Math.floor(ms / 86400000);
              var p = {};
              days.formatToParts(new Date(ms)).forEach(function (x) { if (x.type !== 'literal') p[x.type] = +x.value; });
              return Math.round(Date.UTC(p.year, p.month - 1, p.day) / 86400000);
            }
            var eventDay = dayOf(at);
            var words = { d: ['day', 'days'], h: ['hour', 'hours'], m: ['minute', 'minutes'], s: ['second', 'seconds'] };
            function two(n) { return n < 10 ? '0' + n : '' + n; }
            function tick() {
              var now = Date.now(), today = dayOf(now), left = Math.max(0, at - now);
              nodes.forEach(function (n) {
                var smallest = n.getAttribute('data-countdown');
                if (today > eventDay) { n.style.display = 'none'; return; }
                var done = smallest === 'd' ? today >= eventDay : left <= 0;
                if (done) {
                  n.setAttribute('data-done', '');
                  if (!(n.querySelector('.cx') || {}).textContent) n.style.visibility = 'hidden';
                  return;
                }
                var v = smallest === 'd'
                  ? { d: eventDay - today }
                  : { d: Math.floor(left / 86400000), h: Math.floor(left / 3600000) % 24, m: Math.floor(left / 60000) % 60, s: Math.floor(left / 1000) % 60 };
                Array.prototype.forEach.call(n.querySelectorAll('[data-u]'), function (u) {
                  var k = u.getAttribute('data-u'), x = v[k] || 0;
                  var b = u.querySelector('b'), i = u.querySelector('i');
                  if (b) b.textContent = k === 'd' ? '' + x : two(x);
                  if (i && words[k]) i.textContent = words[k][x === 1 ? 0 : 1];
                });
              });
            }
            tick();
            setInterval(tick, 1000);
          }

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
            localiseWhen();
            countdowns();
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

    /// <summary>
    /// For an uploaded design: the same rules without <c>sandbox</c>. That page is markup we wrote
    /// around one image or clip, so the sandbox guards nothing there, and it costs every guest the
    /// download: a sandboxed page has an opaque origin, and Chrome gives an opaque origin a new cache
    /// on every load (measured: a sandboxed page fetched its image on each of three opens, the same
    /// page without it once). Designer templates keep the sandbox.
    /// </summary>
    public const string UploadedDesignContentSecurityPolicy =
        "default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; " +
        "img-src 'self' data:; media-src 'self'; font-src 'self' data:; base-uri 'none'; form-action 'none'";

    /// <summary>Keeps an invite token out of <c>document.referrer</c> on the way into the render.</summary>
    public const string ReferrerPolicy = "no-referrer";
}
