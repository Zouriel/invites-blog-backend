/*
 * The event camera (§5).
 *
 * Runs on the guest photo page, which is an ordinary same-origin document — NOT the invitation. The
 * invitation is sandboxed into an opaque origin with `default-src 'none'`, so it can neither hold a
 * camera permission nor upload anything; it links here instead.
 *
 * The shape that matters: a shutter press must never wait for a network. Capture writes a blob to
 * IndexedDB and returns; a separate pump drains that store. So the queue survives a reload, a locked
 * phone, or the venue's wifi going away mid-party, and the only thing the shutter is waiting for is
 * the sensor.
 */
(() => {
  'use strict';

  const cfg = window.__ibCamera;
  if (!cfg) return;

  const $ = (id) => document.getElementById(id);
  const video = $('cam');
  const strip = $('queue');
  const shutter = $('shoot');

  // ---- filters -------------------------------------------------------------------------------
  // Every one is expressible as a CSS filter for the preview and as the same string on a canvas at
  // capture, so the photograph matches what was on screen. Kept to colour grades: anything that
  // tracks a face needs a model per frame, which is a different product and a hot phone.
  const FILTERS = [
    { name: 'None', css: 'none' },
    { name: 'Warm', css: 'saturate(1.25) sepia(0.18) contrast(1.05)' },
    { name: 'Cool', css: 'saturate(1.1) hue-rotate(-12deg) brightness(1.04)' },
    { name: 'Film', css: 'contrast(1.18) saturate(0.85) sepia(0.12) brightness(1.02)' },
    { name: 'Mono', css: 'grayscale(1) contrast(1.12)' },
    { name: 'Faded', css: 'contrast(0.88) saturate(0.8) brightness(1.1)' },
    { name: 'Punch', css: 'contrast(1.3) saturate(1.5)' },
  ];
  let filterIndex = 0;

  // ---- camera --------------------------------------------------------------------------------
  let stream = null;
  let track = null;
  let facing = 'environment';
  let torchOn = false;

  const canvasFilterWorks = (() => {
    try {
      const c = document.createElement('canvas').getContext('2d');
      return c !== null && 'filter' in c;
    } catch {
      return false;
    }
  })();

  async function start(next) {
    stop();
    facing = next || facing;

    // Ask for far more than any sensor gives; the browser clamps to the best it actually has.
    const constraints = {
      audio: false,
      video: {
        facingMode: { ideal: facing },
        width: { ideal: 4096 },
        height: { ideal: 4096 },
      },
    };

    try {
      stream = await navigator.mediaDevices.getUserMedia(constraints);
    } catch (err) {
      fail(err);
      return;
    }

    video.srcObject = stream;
    track = stream.getVideoTracks()[0] || null;
    // A selfie preview is mirrored because that is how a mirror behaves and how every phone camera
    // shows it. What gets SAVED is mirrored to match — a portrait that flips the moment you take it
    // reads as broken, whatever the optics say.
    video.classList.toggle('mirror', facing === 'user');

    await maxOut();
    controls();
    document.body.dataset.state = 'live';
  }

  /** Push the track to the largest frame it admits to supporting. */
  async function maxOut() {
    if (!track || !track.getCapabilities) return;
    try {
      const caps = track.getCapabilities();
      if (caps.width && caps.height && caps.width.max) {
        await track.applyConstraints({
          width: { ideal: caps.width.max },
          height: { ideal: caps.height.max },
        });
      }
    } catch {
      /* A track that refuses simply keeps the resolution it negotiated. */
    }
  }

  /** Show only the controls this device actually has. Most of these are Android-only today. */
  function controls() {
    const caps = track && track.getCapabilities ? track.getCapabilities() : {};

    const zoom = $('zoom');
    if (caps.zoom && caps.zoom.max > caps.zoom.min) {
      zoom.min = caps.zoom.min;
      zoom.max = caps.zoom.max;
      zoom.step = caps.zoom.step || 0.1;
      zoom.value = track.getSettings().zoom || caps.zoom.min;
      zoom.hidden = false;
    } else {
      zoom.hidden = true;
    }

    $('torch').hidden = !caps.torch;
    $('flip').hidden = false;
  }

  function stop() {
    if (stream) stream.getTracks().forEach((t) => t.stop());
    stream = null;
    track = null;
    torchOn = false;
  }

  function fail(err) {
    document.body.dataset.state = 'denied';
    const why =
      err && err.name === 'NotAllowedError'
        ? 'The camera was blocked. Allow it in your browser settings and reload this page.'
        : err && err.name === 'NotFoundError'
          ? "This device doesn't seem to have a camera."
          : 'The camera could not be opened on this browser.';
    $('why').textContent = why;
  }

  // ---- capture -------------------------------------------------------------------------------
  async function shoot() {
    if (!track || document.body.dataset.busy === '1') return;
    document.body.dataset.busy = '1';

    flash();
    if (navigator.vibrate) navigator.vibrate(12);

    try {
      const blob = await grab();
      if (blob) await enqueue(blob);
    } catch {
      /* One bad frame is not worth a broken page; the next press tries again. */
    } finally {
      document.body.dataset.busy = '';
    }
  }

  /**
   * The frame, at the best resolution going.
   *
   * ImageCapture gives the full still rather than the preview frame, but only Chromium has it, and
   * what it returns is unfiltered and unmirrored — so it is redrawn either way.
   */
  async function grab() {
    let source = video;

    if (window.ImageCapture) {
      try {
        const still = await new ImageCapture(track).takePhoto();
        source = await createImageBitmap(still);
      } catch {
        source = video;
      }
    }

    const w = source.videoWidth || source.width;
    const h = source.videoHeight || source.height;
    if (!w || !h) return null;

    const canvas = document.createElement('canvas');
    canvas.width = w;
    canvas.height = h;
    const ctx = canvas.getContext('2d');

    const css = FILTERS[filterIndex].css;
    if (canvasFilterWorks && css !== 'none') ctx.filter = css;

    if (facing === 'user') {
      ctx.translate(w, 0);
      ctx.scale(-1, 1);
    }
    ctx.drawImage(source, 0, 0, w, h);

    if (source.close) source.close();

    // Older Safari has no canvas filter. Rather than silently hand back an unfiltered photo that
    // does not match the preview, do the same grade by hand.
    if (!canvasFilterWorks && css !== 'none') grade(ctx, w, h, css);

    return await new Promise((resolve) => canvas.toBlob(resolve, 'image/jpeg', 0.92));
  }

  /** A per-pixel stand-in for the CSS grades above, for browsers without ctx.filter. */
  function grade(ctx, w, h, css) {
    const num = (name, dflt) => {
      const m = new RegExp(name + '\\(([0-9.]+)').exec(css);
      return m ? parseFloat(m[1]) : dflt;
    };
    const sat = num('saturate', 1);
    const con = num('contrast', 1);
    const bri = num('brightness', 1);
    const sep = num('sepia', 0);
    const grey = num('grayscale', 0);

    const img = ctx.getImageData(0, 0, w, h);
    const d = img.data;
    for (let i = 0; i < d.length; i += 4) {
      let r = d[i], g = d[i + 1], b = d[i + 2];
      const l = 0.2126 * r + 0.7152 * g + 0.0722 * b;

      if (grey) { r += (l - r) * grey; g += (l - g) * grey; b += (l - b) * grey; }
      if (sat !== 1) { r = l + (r - l) * sat; g = l + (g - l) * sat; b = l + (b - l) * sat; }
      if (sep) {
        const sr = 0.393 * r + 0.769 * g + 0.189 * b;
        const sg = 0.349 * r + 0.686 * g + 0.168 * b;
        const sb = 0.272 * r + 0.534 * g + 0.131 * b;
        r += (sr - r) * sep; g += (sg - g) * sep; b += (sb - b) * sep;
      }
      if (con !== 1) { r = (r - 128) * con + 128; g = (g - 128) * con + 128; b = (b - 128) * con + 128; }
      if (bri !== 1) { r *= bri; g *= bri; b *= bri; }

      d[i] = r < 0 ? 0 : r > 255 ? 255 : r;
      d[i + 1] = g < 0 ? 0 : g > 255 ? 255 : g;
      d[i + 2] = b < 0 ? 0 : b > 255 ? 255 : b;
    }
    ctx.putImageData(img, 0, 0);
  }

  function flash() {
    const f = $('flashfx');
    f.classList.remove('go');
    void f.offsetWidth;
    f.classList.add('go');
  }

  // ---- the queue -----------------------------------------------------------------------------
  // IndexedDB rather than memory: a photograph that exists only in a tab is lost to a lock screen,
  // a phone call, or an accidental back gesture — at an event, all three are routine.
  const DB = 'ib-camera';
  const STORE = 'pending';
  let db = null;

  function openDb() {
    return new Promise((resolve) => {
      let req;
      try {
        req = indexedDB.open(DB, 1);
      } catch {
        resolve(null);
        return;
      }
      req.onupgradeneeded = () => {
        const d = req.result;
        if (!d.objectStoreNames.contains(STORE)) d.createObjectStore(STORE, { keyPath: 'id' });
      };
      req.onsuccess = () => resolve(req.result);
      req.onerror = () => resolve(null);
    });
  }

  function tx(mode) {
    return db.transaction(STORE, mode).objectStore(STORE);
  }

  function put(item) {
    return new Promise((resolve) => {
      if (!db) return resolve();
      const r = tx('readwrite').put(item);
      r.onsuccess = r.onerror = () => resolve();
    });
  }

  function drop(id) {
    return new Promise((resolve) => {
      if (!db) return resolve();
      const r = tx('readwrite').delete(id);
      r.onsuccess = r.onerror = () => resolve();
    });
  }

  function all() {
    return new Promise((resolve) => {
      if (!db) return resolve([]);
      const r = tx('readonly').getAll();
      r.onsuccess = () => resolve(r.result || []);
      r.onerror = () => resolve([]);
    });
  }

  const live = new Map();

  async function enqueue(blob) {
    const item = { id: `${Date.now()}-${Math.random().toString(36).slice(2, 8)}`, blob, tries: 0 };
    await put(item);
    tile(item);
    pump();
  }

  function tile(item) {
    const el = document.createElement('div');
    el.className = 'shot';
    el.dataset.id = item.id;
    const img = document.createElement('img');
    img.src = URL.createObjectURL(item.blob);
    img.alt = '';
    // Revoked on load: a party's worth of object URLs held open is a party's worth of memory.
    img.onload = () => URL.revokeObjectURL(img.src);
    el.appendChild(img);
    el.appendChild(Object.assign(document.createElement('span'), { className: 'mark' }));
    strip.prepend(el);
    live.set(item.id, el);
    count();
  }

  function mark(id, state) {
    const el = live.get(id);
    if (el) el.dataset.state = state;
    count();
  }

  /**
   * Still on its way: neither uploaded nor permanently refused. Both states are terminal, and a
   * refused frame has already been dropped from the store — counting it as pending would leave a
   * badge that never clears and a leave-the-page warning that fires forever.
   */
  const PENDING = '.shot:not([data-state="done"]):not([data-state="rejected"])';

  function count() {
    const pending = strip.querySelectorAll(PENDING).length;
    $('pending').textContent = pending ? String(pending) : '';
    $('pending').hidden = pending === 0;
  }

  let active = 0;
  const MAX_ACTIVE = 2;

  // In-flight ids are tracked HERE, not on the record. Every all() returns freshly deserialized
  // objects, so a flag written to one of them is invisible to the next read — two pumps would each
  // believe they were the first and upload the same photograph twice.
  const inflight = new Set();

  async function pump() {
    if (active >= MAX_ACTIVE) return;
    const waiting = (await all()).filter((i) => !inflight.has(i.id));
    for (const item of waiting) {
      if (active >= MAX_ACTIVE) break;
      send(item);
    }
  }

  async function send(item) {
    if (inflight.has(item.id)) return;
    inflight.add(item.id);
    active++;
    mark(item.id, 'sending');

    const body = new FormData();
    body.append('file', item.blob, `${item.id}.jpg`);

    try {
      const res = await fetch(cfg.upload, {
        method: 'POST',
        body,
        credentials: 'same-origin',
        headers: { Accept: 'application/json' },
      });
      if (!res.ok) {
        const e = new Error(String(res.status));
        // 408/429 and 5xx are the server saying "not now"; the 4xx range is "not ever".
        e.rejected = res.status >= 400 && res.status < 500 && res.status !== 408 && res.status !== 429;
        throw e;
      }

      await drop(item.id);
      mark(item.id, 'done');
      active--;
      inflight.delete(item.id);
      pump();
    } catch (err) {
      active--;
      inflight.delete(item.id);

      // A 4xx means this frame will never be accepted — retrying it forever would block the queue
      // behind a photograph the server has already refused.
      if (err && err.rejected) {
        await drop(item.id);
        mark(item.id, 'rejected');
        pump();
        return;
      }

      item.tries = (item.tries || 0) + 1;
      await put({ id: item.id, blob: item.blob, tries: item.tries });
      mark(item.id, 'retry');
      // Backs off, but never gives up while the tab is open — the photograph is already safe on
      // disk, so the only thing failing is this attempt.
      const wait = Math.min(30000, 1000 * Math.pow(2, item.tries));
      setTimeout(pump, wait);
    }
  }

  // ---- wiring --------------------------------------------------------------------------------
  function filters() {
    const bar = $('filters');
    FILTERS.forEach((f, i) => {
      const b = document.createElement('button');
      b.type = 'button';
      b.textContent = f.name;
      b.className = 'chip';
      if (i === 0) b.classList.add('on');
      b.addEventListener('click', () => {
        filterIndex = i;
        video.style.filter = f.css;
        bar.querySelectorAll('.chip').forEach((c) => c.classList.remove('on'));
        b.classList.add('on');
      });
      bar.appendChild(b);
    });
  }

  async function boot() {
    db = await openDb();
    filters();

    shutter.addEventListener('click', shoot);
    $('flip').addEventListener('click', () => start(facing === 'user' ? 'environment' : 'user'));

    $('torch').addEventListener('click', async () => {
      if (!track) return;
      torchOn = !torchOn;
      try {
        await track.applyConstraints({ advanced: [{ torch: torchOn }] });
        $('torch').classList.toggle('on', torchOn);
      } catch {
        $('torch').hidden = true;
      }
    });

    $('zoom').addEventListener('input', async (e) => {
      if (!track) return;
      try {
        await track.applyConstraints({ advanced: [{ zoom: Number(e.target.value) }] });
      } catch {
        /* ignored: the slider is only shown when the track claimed to support this */
      }
    });

    // Anything left from a previous visit is already on disk. Show it and keep trying.
    for (const item of await all()) tile(item);
    pump();
    window.addEventListener('online', pump);

    // Releasing the camera while hidden stops the phone cooking in a pocket; it comes back on return.
    document.addEventListener('visibilitychange', () => {
      if (document.hidden) stop();
      else if (document.body.dataset.state === 'live') start();
    });

    window.addEventListener('beforeunload', (e) => {
      if (strip.querySelector(PENDING)) {
        e.preventDefault();
        e.returnValue = '';
      }
    });

    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
      fail({ name: 'NotSupportedError' });
      return;
    }
    start('environment');
  }

  boot();
})();
