namespace InvitesBlog.TemplateCompiler.Design;

/// <summary>
/// Converts a hand-written template into a designer scene by measuring what it renders.
///
/// <para>Runs INSIDE the template, in a sandboxed frame at phone size (390 × 844), after the server has
/// bound it with sample data. It finds the visible pieces — text (keeping its bindings), photo slots,
/// buttons, pictures, inline SVGs, filled boxes, the dress-colour spot — then steps through the scroll
/// and records where each one is. Movement becomes keyframes, simplified until only the turns remain.
/// Time-based animation (a bouncing arrow) is frozen at its start; scroll-triggered transitions are
/// made instant so they register as the step they are.</para>
///
/// <para>It is a conversion, not a copy: layout that depends on JavaScript beyond scroll position,
/// blend modes, masks, 3D and filters don't survive. What it produces is ordinary scene JSON, which the
/// server validates like any other; the frame's opaque origin keeps the template's own script away
/// from the editor page while it runs.</para>
/// </summary>
public static class DesignImportScript
{
    public const string Js = """
(function () {
  'use strict';
  var W = 390, VIEW = 844;
  function post(msg) { try { parent.postMessage(msg, '*'); } catch (e) {} }
  function progress(text) { post({ type: 'ib:import-progress', progress: text }); }
  function wait(ms) { return new Promise(function (r) { setTimeout(r, ms); }); }
  function frame() { return new Promise(function (r) { var done = false; requestAnimationFrame(function () { requestAnimationFrame(function () { if (!done) { done = true; r(); } }); }); setTimeout(function () { if (!done) { done = true; r(); } }, 120); }); }
  function round(v, d) { var f = Math.pow(10, d || 1); return Math.round(v * f) / f; }

  // ----- Colours -----
  var probe = document.createElement('canvas').getContext('2d');
  function hex(css) {
    if (!css || css === 'transparent') return null;
    var m = /rgba?\(\s*([\d.]+)[ ,]+([\d.]+)[ ,]+([\d.]+)(?:[ ,/]+([\d.]+%?))?\s*\)/.exec(css);
    if (!m) {
      try { probe.fillStyle = '#000'; probe.fillStyle = css; var v = probe.fillStyle; if (v[0] === '#') return v.toLowerCase(); m = /rgba?\(\s*([\d.]+)[ ,]+([\d.]+)[ ,]+([\d.]+)(?:[ ,/]+([\d.]+%?))?\s*\)/.exec(v); } catch (e) { return null; }
      if (!m) return null;
    }
    var a = m[4] === undefined ? 1 : (m[4].indexOf('%') > 0 ? parseFloat(m[4]) / 100 : parseFloat(m[4]));
    if (a <= 0.02) return null;
    function h(n) { var s = Math.max(0, Math.min(255, Math.round(+n))).toString(16); return s.length < 2 ? '0' + s : s; }
    var out = '#' + h(m[1]) + h(m[2]) + h(m[3]);
    return a < 0.98 ? out + h(a * 255) : out;
  }

  function firstGradientColor(image) {
    if (!image || !/gradient\(/.test(image)) return null;
    var m = /(rgba?\([^)]+\)|#[0-9a-fA-F]{3,8})/.exec(image);
    return m ? hex(m[1]) : null;
  }
  function fillOf(cs) { return hex(cs.backgroundColor) || firstGradientColor(cs.backgroundImage); }

  // ----- Theme -----
  var FONTS = { 'playfair display': 'playfair-display', 'cormorant garamond': 'cormorant-garamond', 'cormorant': 'cormorant-garamond', 'lora': 'lora',
    'libre baskerville': 'libre-baskerville', 'cinzel': 'cinzel', 'italiana': 'italiana', 'inter': 'inter', 'montserrat': 'montserrat',
    'josefin sans': 'josefin-sans', 'poppins': 'poppins', 'great vibes': 'great-vibes', 'parisienne': 'parisienne', 'dancing script': 'dancing-script', 'allura': 'allura' };
  function fontId(family) {
    var list = (family || '').split(',').map(function (f) { return f.trim().replace(/^["']|["']$/g, '').toLowerCase(); });
    for (var i = 0; i < list.length; i++) if (FONTS[list[i]]) return FONTS[list[i]];
    var joined = list.join(' ');
    if (/cursive|script|vibes|hand/.test(joined)) return 'great-vibes';
    if (/mono/.test(joined)) return 'inter';
    if (/sans/.test(joined) || /system-ui|helvetica|arial/.test(joined)) return 'inter';
    if (/serif|georgia|times|garamond|display|didot|bodoni/.test(joined)) return 'lora';
    return 'inter';
  }

  function readTheme() {
    var theme = [], seen = {}, root = getComputedStyle(document.documentElement);
    function collect(rules) {
      for (var i = 0; i < rules.length; i++) {
        var r = rules[i];
        if (r.cssRules && !r.style) { try { collect(r.cssRules); } catch (e) {} continue; }
        if (!r.style) continue;
        for (var j = 0; j < r.style.length; j++) {
          var name = r.style[j];
          if (name.indexOf('--ib-') !== 0) continue;
          var key = name.slice(5).toLowerCase().replace(/[^a-z0-9-]/g, '');
          if (!key || seen[key]) continue;
          var value = (root.getPropertyValue(name) || r.style.getPropertyValue(name)).trim();
          if (key.indexOf('font') >= 0) { seen[key] = 1; theme.push({ key: key, label: label(key), value: fontId(value) }); continue; }
          var c = hex(value);
          if (c) { seen[key] = 1; theme.push({ key: key, label: label(key), value: c.slice(0, 7) }); }
        }
      }
    }
    for (var s = 0; s < document.styleSheets.length; s++) { try { collect(document.styleSheets[s].cssRules); } catch (e) {} }
    var body = getComputedStyle(document.body);
    function ensure(key, value, lbl) { if (!seen[key] && value) { seen[key] = 1; theme.push({ key: key, label: lbl, value: value.slice(0, 7) }); } }
    ensure('bg', hex(body.backgroundColor) || hex(root.backgroundColor) || '#ffffff', 'Background');
    ensure('text', hex(body.color) || '#222222', 'Text');
    var link = document.querySelector('a, button, h1, h2');
    ensure('accent', (link && hex(getComputedStyle(link).color)) || '#b08d57', 'Accent');
    if (!theme.some(function (t) { return t.key.indexOf('font') >= 0; })) theme.push({ key: 'body-font', label: 'Body font', value: fontId(body.fontFamily) });
    return theme;
  }
  function label(key) { return key.split('-').map(function (w) { return w.charAt(0).toUpperCase() + w.slice(1); }).join(' '); }
  function colorRef(theme, c) {
    if (!c) return null;
    for (var i = 0; i < theme.length; i++) if (theme[i].key.indexOf('font') < 0 && theme[i].value === c.slice(0, 7) && c.length === 7) return 'theme:' + theme[i].key;
    return c;
  }
  function fontRef(theme, family) {
    var id = fontId(family);
    for (var i = 0; i < theme.length; i++) if (theme[i].key.indexOf('font') >= 0 && theme[i].value === id) return 'theme:' + theme[i].key;
    return id;
  }

  // ----- Discovery -----
  function hiddenForGood(el) {
    var cs = getComputedStyle(el);
    return cs.display === 'none' || cs.visibility === 'hidden' || (el.offsetWidth === 0 && el.offsetHeight === 0 && !(el instanceof SVGElement));
  }
  function inlineish(el) {
    var d = getComputedStyle(el).display;
    return d === 'inline' || d === 'contents';
  }
  function blockOf(node) {
    var el = node.nodeType === 3 ? node.parentElement : node;
    while (el && el !== document.body && inlineish(el) && !el.hasAttribute('data-var')) el = el.parentElement;
    if (el && el.hasAttribute && el.hasAttribute('data-var') && inlineish(el)) {
      var up = el.parentElement;
      while (up && up !== document.body && inlineish(up)) up = up.parentElement;
      return up;
    }
    return el;
  }

  function discover() {
    var found = [], taken = new Set();
    function add(kind, el, extra) { if (!taken.has(el)) { taken.add(el); found.push(Object.assign({ kind: kind, el: el }, extra || {})); } }

    document.querySelectorAll('[data-dress-colors]').forEach(function (el) { add('dress', el); });
    document.querySelectorAll('[data-href]').forEach(function (el) {
      var path = el.getAttribute('data-href');
      if (path === 'rsvp.link') add('rsvp', el);
      else if (path === 'camera.link' || path === 'photos.link' || path === 'event.venue.mapLink' || /^event\.[A-Za-z0-9]+$/.test(path)) add('link', el, { path: path });
    });
    document.querySelectorAll('img').forEach(function (el) {
      if (el.closest('[data-href], [data-dress-colors]') || el.hasAttribute('data-gallery-clone')) return;
      var ics = getComputedStyle(el);
      if (parseFloat(ics.borderTopWidth) > 0 && ics.borderTopStyle !== 'none' && hex(ics.borderTopColor))
        found.push({ kind: 'box', el: el, bg: fillOf(ics), border: hex(ics.borderTopColor), gradient: null, picture: null, behindText: true });
      if (el.hasAttribute('data-src')) add('slot', el); else add('image', el);
    });
    document.querySelectorAll('svg').forEach(function (el) {
      if (el.closest('[data-href], [data-dress-colors]') || el.parentElement.closest('svg')) return;
      if (el.getBoundingClientRect().width < 4) return;
      add('svg', el);
    });

    // Text: every block that directly holds text or a binding.
    var walker = document.createTreeWalker(document.body, NodeFilter.SHOW_TEXT | NodeFilter.SHOW_ELEMENT);
    var blocks = new Set();
    for (var n = walker.nextNode(); n; n = walker.nextNode()) {
      if (n.nodeType === 1) {
        if (n.tagName === 'SCRIPT' || n.tagName === 'STYLE' || n.tagName === 'NOSCRIPT') { continue; }
        if (!n.hasAttribute('data-var')) continue;
      } else if (!n.textContent.trim()) continue;
      var host = n.nodeType === 1 ? n : n.parentElement;
      if (!host || host.closest('script, style, noscript, svg, [data-href], [data-dress-colors]')) continue;
      var b = blockOf(n);
      if (b && b !== document.body && b !== document.documentElement) blocks.add(b);
    }
    var textBlocks = blocks;
    blocks.forEach(function (b) { add('text', b); });

    // Filled boxes: backgrounds, borders, background pictures — on elements and on their ::before/::after.
    function backgroundItem(el, cs, pseudo) {
      var uri = svgDataUrl(cs.backgroundImage);
      if (!uri) return false;
      var tileW = parseFloat((cs.backgroundSize || '').split(' ')[0]), tileH = parseFloat((cs.backgroundSize || '').split(' ')[1]);
      found.push({ kind: 'bgsvg', el: el, pseudo: pseudo, uri: uri, repeat: cs.backgroundRepeat, tileW: tileW, tileH: tileH });
      return true;
    }
    document.querySelectorAll('body *').forEach(function (el) {
      if (el.closest('svg, script, style, [data-gallery-clone]')) return;
      ['::before', '::after'].forEach(function (pseudo) {
        var ps = getComputedStyle(el, pseudo);
        if (!ps.content || ps.content === 'none' || ps.display === 'none') return;
        if (ps.position !== 'absolute' && ps.position !== 'fixed') return;
        if (backgroundItem(el, ps, pseudo)) return;
        var pbg = fillOf(ps), picture = /url\(["']?data:image\/(png|jpeg|webp|gif);base64,/.test(ps.backgroundImage) ? ps.backgroundImage : null;
        if (!pbg && !picture) return;
        found.push({ kind: picture ? 'bgimage' : 'box', el: el, pseudo: pseudo, bg: pbg, border: null, gradient: null, picture: picture });
      });
      // Text blocks keep their box (a pill, a seal) as a separate shape behind the words.
      if (taken.has(el) && !textBlocks.has(el)) return;
      var cs = getComputedStyle(el);
      if (backgroundItem(el, cs, null)) { taken.add(el); return; }
      // A single side's border (an accent bar, a divider) is a thin rule of its own.
      ['Left', 'Right', 'Top', 'Bottom'].forEach(function (side) {
        var bw = parseFloat(cs['border' + side + 'Width']) || 0, style = cs['border' + side + 'Style'], color = hex(cs['border' + side + 'Color']);
        if (bw <= 0 || style === 'none' || !color) return;
        var all = ['Left', 'Right', 'Top', 'Bottom'].every(function (o) { return (parseFloat(cs['border' + o + 'Width']) || 0) > 0 && cs['border' + o + 'Style'] !== 'none'; });
        if (all) return;
        found.push({ kind: 'rule', el: el, side: side, width: bw, color: color, behindText: true });
      });
      var bg = fillOf(cs), border = parseFloat(cs.borderTopWidth) > 0 && cs.borderTopStyle !== 'none' && parseFloat(cs.borderLeftWidth) > 0 ? hex(cs.borderTopColor) : null;
      var gradient = /gradient\(/.test(cs.backgroundImage) ? cs.backgroundImage : null;
      var picture = /url\(["']?data:image\/(png|jpeg|webp|gif);base64,/.test(cs.backgroundImage) ? cs.backgroundImage : null;
      if (!bg && !border && !gradient && !picture) return;
      if (el.offsetWidth * el.offsetHeight < 36) return;
      if (taken.has(el)) { found.push({ kind: picture ? 'bgimage' : 'box', el: el, bg: bg, border: border, gradient: gradient, picture: picture, behindText: true }); return; }
      add(picture ? 'bgimage' : 'box', el, { bg: bg, border: border, gradient: gradient, picture: picture });
    });

    // Paint order: the stacking level of the nearest positioned ancestor with a z-index (a fixed
    // decoration above the page stays above it), then document order, boxes behind what they hold.
    var order = new Map(); var i = 0;
    document.querySelectorAll('*').forEach(function (el) { order.set(el, i++); });
    function level(el) {
      for (var n = el; n && n !== document.documentElement; n = n.parentElement) {
        var cs = getComputedStyle(n), z = parseInt(cs.zIndex, 10);
        if (cs.position !== 'static' && isFinite(z)) return z;
      }
      return 0;
    }
    found.forEach(function (f) { f.level = level(f.el); });
    found.sort(function (a, b) {
      if (a.level !== b.level) return a.level - b.level;
      if (a.el.contains(b.el) && a.el !== b.el) return -1;
      if (b.el.contains(a.el) && a.el !== b.el) return 1;
      if (a.el === b.el) {
        var rank = function (x) { return x.pseudo === '::before' ? -2 : x.behindText ? -1 : x.pseudo === '::after' ? 1 : 0; };
        return rank(a) - rank(b);
      }
      return order.get(a.el) - order.get(b.el);
    });
    return found;
  }

  // ----- Measuring -----
  function measurePseudo(el, pseudo) {
    var host = measure(el);
    if (!host) return null;
    var ps = getComputedStyle(el, pseudo);
    var w = parseFloat(ps.width), h = parseFloat(ps.height), left = parseFloat(ps.left), top = parseFloat(ps.top);
    if (!isFinite(w) || !isFinite(h) || w <= 0 || h <= 0) return null;
    if (!isFinite(left)) left = 0;
    if (!isFinite(top)) top = 0;
    var fixed = ps.position === 'fixed';
    var pscale = 1;
    if (ps.transform && ps.transform !== 'none') { try { var pm = new DOMMatrixReadOnly(ps.transform); pscale = Math.sqrt(pm.a * pm.a + pm.b * pm.b) * Math.sqrt(pm.c * pm.c + pm.d * pm.d); } catch (e) {} }
    if (pscale < 0.02) return { x: host.x, y: host.y, w: 1, h: 1, rotate: 0, scale: 1, opacity: 0 };
    return { x: fixed ? left : host.x + left, y: fixed ? top + window.pageYOffset : host.y + top, w: w, h: h,
      rotate: host.rotate, scale: host.scale, opacity: host.opacity * (parseFloat(ps.opacity) || 1) };
  }

  function measure(el) {
    var r = el.getBoundingClientRect();
    if (r.width === 0 && r.height === 0) return null;
    var angle = 0, scale = 1, opacity = 1, node = el;
    while (node && node.nodeType === 1) {
      var cs = getComputedStyle(node);
      opacity *= parseFloat(cs.opacity);
      if (cs.visibility === 'hidden' || cs.display === 'none') opacity = 0;
      if (cs.transform && cs.transform !== 'none') {
        try { var m = new DOMMatrixReadOnly(cs.transform); angle += Math.atan2(m.b, m.a) * 180 / Math.PI; scale *= Math.sqrt(m.a * m.a + m.b * m.b); } catch (e) {}
      }
      node = node.parentElement;
    }
    var w = el.offsetWidth || r.width, h = el.offsetHeight || r.height;
    if (el instanceof SVGElement) { var bb = el.getBoundingClientRect(); w = bb.width / scale; h = bb.height / scale; }
    var cx = r.left + r.width / 2, cy = r.top + r.height / 2 + window.pageYOffset;
    return { x: cx - w / 2, y: cy - h / 2, w: w, h: h, rotate: ((angle % 360) + 540) % 360 - 180, scale: scale, opacity: opacity };
  }

  function measureRule(item) {
    var host = measure(item.el);
    if (!host) return null;
    var bw = item.width;
    if (item.side === 'Left') return Object.assign({}, host, { w: bw });
    if (item.side === 'Right') return Object.assign({}, host, { x: host.x + host.w - bw, w: bw });
    if (item.side === 'Top') return Object.assign({}, host, { h: bw });
    return Object.assign({}, host, { y: host.y + host.h - bw, h: bw });
  }

  // A gallery is its prints together: the union of the original and every clone of it.
  function measureGallery(el) {
    var group = el.getAttribute('data-gallery-of');
    var prints = [el].concat([].slice.call(document.querySelectorAll('[data-gallery-clone="' + group + '"]')));
    var l = Infinity, t = Infinity, r = -Infinity, b = -Infinity, op = 0;
    prints.forEach(function (p) { var m = measure(p); if (!m) return; l = Math.min(l, m.x); t = Math.min(t, m.y); r = Math.max(r, m.x + m.w); b = Math.max(b, m.y + m.h); op = Math.max(op, m.opacity); });
    if (!isFinite(l)) return null;
    return { x: l, y: t, w: r - l, h: b - t, rotate: 0, scale: 1, opacity: op };
  }

  function simplify(points, keys, tol) {
    if (points.length <= 2) return points.slice();
    function dist(p, a, b) {
      var span = b.s - a.s || 1, t = (p.s - a.s) / span, worst = 0;
      for (var k = 0; k < keys.length; k++) {
        var key = keys[k], expected = a.v[key] + (b.v[key] - a.v[key]) * t;
        worst = Math.max(worst, Math.abs(p.v[key] - expected) / tol[key]);
      }
      return worst;
    }
    var maxD = 0, idx = 0;
    for (var i = 1; i < points.length - 1; i++) { var d = dist(points[i], points[0], points[points.length - 1]); if (d > maxD) { maxD = d; idx = i; } }
    if (maxD <= 1) return [points[0], points[points.length - 1]];
    var left = simplify(points.slice(0, idx + 1), keys, tol), right = simplify(points.slice(idx), keys, tol);
    return left.slice(0, -1).concat(right);
  }

  // ----- Building -----
  function runsOf(block) {
    var runs = [];
    if (block.hasAttribute('data-var')) return [{ var: block.getAttribute('data-var') }];
    function walk(node, bold, italic) {
      if (node.nodeType === 3) { var t = node.textContent.replace(/\s+/g, ' '); if (t) runs.push({ text: t, bold: bold, italic: italic }); return; }
      if (node.nodeType !== 1) return;
      if (node.tagName === 'BR') { runs.push({ text: '\n', bold: false, italic: false }); return; }
      if (node !== block && !inlineish(node) && !node.hasAttribute('data-var')) return;
      var cs = getComputedStyle(node);
      if (cs.display === 'none') return;
      // Words split across spans and kept apart by margin, not by a space character.
      if (node !== block && runs.length && (parseFloat(cs.marginLeft) > 0 || parseFloat(cs.paddingLeft) > 0)) {
        var prev = runs[runs.length - 1];
        if (prev.text !== undefined && !/\s$/.test(prev.text)) runs.push({ text: ' ', bold: false, italic: false });
      }
      if (node.hasAttribute('data-var')) { runs.push({ var: node.getAttribute('data-var'), bold: bold, italic: italic }); return; }
      var b = bold || node.tagName === 'B' || node.tagName === 'STRONG';
      var i = italic || node.tagName === 'I' || node.tagName === 'EM';
      node.childNodes.forEach(function (c) { walk(c, b, i); });
    }
    block.childNodes.forEach(function (c) { walk(c, false, false); });
    if (runs.length) { runs[0].text !== undefined && (runs[0].text = runs[0].text.replace(/^\s+/, '')); var last = runs[runs.length - 1]; last.text !== undefined && (last.text = last.text.replace(/\s+$/, '')); }
    return runs.filter(function (r) { return r.var || r.text; }).map(function (r) {
      var o = r.var ? { var: r.var } : { text: r.text };
      if (r.bold) o.bold = true; if (r.italic) o.italic = true; return o;
    });
  }

  function typography(theme, el) {
    var cs = getComputedStyle(el), size = parseFloat(cs.fontSize) || 16;
    var lh = cs.lineHeight === 'normal' ? 1.25 : (parseFloat(cs.lineHeight) || size * 1.25) / size;
    var ls = cs.letterSpacing === 'normal' ? 0 : (parseFloat(cs.letterSpacing) || 0) / size;
    var align = cs.textAlign === 'center' ? 'center' : (cs.textAlign === 'right' || cs.textAlign === 'end') ? 'right' : 'left';
    return { font: fontRef(theme, cs.fontFamily), size: round(size), weight: Math.round((parseInt(cs.fontWeight, 10) || 400) / 100) * 100,
      italic: cs.fontStyle === 'italic', color: colorRef(theme, textColorOf(el)) || 'theme:text', align: align, valign: 'top',
      lineHeight: round(Math.max(0.6, Math.min(4, lh)), 2), letterSpacing: round(Math.max(-0.2, Math.min(2, ls)), 3), uppercase: cs.textTransform === 'uppercase' };
  }

  // A CSS filter (a colour shift, a drop shadow) can't be expressed in a scene, so it's painted into
  // the picture itself.
  async function bakeFilter(src, filter, w, h) {
    if (!filter || filter === 'none') return src;
    try {
      var img = new Image();
      img.src = src;
      await img.decode();
      var scale = Math.min(2, Math.max(1, Math.ceil((w || img.naturalWidth) * 2 / Math.max(1, img.naturalWidth))));
      var cw = Math.min(1200, img.naturalWidth * scale), ch = Math.round(cw * img.naturalHeight / img.naturalWidth);
      var pad = /drop-shadow/.test(filter) ? Math.round(cw * 0.15) : 0;
      var c = document.createElement('canvas'); c.width = cw + pad * 2; c.height = ch + pad * 2;
      var g = c.getContext('2d'); g.filter = filter; g.drawImage(img, pad, pad, cw, ch);
      var out = c.toDataURL('image/webp', 0.85);
      return out.indexOf('data:image/webp') === 0 ? out : c.toDataURL('image/png');
    } catch (e) { return src; }
  }

  // A marquee repeats its words so the loop never shows a gap; one copy is the content.
  function collapseRepeats(text) {
    var t = text.replace(/\s+/g, ' ').trim();
    for (var parts = 2; parts <= 8; parts++) {
      if (t.length % parts !== 0 && t.replace(/ /g, '').length % parts !== 0) continue;
      var squeezed = t.replace(/ /g, ''), size = squeezed.length / parts;
      if (size !== Math.floor(size) || size < 2) continue;
      var unit = squeezed.slice(0, size), ok = true;
      for (var i = 1; i < parts; i++) if (squeezed.slice(i * size, (i + 1) * size) !== unit) { ok = false; break; }
      if (ok) {
        // Give back the spacing of the first copy.
        var words = t.split(' '), out = '', count = 0;
        for (var w = 0; w < words.length && count < size; w++) { out += (out ? ' ' : '') + words[w]; count += words[w].length; }
        return out;
      }
    }
    return t;
  }
  function textColorOf(node) {
    var walker = document.createTreeWalker(node, NodeFilter.SHOW_TEXT);
    for (var t = walker.nextNode(); t; t = walker.nextNode()) {
      if (!t.textContent.trim()) continue;
      var cs = getComputedStyle(t.parentElement);
      if (cs.display === 'none' || cs.visibility === 'hidden') continue;
      return strokeAwareColor(cs);
    }
    return strokeAwareColor(getComputedStyle(node));
  }
  // Outlined text (a transparent fill with a stroke) keeps the stroke's colour, softened.
  function strokeAwareColor(cs) {
    var fill = cs.webkitTextFillColor || cs.getPropertyValue('-webkit-text-fill-color');
    var strokeWidth = parseFloat(cs.webkitTextStrokeWidth || cs.getPropertyValue('-webkit-text-stroke-width')) || 0;
    if (strokeWidth > 0 && (!hex(fill))) {
      var stroke = hex(cs.webkitTextStrokeColor || cs.getPropertyValue('-webkit-text-stroke-color'));
      if (stroke) return stroke.slice(0, 7) + '66';
    }
    return hex(cs.color);
  }

  // A computed background-image is serialised as url("…") with inner quotes escaped, so an SVG that
  // uses quotes of either kind (xmlns='…') has to be read to the closing quote, not to the first one.
  function svgDataUrl(value) {
    var m = /url\(\s*(?:"((?:[^"\\]|\\.)*)"|'((?:[^'\\]|\\.)*)'|([^"')\s][^)\s]*))\s*\)/.exec(value || '');
    if (!m) return null;
    var raw = m[1] != null ? m[1] : m[2] != null ? m[2] : m[3];
    raw = raw.replace(/\\(.)/g, '$1');
    return /^data:image\/svg\+xml/.test(raw) ? raw : null;
  }

  function validSvg(markup) {
    try {
      var doc = new DOMParser().parseFromString(markup, 'image/svg+xml');
      return !doc.getElementsByTagNameNS('*', 'parsererror').length && doc.documentElement && doc.documentElement.localName === 'svg';
    } catch (e) { return false; }
  }

  function radiusOf(cs, w, h) {
    var r = cs.borderTopLeftRadius || '0';
    if (/%/.test(r)) return parseFloat(r) >= 50 ? Math.min(w, h) / 2 : Math.min(w, h) * parseFloat(r) / 100;
    return parseFloat(r) || 0;
  }

  function slug(s) { return (s || '').replace(/[^A-Za-z0-9]+/g, '-').replace(/^-|-$/g, '').slice(0, 30) || 'x'; }

  async function run() {
    progress('Loading the template…');
    try { await document.fonts.ready; } catch (e) {}
    await wait(600);

    // Freeze time-based motion; keep scroll-driven motion live.
    var calm = document.createElement('style');
    calm.textContent = '*,*::before,*::after{transition-duration:0s!important;transition-delay:0s!important;scroll-behavior:auto!important}';
    document.head.appendChild(calm);
    function freeze() {
      if (!document.getAnimations) return;
      document.getAnimations().forEach(function (a) {
        try {
          if (a.timeline && a.timeline !== document.timeline && a.timeline.constructor.name !== 'DocumentTimeline') return;
          // An intro that plays once is shown finished — its end is how the page is meant to look.
          // Something that loops forever (a bouncing arrow) is held at its start.
          var end = a.effect && a.effect.getComputedTiming ? a.effect.getComputedTiming().endTime : Infinity;
          if (isFinite(end)) a.finish(); else { a.currentTime = 0; a.pause(); }
        } catch (e) {}
      });
    }

    var theme = readTheme();
    var height = Math.max(document.documentElement.scrollHeight, document.body.scrollHeight, VIEW);
    var range = Math.max(0, height - window.innerHeight);
    var found = discover();
    var notes = [];
    if (found.length > 280) { notes.push('Only the first 280 pieces were kept.'); found = found.slice(0, 280); }

    var step = Math.max(40, Math.ceil(range / 70));
    var positions = [];
    for (var s = 0; s <= range; s += step) positions.push(s);
    if (positions[positions.length - 1] !== range) positions.push(range);

    var samples = found.map(function () { return []; });
    for (var p = 0; p < positions.length; p++) {
      window.scrollTo(0, positions[p]);
      // A frame nobody can see may get its scroll events late; the template's own handlers (reveal on
      // scroll, progress) are told directly so they run before anything is measured.
      try { window.dispatchEvent(new Event('scroll')); document.dispatchEvent(new Event('scroll')); } catch (e) {}
      await frame();
      freeze();
      if (p % 6 === 0) progress('Following the motion… ' + Math.round(p / positions.length * 100) + '%');
      for (var f = 0; f < found.length; f++) {
        var m = found[f].kind === 'rule' ? measureRule(found[f]) : found[f].pseudo ? measurePseudo(found[f].el, found[f].pseudo) : found[f].kind === 'slot' && found[f].el.hasAttribute('data-gallery-of') ? measureGallery(found[f].el) : measure(found[f].el);
        samples[f].push({ s: Math.round(positions[p] * (W / window.innerWidth)), v: m });
      }
    }
    window.scrollTo(0, 0);
    progress('Building the design…');

    var elements = [], assets = {}, assetCount = 0, skipped = 0, assetByData = {};
    // The same picture used many times (a rose on every leaf of a vine) is embedded once.
    function addAsset(prefix, asset) {
      if (assetByData[asset.data]) return assetByData[asset.data];
      var id = prefix + (assetCount++);
      assets[id] = asset;
      assetByData[asset.data] = id;
      return id;
    }
    for (var e = 0; e < found.length; e++) {
      var item = found[e], pts = samples[e].filter(function (x) { return x.v; });
      if (!pts.length || pts.every(function (x) { return x.v.opacity < 0.02; })) { skipped++; continue; }
      // Resting state: the first moment it's visible.
      var rest = (pts.find(function (x) { return x.v.opacity > 0.5; }) || pts[0]).v;
      var el = { id: 'i' + e, type: 'text', name: null, x: round(rest.x), y: round(rest.y), w: round(Math.max(1, rest.w)), h: round(Math.max(1, rest.h)),
        rotate: round(rest.rotate), scale: round(rest.scale, 3), opacity: round(rest.opacity, 2), track: null, keyframes: [] };
      var node = item.el, cs = item.pseudo ? getComputedStyle(node, item.pseudo) : getComputedStyle(node);
      var block = node.closest('[data-block]');
      if (block) el.block = block.getAttribute('data-block');

      if (item.kind === 'text') {
        el.type = 'text';
        var runs = runsOf(node);
        if (!runs.length) { skipped++; continue; }
        if (runs.every(function (r) { return r.text !== undefined; })) {
          var joined = runs.map(function (r) { return r.text; }).join('');
          var single = collapseRepeats(joined);
          if (single.length < joined.replace(/\s+/g, ' ').trim().length) runs = [{ text: single }];
        }
        el.text = { runs: runs, style: typography(theme, node) };
        if (cs.whiteSpace === 'nowrap' || cs.whiteSpace === 'pre' || node.scrollWidth > node.clientWidth + 4) {
          // Text that never wraps (a marquee, a single long line) keeps one line; the page clips it.
          el.w = round(Math.min(W * 4, Math.max(el.w, node.scrollWidth)));
          el.text.style.lineHeight = Math.max(el.text.style.lineHeight, 1);
          el.h = round(Math.max(el.h, el.text.style.size * el.text.style.lineHeight));
        }
        // The replacement font can be wider than what the template used. A line that fit on one line
        // gets room to still fit, growing from its alignment edge.
        var lineBox = el.text.style.size * el.text.style.lineHeight;
        if (rest.h < lineBox * 1.6 && el.w < W - 8) {
          var grown = Math.min(W - 8, Math.max(el.w, el.w * 1.4 + 16));
          if (el.text.style.align === 'center') el.x = round(el.x - (grown - el.w) / 2);
          else if (el.text.style.align === 'right') el.x = round(el.x - (grown - el.w));
          el.x = Math.max(4 - 0, Math.min(el.x, W - grown - 4));
          item.dx = el.x - round(rest.x);
          el.w = round(grown);
        }
        var first = runs[0].var ? runs[0].var : runs.map(function (r) { return r.text || ''; }).join('').slice(0, 24);
        el.name = first;
      } else if (item.kind === 'rsvp' || item.kind === 'link') {
        el.type = item.kind;
        var bw = parseFloat(cs.borderTopWidth) || 0;
        var labelText = (node.innerText || node.textContent || 'Open').replace(/\s+/g, ' ').trim();
        // Hover effects often print the label twice (one copy slides in); keep one.
        labelText = collapseRepeats(labelText);
        el.button = { path: item.kind === 'link' ? item.path : null, label: labelText.slice(0, 60) || 'Open',
          fill: colorRef(theme, fillOf(cs)), stroke: bw > 0 ? colorRef(theme, hex(cs.borderTopColor)) : null, strokeWidth: round(bw),
          radius: round(parseFloat(cs.borderTopLeftRadius) || 0), style: typography(theme, node) };
        el.button.style.align = 'center'; el.button.style.valign = 'middle';
        el.name = item.kind === 'rsvp' ? 'RSVP button' : el.button.label;
      } else if (item.kind === 'dress') {
        el.type = 'dress';
        var sw = node.querySelector('.ib-dress__swatch');
        var sws = sw ? getComputedStyle(sw) : null;
        el.dress = { swatch: sw ? round(sw.offsetWidth || 40) : 40, shape: sws && parseFloat(sws.borderTopLeftRadius) < (sw.offsetWidth || 40) / 3 ? 'square' : 'circle', gap: 10, style: typography(theme, node) };
        el.name = 'Dress colours';
      } else if (item.kind === 'slot') {
        el.type = 'slot';
        var path = node.getAttribute('data-src') || 'event.coverImage';
        var multiple = node.getAttribute('data-multiple') === 'true';
        el.slot = { path: path, label: node.getAttribute('data-slot-label') || 'Photo', fit: cs.objectFit === 'contain' ? 'contain' : 'cover',
          radius: round(radiusOf(cs, el.w, el.h)), multiple: multiple,
          min: +node.getAttribute('data-min-images') || null, max: +node.getAttribute('data-max-images') || null,
          columns: multiple ? Math.max(1, Math.min(4, Math.round(el.w / Math.max(40, node.offsetWidth)))) : 2, gap: 8,
          aspect: node.offsetHeight ? round(node.offsetWidth / node.offsetHeight, 2) : 1 };
        el.name = el.slot.label;
      } else if (item.kind === 'image' || item.kind === 'bgimage') {
        var src = item.kind === 'image' ? node.currentSrc || node.src : (/url\(["']?(data:[^"')]+)/.exec(item.picture) || [])[1];
        if (!src || !/^data:image\/(png|jpeg|webp|gif);base64,/.test(src) || src.length > 540000) {
          if (item.kind === 'bgimage' && item.bg) { item.kind = 'box'; } else { skipped++; notes.push('A picture that isn’t embedded in the template was left out.'); continue; }
        } else {
          src = await bakeFilter(src, cs.filter, el.w, el.h);
          if (src.length > 540000) { skipped++; continue; }
          var aid = addAsset('img', { kind: 'image', data: src, width: node.naturalWidth || el.w, height: node.naturalHeight || el.h, colors: null, name: 'Picture' });
          el.type = 'image';
          el.image = { asset: aid, fit: item.kind === 'image' && cs.objectFit === 'cover' ? 'cover' : (item.kind === 'bgimage' && /cover/.test(cs.backgroundSize) ? 'cover' : 'contain'), radius: round(radiusOf(cs, el.w, el.h)) };
          el.name = 'Picture';
        }
      } else if (item.kind === 'svg') {
        var clone = node.cloneNode(true);
        // <use> and url(#…) can point at definitions elsewhere on the page; bring them along.
        var needed = new Set();
        clone.querySelectorAll('use').forEach(function (u) { var ref = u.getAttribute('href') || u.getAttribute('xlink:href'); if (ref && ref[0] === '#') needed.add(ref.slice(1)); });
        (clone.outerHTML.match(/url\(#([^)]+)\)/g) || []).forEach(function (m) { needed.add(m.slice(5, -1)); });
        var defs = document.createElementNS('http://www.w3.org/2000/svg', 'defs');
        needed.forEach(function (id) { if (clone.querySelector('[id="' + id + '"]')) return; var def = document.getElementById(id); if (def) defs.appendChild(def.cloneNode(true)); });
        if (defs.childNodes.length) clone.insertBefore(defs, clone.firstChild);
        // XMLSerializer, not outerHTML: HTML serialisation drops namespace declarations (xlink:href
        // without xmlns:xlink), and the server reads SVG as XML.
        var markup = new XMLSerializer().serializeToString(clone);
        if (markup.indexOf('xmlns=') < 0) markup = markup.replace('<svg', '<svg xmlns="http://www.w3.org/2000/svg"');
        if (!/viewBox/.test(markup)) { var bb = node.getBBox ? node.getBBox() : null; markup = markup.replace('<svg', '<svg viewBox="0 0 ' + round(el.w) + ' ' + round(el.h) + '"'); }
        // Inherited paint (currentColor) would be lost once the SVG leaves its page.
        markup = markup.replace(/currentColor/g, hex(cs.color) || '#000000');
        if (markup.length > 200000) { skipped++; notes.push('A very large illustration was left out.'); continue; }
        if (!validSvg(markup)) { skipped++; notes.push('An illustration that couldn\u2019t be read was left out.'); continue; }
        var sid = addAsset('svg', { kind: 'svg', data: markup, width: el.w, height: el.h, colors: null, name: 'Illustration' });
        el.type = 'svg';
        el.svg = { asset: sid, fills: {} };
        el.name = 'Illustration';
      }
      if (item.kind === 'bgsvg') {
        var svgText = item.uri.replace(/^data:image\/svg\+xml(;charset=[^,;]+)?(;base64)?,/, '');
        try { svgText = /;base64,/.test(item.uri) ? atob(svgText) : decodeURIComponent(svgText); } catch (e) { skipped++; continue; }
        if (svgText.length > 200000 || !validSvg(svgText)) { skipped++; continue; }
        var tw = isFinite(item.tileW) && item.tileW > 0 ? item.tileW : el.w, th = isFinite(item.tileH) && item.tileH > 0 ? item.tileH : el.h;
        if (cs.filter && cs.filter !== 'none') {
          var baked = await bakeFilter('data:image/svg+xml;charset=utf-8,' + encodeURIComponent(svgText), cs.filter, tw, th);
          if (/^data:image\/(webp|png)/.test(baked) && baked.length < 540000) {
            var pid = addAsset('img', { kind: 'image', data: baked, width: tw, height: th, colors: null, name: 'Picture' });
            el.type = 'image'; el.image = { asset: pid, fit: /cover/.test(cs.backgroundSize) ? 'cover' : 'contain', radius: 0 }; el.name = 'Picture';
          } else { skipped++; continue; }
        } else {
          var bid = addAsset('svg', { kind: 'svg', data: svgText, width: tw, height: th, colors: null, name: 'Pattern' });
          el.type = 'svg'; el.svg = { asset: bid, fills: {} }; el.name = 'Pattern';
        }
        item.tiles = null;
        if (/repeat/.test(item.repeat) && item.repeat !== 'no-repeat') {
          var countY = /repeat-x/.test(item.repeat) ? 1 : Math.min(40, Math.max(1, Math.ceil(el.h / th)));
          var countX = /repeat-y/.test(item.repeat) ? 1 : Math.min(12, Math.max(1, Math.ceil(el.w / tw)));
          item.tiles = { cx: countX, cy: countY, w: tw, h: th };
        }
        el.w = round(item.tiles ? tw : el.w); el.h = round(item.tiles ? th : el.h);
      }
      if (item.kind === 'rule') {
        el.type = 'shape';
        el.shape = { kind: 'rect', sides: 6, fill: colorRef(theme, item.color), stroke: null, strokeWidth: 0, radius: 0 };
        el.name = 'Rule';
      }
      if (item.kind === 'box') {
        el.type = 'shape';
        var fill = item.bg || (item.gradient ? hex((/(rgba?\([^)]+\)|#[0-9a-f]{3,8})/i.exec(item.gradient) || [])[1]) : null);
        var radius = radiusOf(cs, el.w, el.h);
        var round50 = /%/.test(cs.borderTopLeftRadius) && parseFloat(cs.borderTopLeftRadius) >= 50;
        el.shape = { kind: round50 ? 'ellipse' : 'rect', sides: 6, fill: colorRef(theme, fill), stroke: item.border ? colorRef(theme, item.border) : null,
          strokeWidth: item.border ? round(parseFloat(cs.borderTopWidth)) : 0, radius: round(radius) };
        el.name = 'Box';
      }

      // Motion: keyframes from the samples, simplified.
      var keys = ['x', 'y', 'rotate', 'scale', 'opacity'], tol = { x: 2, y: 2, rotate: 2, scale: 0.02, opacity: 0.06 };
      var series = pts.map(function (x) { return { s: x.s, v: x.v }; });
      var moving = series.some(function (x) { return keys.some(function (k) { return Math.abs(x.v[k] - series[0].v[k]) > tol[k]; }); });
      if (moving) {
        var kept = simplify(series, keys, tol);
        // Trim still ends so the track covers only the movement.
        var firstMove = 0, lastMove = kept.length - 1;
        while (firstMove < kept.length - 1 && keys.every(function (k) { return Math.abs(kept[firstMove + 1].v[k] - kept[firstMove].v[k]) <= tol[k]; })) firstMove++;
        while (lastMove > 0 && keys.every(function (k) { return Math.abs(kept[lastMove - 1].v[k] - kept[lastMove].v[k]) <= tol[k]; })) lastMove--;
        kept = kept.slice(firstMove, lastMove + 1);
        if (kept.length > 24) { var stride = Math.ceil(kept.length / 24); kept = kept.filter(function (x, i) { return i % stride === 0 || i === kept.length - 1; }); }
        if (kept.length >= 2) {
          var start = kept[0].s, end = kept[kept.length - 1].s;
          if (end > start) {
            el.track = { start: start, end: end };
            el.keyframes = kept.map(function (x) {
              return { t: round((x.s - start) / (end - start), 4), x: round(x.v.x), y: round(x.v.y), rotate: round(x.v.rotate), scale: round(x.v.scale, 3), opacity: round(Math.max(0, Math.min(1, x.v.opacity)), 2) };
            });
            if (item.dx) el.keyframes.forEach(function (k) { k.x = round(k.x + item.dx); });
            var r0 = el.keyframes[0];
            el.x = r0.x; el.y = r0.y; el.rotate = r0.rotate; el.scale = r0.scale; el.opacity = r0.opacity;
          }
        }
      }
      if (el.opacity < 0.02 && !el.keyframes.length) { skipped++; continue; }
      el.opacity = Math.max(0, Math.min(1, el.opacity));
      if (item.tiles && item.tiles.cx * item.tiles.cy > 1) {
        // A repeating background becomes a group of tiles that move together.
        var tiles = [];
        for (var ty = 0; ty < item.tiles.cy; ty++) for (var tx = 0; tx < item.tiles.cx; tx++)
          tiles.push({ id: el.id + 't' + tx + '_' + ty, type: 'svg', name: 'Tile', x: tx * item.tiles.w, y: ty * item.tiles.h, w: item.tiles.w, h: item.tiles.h,
            rotate: 0, scale: 1, opacity: 1, track: null, keyframes: [],
            svg: el.svg ? { asset: el.svg.asset, fills: {} } : null, image: el.image ? Object.assign({}, el.image) : null, type: el.type });
        var group = Object.assign({}, el, { type: 'group', name: 'Pattern', svg: null, image: null, w: item.tiles.w * item.tiles.cx, h: item.tiles.h * item.tiles.cy, children: tiles });
        elements.push(group);
        continue;
      }
      elements.push(el);
    }

    if (!elements.some(function (x) { return x.type === 'rsvp'; })) {
      notes.push('The template had no RSVP button, so one was added at the end — every invitation needs one.');
      elements.push({ id: 'rsvp', type: 'rsvp', name: 'RSVP button', x: 85, y: Math.max(0, height - 200), w: 220, h: 54, rotate: 0, scale: 1, opacity: 1, track: null, keyframes: [],
        button: { path: null, label: 'Reply now', fill: 'theme:accent', stroke: null, strokeWidth: 0, radius: 999,
          style: { font: theme.some(function (t) { return t.key === 'body-font'; }) ? 'theme:body-font' : 'inter', size: 17, weight: 600, italic: false, color: 'theme:bg', align: 'center', valign: 'middle', lineHeight: 1.3, letterSpacing: 0, uppercase: false } } });
    }

    // Phone-height screens, the last one taking what's left (never shorter than the minimum).
    var sections = [], left = Math.max(height, VIEW), n = 1;
    while (left > 0 && n <= 30) {
      var hh = left >= VIEW * 2 || n === 30 ? Math.min(left, n === 30 ? 6000 : VIEW) : left;
      sections.push({ id: 's' + n, name: 'Screen ' + n, height: Math.round(Math.max(200, hh)), background: null });
      left -= hh; n++;
    }

    var roles = []; var meta = document.querySelector('meta[name="ib-roles"]');
    if (meta) roles = meta.getAttribute('content').split(',').map(function (r) { return r.trim(); }).filter(Boolean);
    var fonts = []; theme.forEach(function (t) { if (t.key.indexOf('font') >= 0 && fonts.indexOf(t.value) < 0) fonts.push(t.value); });

    var fields = [], seenField = {};
    document.querySelectorAll('[data-var], [data-href], [data-src]').forEach(function (node) {
      var path = node.getAttribute('data-var') || node.getAttribute('data-href') || node.getAttribute('data-src');
      if (!/^event\.[A-Za-z][A-Za-z0-9]*$/.test(path) || seenField[path]) return;
      var known = ['event.title', 'event.subtitle', 'event.description', 'event.date', 'event.time', 'event.schedule', 'event.dressCode', 'event.hashtag', 'event.coverImage', 'event.couplePhoto', 'event.gallery'];
      if (known.indexOf(path) >= 0 || node.hasAttribute('data-src')) return;
      seenField[path] = 1;
      var type = node.getAttribute('data-type') || node.getAttribute('data-field-type') || (node.hasAttribute('data-href') ? 'url' : 'text');
      if (['text', 'textarea', 'date', 'time', 'url', 'color', 'select'].indexOf(type) < 0) type = 'text';
      var options = (node.getAttribute('data-options') || '').split(',').map(function (o) { return o.trim(); }).filter(Boolean);
      fields.push({ path: path, label: node.getAttribute('data-field-label') || label(path.slice(6).replace(/([A-Z])/g, '-$1').toLowerCase()), type: type,
        options: type === 'select' ? (options.length ? options : ['Option']) : null, roleScope: node.getAttribute('data-role-scope') || null, sample: null });
    });

    var scene = { schema: 2, canvas: { sections: sections }, theme: theme, fonts: fonts, roles: roles, fields: fields, elements: elements, assets: assets };
    post({ type: 'ib:import', scene: scene, report: { elements: elements.length, skipped: skipped, notes: notes.filter(function (v, i, a) { return a.indexOf(v) === i; }) } });
  }

  function start() { run().catch(function (e) { post({ type: 'ib:import', error: 'The template could not be read: ' + (e && e.message ? e.message : e) }); }); }
  if (document.readyState === 'complete') setTimeout(start, 50); else addEventListener('load', function () { setTimeout(start, 50); });
})();
""";
}
