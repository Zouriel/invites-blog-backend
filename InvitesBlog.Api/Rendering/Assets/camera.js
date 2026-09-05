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

  /**
   * Front cameras are wide — wide enough that a selfie taken at arm's length is mostly room. A
   * little in is the more flattering default, and anyone who disagrees can pull back out.
   */
  const FRONT_ZOOM = 1.25;

  /** Whether the selfie framing has already been applied once, so a re-open does not re-impose it. */
  let frontZoomed = false;

  /** True while two fingers are down, so the tap that ends a pinch does not also refocus. */
  let pinching = false;

  /**
   * Set only when the track cannot zoom itself. Sensor zoom is real detail; this is a crop, so it
   * is the fallback rather than the method — and exactly one of the two is ever in play WITHIN a
   * lens, which is what stops them compounding into each other.
   */
  let crop = 1;

  // ---- zoom ----------------------------------------------------------------------------------
  //
  // Zoom here means one number: how far in, relative to the phone's MAIN back lens. 1 is that lens,
  // 0.5 is the ultra-wide beside it, 2 is the telephoto. It is deliberately the number printed on
  // every phone camera app, because it is the number the person pinching already understands.
  //
  // Three mechanisms deliver it, in descending order of how good the picture is:
  //
  //   1. A DIFFERENT LENS. Real optics, full sensor resolution, and the reason a modern phone takes
  //      a good wide shot and a good distant one. Reachable on the web only where the browser
  //      enumerates the back cameras separately — iOS has since 16.3; most Androids expose one
  //      logical back camera and nothing else, because Camera2 hides the physical sub-cameras of a
  //      logical group by default and Chrome does not ask for them.
  //   2. SENSOR ZOOM, the `zoom` constraint. Real detail within one lens. Chromium only: WebKit
  //      still does not implement it, which is why the old slider was invisible on every iPhone —
  //      it was bound to a capability iOS does not have.
  //   3. A CROP of the frame. Every device, no new detail. The floor rather than the method.
  //
  // They compose: a lens gets us to the nearest optical step, and whatever is left over is covered
  // by (2) if the track has it and (3) if it does not.

  /** How far in the user has asked to be, as a multiple of the main back lens. */
  let zoom = 1;

  /** The back lens currently open, as an entry of {@link lenses}, or null when we have no choice. */
  let lens = null;

  /**
   * The back cameras this device will actually hand over, ascending by how wide they are.
   *
   * <p>Empty until the first camera has started, because labels are blank until a permission is
   * granted and the label is the only thing that says which lens a device is. There is no standard
   * field for focal length, field of view, or "this is the ultra-wide" — the working group has been
   * asked and the answer is still no — so this is a heuristic, and it is written to degrade into
   * "one camera, no lens switching" rather than to guess wrong.</p>
   */
  let lenses = [];

  /**
   * The most we will crop before refusing to go further. Beyond this it stops being zoom and starts
   * being a smaller photograph of the same pixels — the point where a phone's own camera app also
   * stops, for the same reason.
   */
  const MAX_DIGITAL = 4;

  /**
   * Night mode. Off by default, because a room that is already lit does not want it: biasing a good
   * exposure upward only washes it out.
   */
  let night = false;

  /** How far to bias exposure, in EV, when it is on. Clamped to whatever the track allows. */
  const NIGHT_EV = 1.5;

  // ---- recording -----------------------------------------------------------------------------

  /** How long the shutter must be held before it is a recording rather than a slow tap. */
  const HOLD_MS = 350;

  /**
   * The ceiling on one clip. Not a technical limit — it is what keeps a pocketed phone with a
   * locked shutter from filling the queue with an hour of the inside of a jacket, and it bounds
   * what the upload is allowed to weigh on a venue's wifi.
   */
  const MAX_MS = 60000;

  /** How near the finger must come to the lock, in px, to engage it and to light it up on approach. */
  const LOCK_HIT = 40;
  const LOCK_NEAR = 96;

  let recorder = null;
  let chunks = [];
  let recFrom = 0;
  let recTick = 0;
  let recPoster = null;
  let locked = false;

  /**
   * Set the moment the lock engages and cleared by the release that follows it.
   *
   * <p>Locking and stopping are both "the finger came off the shutter", and without this they are
   * the same event: the lift that completes the locking gesture would immediately stop the clip it
   * had just locked, and the whole feature would do nothing. One release is spent here; the next
   * press is the stop.</p>
   */
  let lockedByThisPress = false;

  /**
   * The microphone, or false once it is known there will not be one.
   *
   * <p>Asked for in the background as soon as the camera is live, NOT at the moment someone starts
   * recording. A permission prompt raised mid-gesture is one raised while a finger is holding the
   * shutter down: answering it loses the hold, and the clip that provoked it is the one that gets
   * missed. It is a separate request from the camera's on purpose — asking for both at once means a
   * guest who does not want to be recorded refuses the camera as well.</p>
   */
  let mic = null;

  /** Held so the preview's grade can be put back when a recording ends. */
  let filterBeforeRecording = 'none';

  const canvasFilterWorks = (() => {
    try {
      const c = document.createElement('canvas').getContext('2d');
      return c !== null && 'filter' in c;
    } catch {
      return false;
    }
  })();

  /**
   * Opens a camera.
   *
   * @param next which way it faces, or nothing to keep the current one.
   * @param deviceId a specific camera to open — how a lens is chosen. Exact, because "ideal" on a
   *        deviceId means "or anything else", and anything else is the wrong lens.
   */
  async function start(next, deviceId) {
    const previous = facing;
    stop();
    facing = next || facing;

    // Ask for far more than any sensor gives; the browser clamps to the best it actually has.
    //
    // frameRate floor is deliberately low. A camera in a dark room lengthens its exposure and
    // drops frames to do it — that IS its low-light behaviour — so demanding a steady 30 would
    // fight the one adaptation that makes an evening party usable, and buy a dim, noisy preview
    // in exchange for smooth motion nobody asked for.
    // NOT named `video`: that is the <video> element, declared at the top of this file, and
    // shadowing it here left `video.srcObject = stream` quietly setting a property on a plain
    // object and `video.classList` throwing on undefined — with the throw landing between the
    // preview starting and the state being set, so the page sat on its loading spinner forever.
    const wanted = deviceId
      // A named camera and a facingMode are two answers to the same question, and a device that
      // reads both can refuse the pair. The id is the more specific of the two, so it goes alone.
      ? {
          deviceId: { exact: deviceId },
          width: { ideal: 4096 },
          height: { ideal: 4096 },
          frameRate: { ideal: 30, min: 5 },
        }
      : {
          facingMode: { ideal: facing },
          width: { ideal: 4096 },
          height: { ideal: 4096 },
          frameRate: { ideal: 30, min: 5 },
        };

    try {
      // Zoom has to be asked for HERE. A camera permission granted without pan-tilt-zoom never
      // gains it later, so requesting it at applyConstraints time is too late — which is why the
      // zoom control could quietly never appear. Devices that refuse the whole request over it
      // get a second, plainer ask rather than no camera.
      try {
        stream = await navigator.mediaDevices.getUserMedia({ audio: false, video: { ...wanted, zoom: true } });
      } catch {
        stream = await navigator.mediaDevices.getUserMedia({ audio: false, video: wanted });
      }
    } catch (err) {
      // A device with one camera refuses the facing it does not have. That must not take down a
      // viewfinder that was already working — go back to the camera we had and stay live. Only a
      // failure with nothing to fall back to is a dead end worth showing the gate for.
      if (document.body.dataset.state === 'live' && previous !== facing) {
        facing = previous;
        await start(previous);
        $('flip').hidden = true;
        return;
      }
      fail(err);
      return;
    }

    // Everything from here to 'live' is wrapped, because a throw in the middle of it leaves the
    // page on its loading spinner with nothing to press — the state never becomes 'live' and
    // nothing ever calls fail(). A bug that stops the camera should say so, not hang.
    try {
      video.srcObject = stream;
      track = stream.getVideoTracks()[0] || null;
      // A selfie preview is mirrored because that is how a mirror behaves and how every phone
      // camera shows it. What gets SAVED is mirrored to match — a portrait that flips the moment
      // you take it reads as broken, whatever the optics say.
      video.classList.toggle('mirror', facing === 'user');

      await maxOut();
      await settle();
      // Only now: labels are blank until a camera permission has actually been granted, so asking
      // before this point returns a list of anonymous devices and no way to tell them apart.
      await discoverLenses();
      controls();
      document.body.dataset.state = 'live';

      // Not awaited: the viewfinder is already usable, and the microphone prompt must not be
      // something the camera waits behind. By the time anyone has framed a shot and held the
      // shutter down, this has long since been answered one way or the other.
      listenIn();
    } catch (err) {
      stop();
      fail(err);
    }
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

  /**
   * Works out which back cameras this phone has and what each one is FOR.
   *
   * <p>There is no field on a media device that says "ultra-wide" — no focal length, no field of
   * view, nothing. The label is the only signal, and it is a display string: localised, worded
   * differently by every platform, and attached to ids that change between sessions. So this reads
   * the label for the two words that matter, takes the plain back camera as 1×, and where it
   * recognises nothing it produces a list of one and the whole feature quietly becomes "pinch
   * crops", which is what every device got before.</p>
   *
   * <p>The virtual multi-camera devices iOS also offers — the ones that switch lenses by themselves
   * — are deliberately skipped. They switch on a zoom factor set through a constraint WebKit does
   * not implement, so on the web they are the wide lens with extra steps.</p>
   */
  async function discoverLenses() {
    if (lenses.length || !navigator.mediaDevices.enumerateDevices) return;

    let devices = [];
    try {
      devices = await navigator.mediaDevices.enumerateDevices();
    } catch {
      return;
    }

    const cams = devices.filter((d) => d.kind === 'videoinput' && d.deviceId);
    const found = [];

    for (const d of cams) {
      const label = (d.label || '').toLowerCase();
      // Front cameras and the auto-switching virtual devices are not lenses we can drive.
      if (!label) continue;
      if (label.includes('front') || label.includes('user') || label.includes('face')) continue;
      if (label.includes('dual') || label.includes('triple')) continue;
      if (!(label.includes('back') || label.includes('rear') || label.includes('environment'))) continue;

      const factor = label.includes('ultra') ? 0.5 : label.includes('tele') ? 2 : 1;
      found.push({ deviceId: d.deviceId, factor, label: d.label });
    }

    // One recognised camera is the same as none: there is nothing to switch BETWEEN, and a bar with
    // a single button on it is a control that cannot do anything.
    if (found.length < 2) return;

    // Ascending, and only one of each step — a phone with two telephotos would otherwise put two
    // buttons marked 2 next to each other.
    found.sort((a, b) => a.factor - b.factor);
    const seen = new Set();
    lenses = found.filter((l) => !seen.has(l.factor) && seen.add(l.factor));

    // Whichever of them we are actually looking through right now.
    const open = track && track.getSettings ? track.getSettings().deviceId : null;
    lens = lenses.find((l) => l.deviceId === open) || lenses.find((l) => l.factor === 1) || lenses[0];
    zoom = lens ? lens.factor : 1;
  }

  /** The widest and closest this device can go, counting lenses, sensor zoom and cropping. */
  function zoomRange() {
    const widest = lenses.length ? lenses[0].factor : 1;
    const longest = lenses.length ? lenses[lenses.length - 1].factor : 1;
    const caps = track && track.getCapabilities ? track.getCapabilities() : {};
    const sensor = caps.zoom && caps.zoom.max > caps.zoom.min ? caps.zoom.max / (caps.zoom.min || 1) : 1;
    return { min: widest, max: longest * Math.max(sensor, MAX_DIGITAL) };
  }

  /** Which lens a given factor belongs on: the longest one that does not overshoot it. */
  function lensFor(z) {
    if (!lenses.length) return null;
    let best = lenses[0];
    for (const l of lenses) if (l.factor <= z + 1e-6) best = l;
    return best;
  }

  /**
   * Goes to a zoom factor, by whatever means this device has.
   *
   * <p>The lens comes first because it is the only one of the three that adds real detail, and the
   * remainder is then taken within it — sensor zoom where the track has it, a crop where it does
   * not, never both.</p>
   */
  async function applyZoom(z, { switchLens = true } = {}) {
    const range = zoomRange();
    zoom = Math.max(range.min, Math.min(range.max, z));

    // Mid-pinch the lens is normally pinned — switching is a camera teardown, and doing it every
    // time a finger crossed a step would stutter the preview. The exception is a lens that cannot
    // represent what was asked for at all: cropped past its ceiling, or asked to go wider than it
    // is. Then the number on screen and the picture behind it have already parted company, and the
    // stutter is the cheaper of the two.
    //
    // The lenses overlap — the wide covers 1× to 4×, the 2× telephoto covers 2× to 8× — so this is
    // its own hysteresis: coming back down from 8× stays on the telephoto until 2×, and going up
    // from 1× stays on the wide until 4×. Nothing oscillates at a boundary.
    const want = switchLens ? lensFor(zoom) : outgrown() ? lensFor(zoom) : lens;
    if (want && lens && want.deviceId !== lens.deviceId) {
      lens = want;
      // A full restart, because a lens IS a different camera. The zoom the user asked for survives
      // it: start() finishes by calling settle(), which lands the remainder on the new lens.
      await start(facing, want.deviceId);
      return;
    }

    await applyResidual();
    paintZoom();
  }

  /** Whether the open lens can no longer honestly show the zoom being asked of it. */
  function outgrown() {
    if (!lens) return false;
    const residual = zoom / lens.factor;
    return residual > MAX_DIGITAL + 1e-6 || residual < 1 - 1e-6;
  }

  /** The part of the zoom the current lens has to cover on its own. */
  async function applyResidual() {
    const base = lens ? lens.factor : 1;
    const residual = Math.max(1, zoom / base);
    const caps = track && track.getCapabilities ? track.getCapabilities() : {};

    crop = 1;
    if (caps.zoom && caps.zoom.max > caps.zoom.min) {
      const min = caps.zoom.min || 1;
      const target = Math.min(caps.zoom.max, min * residual);
      try {
        await track.applyConstraints({ advanced: [{ zoom: target }] });
        // Whatever the sensor could not reach is cropped on top. This is the one place the two are
        // allowed to meet, and only because the sensor has run out rather than because we asked
        // for both.
        crop = Math.max(1, Math.min(MAX_DIGITAL, (min * residual) / target));
      } catch {
        crop = Math.min(MAX_DIGITAL, residual);
      }
    } else {
      crop = Math.min(MAX_DIGITAL, residual);
    }

    video.style.setProperty('--crop', String(crop));
  }

  /**
   * Opening state: how far in, and how the camera should hold focus.
   *
   * Sensor zoom is preferred wherever the track offers it — it is real detail rather than a bigger
   * crop of the same pixels — and the slider then reflects and adjusts it. Where the track offers
   * none, the same amount is taken as a crop instead, so a selfie frames the same on any phone.
   */
  async function settle() {
    if (facing === 'user') {
      // The front camera has one lens and no bar. It opens a little in and stays wherever the
      // pinch leaves it, so the selfie framing is not re-imposed on every focus change.
      lens = null;
      if (!frontZoomed) {
        zoom = FRONT_ZOOM;
        frontZoomed = true;
      }
    } else {
      frontZoomed = false;
      // Back again: the zoom is measured from the main lens, and whichever one is open decides what
      // the remainder is. Not switchLens — we are already on the camera we were handed.
      if (lenses.length) lens = lenses.find((l) => l.deviceId === openDeviceId()) || lens;
    }

    await applyResidual();
    paintZoom();
    await meter();
  }

  /** The id of the camera actually open, which is how we know which lens we ended up on. */
  function openDeviceId() {
    return track && track.getSettings ? track.getSettings().deviceId : null;
  }

  /**
   * How the camera should meter the scene — focus, exposure and white balance.
   *
   * <p>Everything here is asked for outright rather than after consulting getCapabilities: devices
   * that do these perfectly well do not always advertise them, and a constraint a track cannot
   * honour is refused harmlessly. Reading capabilities first only adds a way to be wrong.</p>
   *
   * <p><b>Continuous, never manual.</b> A fixed exposure is the wrong instrument for a party: the
   * light changes as people move between a lit table and a dark garden, and a manual setting good
   * for one is useless for the other. Manual exposure also persists onto whatever the browser points
   * at that camera next, which is not ours to leave behind. So the camera keeps adapting, and night
   * mode biases that adaptation instead of replacing it.</p>
   */
  async function meter() {
    if (!track) return;

    const ask = async (constraint) => {
      try {
        await track.applyConstraints({ advanced: [constraint] });
        return true;
      } catch {
        return false;
      }
    };

    // Tapping overrides focus; this is what makes it hold without being asked.
    await ask({ focusMode: 'continuous' });

    // Party light is mixed and coloured — candles, a phone screen, whatever the room is lit with —
    // and a white balance fixed at the first frame turns the rest of the night orange.
    await ask({ whiteBalanceMode: 'continuous' });

    await ask({ exposureMode: 'continuous' });
    await applyNightBias();
  }

  /**
   * Night mode: bias the camera's own exposure upward rather than taking it over.
   *
   * <p>exposureCompensation shifts the target the auto-exposure aims at, in EV, so the camera goes
   * on adapting to a changing room and simply aims brighter. Clamped to the range the track reports,
   * because the useful amount differs per sensor and an out-of-range value is refused outright —
   * taking the rest of the request with it.</p>
   */
  async function applyNightBias() {
    if (!track || !track.getCapabilities) return;

    let caps = {};
    try {
      caps = track.getCapabilities() || {};
    } catch {
      return;
    }
    if (!caps.exposureCompensation) return;

    const { min = 0, max = 0, step } = caps.exposureCompensation;
    const target = night ? Math.min(max, NIGHT_EV) : 0;
    const clamped = Math.max(min, Math.min(max, target));
    const value = step ? Math.round(clamped / step) * step : clamped;

    try {
      await track.applyConstraints({ advanced: [{ exposureCompensation: value }] });
    } catch { /* the track keeps whatever it was metering at */ }
  }

  /**
   * Draws the zoom bar: one button per lens, and the number on whichever is in use.
   *
   * <p>The shape every phone camera app uses, because it is the shape of the thing: the lenses are
   * discrete and the zoom between them is continuous, so the buttons are the steps and the pinch is
   * everything in between. The active one shows what you are actually at — "1.8×", not "1" — which
   * is the only feedback a pinch has.</p>
   */
  function paintZoom() {
    const bar = $('zoombar');
    if (!bar) return;

    // Nothing to show on a device with one lens and no way to zoom it at all.
    const range = zoomRange();
    if (facing === 'user' || (lenses.length < 2 && range.max <= 1.01)) {
      bar.hidden = true;
      return;
    }
    bar.hidden = false;

    const steps = lenses.length ? lenses : [{ factor: 1 }];
    if (bar.childElementCount !== steps.length) {
      bar.textContent = '';
      for (const step of steps) {
        const b = document.createElement('button');
        b.type = 'button';
        b.dataset.factor = String(step.factor);
        b.addEventListener('click', () => applyZoom(step.factor));
        bar.appendChild(b);
      }
    }

    // The lens actually OPEN, not the one this zoom factor belongs to. Mid-pinch they differ — the
    // wide lens holds on up to 4x before handing over — and lighting up the telephoto while the
    // wide one is what you are looking through says the wrong thing about the picture.
    const on = lens || steps[0];
    [...bar.children].forEach((b, i) => {
      const f = Number(b.dataset.factor);
      const active = steps[i] === on || (!lenses.length && i === 0);
      b.classList.toggle('on', active);
      b.setAttribute('aria-pressed', String(active));
      // The active step reads out the real figure; the others stay as their own labels, so the bar
      // does not reflow every frame of a pinch.
      b.textContent = active ? `${trim(zoom)}\u00d7` : trim(f);
      b.setAttribute('aria-label', `Zoom to ${trim(f)} times`);
    });
  }

  /** 0.5, 1, 1.8, 2 — never 1.0 or 1.80, which read as precision that is not there. */
  function trim(n) {
    return String(Math.round(n * 10) / 10);
  }

  /** Show only the controls this device actually has. Most of these are Android-only today. */
  function controls() {
    const caps = track && track.getCapabilities ? track.getCapabilities() : {};

    paintZoom();

    $('torch').hidden = !caps.torch;
    $('flip').hidden = false;

    // Offered wherever the camera will take an exposure bias. Where it will not, night mode has
    // nothing to act on, and a switch that does nothing is worse than no switch.
    $('night').hidden = !caps.exposureCompensation;
    $('night').classList.toggle('on', night);
    $('night').setAttribute('aria-pressed', String(night));
  }

  function stop() {
    // The recording goes FIRST. The tracks feeding it are about to be pulled out from under it, and
    // a recorder that loses its source mid-flight keeps nothing — whereas one asked to stop flushes
    // what it has. This is the path a phone locking its screen takes, so it is the difference
    // between losing the clip and keeping it.
    if (recordingNow()) stopRecording();

    if (stream) stream.getTracks().forEach((t) => t.stop());
    // The microphone goes too. An open mic on a phone in someone's pocket is not a thing to leave
    // running quietly, and the permission is remembered — so coming back costs nothing but the ask.
    if (mic) mic.stop();
    mic = null;
    // The next recording gets new tracks, so what did or did not work with the old ones says nothing.
    plan = 0;
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
          : err && err.name === 'NotReadableError'
          ? 'Another app is using the camera. Close it and reload this page.'
          : 'The camera could not be opened on this browser.';
    $('why').textContent = why;

    // The browser's own name for what went wrong, said quietly underneath. Every one of these
    // failures looks identical to the person holding the phone — "the camera isn't available" — and
    // they have completely different causes: a permission they can change, a device that has no
    // camera, another app holding it. Without this, the only way to tell them apart is to be
    // standing next to the phone.
    const detail = $('whydetail');
    if (detail) detail.textContent = (err && (err.name || err.message)) || 'unknown';
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
        const capture = new ImageCapture(track);
        const still = await capture.takePhoto(await stillSettings(capture));

        // Nothing to change: hand back the encoder's own file. Drawing it to a canvas to read it
        // straight back out again would decode and re-encode a JPEG for no reason, which costs a
        // generation of quality and the sensor's full resolution, and buys nothing.
        if (!needsRedraw()) return still;

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
    if (crop !== 1) {
      // The photograph has to match what was on screen, so the same crop is taken here.
      const cw = w / crop;
      const ch = h / crop;
      ctx.drawImage(source, (w - cw) / 2, (h - ch) / 2, cw, ch, 0, 0, w, h);
    } else {
      ctx.drawImage(source, 0, 0, w, h);
    }

    if (source.close) source.close();

    // Older Safari has no canvas filter. Rather than silently hand back an unfiltered photo that
    // does not match the preview, do the same grade by hand.
    if (!canvasFilterWorks && css !== 'none') grade(ctx, w, h, css);

    return await new Promise((resolve) => canvas.toBlob(resolve, 'image/jpeg', 0.92));
  }

  /** Is there anything to do to the frame? If not, the encoder's own file is the better one. */
  function needsRedraw() {
    return crop !== 1 || facing === 'user' || FILTERS[filterIndex].css !== 'none';
  }

  /**
   * What to ask a still for: the largest the device will give, and the flash if it is wanted.
   *
   * <p>Without imageWidth/imageHeight a photo comes back at whatever the device defaults to, which
   * is often the preview size rather than the sensor's — so the "full resolution" capture quietly
   * was not one.</p>
   *
   * <p>fillLightMode is the flash proper: it fires for the exposure and stops. The torch is a
   * different thing — a lamp left on — and it is what lights the framing, so the two are kept in
   * step rather than fighting. A camera with no flash simply reports none and gets neither.</p>
   */
  async function stillSettings(capture) {
    const settings = {};

    let caps = null;
    try {
      caps = await capture.getPhotoCapabilities();
    } catch {
      return settings;
    }

    if (caps.imageWidth && caps.imageHeight) {
      settings.imageWidth = caps.imageWidth.max;
      settings.imageHeight = caps.imageHeight.max;
    }

    const modes = caps.fillLightMode || [];
    if (torchOn && modes.includes('flash')) settings.fillLightMode = 'flash';
    else if (night && modes.includes('auto')) settings.fillLightMode = 'auto';
    else if (modes.includes('off')) settings.fillLightMode = 'off';

    return settings;
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

  /**
   * Tap to focus.
   *
   * <p>The point is handed to the camera in its own coordinates, which for a selfie are not the
   * ones on screen: the preview is mirrored, so a tap on the left of the phone is the right of the
   * sensor. Missing that inversion focuses the camera on the opposite side of whatever was
   * tapped — right often enough to look like it works, and wrong exactly when someone is off
   * centre.</p>
   *
   * <p>The mark is drawn on every tap, and the constraint is attempted on every tap. An earlier
   * version gated both on getCapabilities() reporting a focusMode, which turned out to be the
   * wrong thing to trust: devices that focus perfectly well do not always advertise it, and the
   * result was a camera that refocused with no sign it had heard you. The mark says where you
   * tapped — which is true whatever the camera then does with it — and a device that cannot take
   * the point simply refuses the constraint.</p>
   */
  /**
   * Two fingers on the viewfinder, the way every camera on a phone works.
   *
   * <p>Tracked from the distance between the two pointers at the moment the second one lands, so the
   * zoom follows the fingers rather than accumulating drift — spread to twice the starting gap and
   * you are at twice the starting zoom, wherever the fingers began.</p>
   *
   * <p>The lens only changes when the fingers come off. Switching mid-gesture means tearing down a
   * camera and opening another one, which takes long enough to be felt, and doing it while somebody
   * is still moving their fingers would make the preview stutter every time they crossed a step. So
   * the pinch moves continuously within the lens it started on, and the step it landed on is taken
   * at the end.</p>
   */
  function pinch() {
    const stage = document.querySelector('.stage');
    if (!stage) return;

    const points = new Map();
    let from = 0;
    let zoomFrom = 1;

    const gap = () => {
      const [a, b] = [...points.values()];
      return Math.hypot(a.x - b.x, a.y - b.y);
    };

    stage.addEventListener('pointerdown', (e) => {
      // Never the shutter or the buttons — those have their own gestures, and hold-to-record must
      // not become a pinch because a second finger brushed the screen.
      if (e.target.closest('.bottom, .top, .lock')) return;
      points.set(e.pointerId, { x: e.clientX, y: e.clientY });
      if (points.size === 2) {
        from = gap();
        zoomFrom = zoom;
        pinching = true;
      }
    });

    stage.addEventListener('pointermove', (e) => {
      if (!points.has(e.pointerId)) return;
      points.set(e.pointerId, { x: e.clientX, y: e.clientY });
      if (points.size !== 2 || !from) return;
      e.preventDefault();
      // Within the current lens only: no restart while fingers are down.
      applyZoom(zoomFrom * (gap() / from), { switchLens: false });
    }, { passive: false });

    const lift = (e) => {
      points.delete(e.pointerId);
      if (points.size >= 2 || !from) return;
      from = 0;
      // Now the expensive part, once: if the gesture ended on a different lens's territory, move to
      // it. The residual lands on the new lens through settle().
      if (pinching) applyZoom(zoom);
      // Cleared on the next frame so the click this lift also produces does not focus the camera on
      // wherever the second finger happened to be.
      requestAnimationFrame(() => { pinching = false; });
    };

    stage.addEventListener('pointerup', lift);
    stage.addEventListener('pointercancel', lift);

    // A trackpad or a mouse wheel, for anyone testing this on a laptop.
    stage.addEventListener('wheel', (e) => {
      if (!e.ctrlKey) return;
      e.preventDefault();
      applyZoom(zoom * (e.deltaY < 0 ? 1.08 : 1 / 1.08));
    }, { passive: false });
  }

  async function focusAt(e) {
    if (!track || document.body.dataset.state !== 'live') return;
    if (pinching) return;

    const box = video.getBoundingClientRect();
    const px = e.clientX - box.left;
    const py = e.clientY - box.top;

    let x = px / box.width;
    let y = py / box.height;
    // Undo the crop. What is on screen is the middle of the frame blown up, so a tap two thirds of
    // the way across a 3x crop is not two thirds of the way across the SENSOR — and a focus point
    // handed to the camera in the wrong coordinates focuses on the wrong thing, further out the
    // more you have zoomed in.
    if (crop !== 1) {
      x = 0.5 + (x - 0.5) / crop;
      y = 0.5 + (y - 0.5) / crop;
    }
    if (facing === 'user') x = 1 - x;
    x = Math.min(1, Math.max(0, x));
    y = Math.min(1, Math.max(0, y));

    showReticle(px, py);

    try {
      await track.applyConstraints({
        advanced: [{ pointsOfInterest: [{ x, y }], focusMode: 'single-shot' }],
      });
    } catch {
      // Some tracks list focusMode but refuse a point. The mark stays: the tap was still received.
    }
  }

  let markTimer = 0;
  /**
   * NOT `mark`. There is a mark(id, state) further down for the upload strip, and two function
   * declarations of the same name in one scope are not two functions — the later one silently
   * replaces the earlier, so every tap-to-focus was calling the queue's marker with a pair of
   * pixel coordinates and the reticle never appeared once.
   */
  function showReticle(x, y) {
    const el = $('reticle');
    el.style.left = x + 'px';
    el.style.top = y + 'px';
    el.hidden = false;
    // Restart the animation on a repeat tap in the same place: without the reflow the class is
    // already there, nothing changes, and a second tap looks like it was ignored.
    el.classList.remove('go');
    void el.offsetWidth;
    el.classList.add('go');
    clearTimeout(markTimer);
    markTimer = setTimeout(() => {
      el.hidden = true;
      el.classList.remove('go');
    }, 1100);
  }

  function flash() {
    const f = $('flashfx');
    f.classList.remove('go');
    void f.offsetWidth;
    f.classList.add('go');
  }

  // ---- recording ------------------------------------------------------------------------------

  function recordingNow() {
    return recorder !== null && recorder.state === 'recording';
  }

  /**
   * What to record into, or null when this browser cannot record at all.
   *
   * <p>mp4 first and emphatically: it is the only container an iPhone will play back, and a clip
   * that half the party cannot open is not a memory of the party. WebM is the fallback for browsers
   * with no mp4 encoder. An empty string means "record, but choose for yourself" — better than
   * refusing on a browser whose isTypeSupported is simply pessimistic.</p>
   */
  function container() {
    if (typeof MediaRecorder === 'undefined') return null;
    if (!MediaRecorder.isTypeSupported) return '';
    const wanted = [
      'video/mp4;codecs=avc1.42E01E,mp4a.40.2',
      'video/mp4;codecs=avc1.42E01E',
      'video/mp4',
      'video/webm;codecs=vp9,opus',
      'video/webm;codecs=vp8,opus',
      'video/webm',
    ];
    for (const type of wanted) {
      try {
        if (MediaRecorder.isTypeSupported(type)) return type;
      } catch { /* keep looking */ }
    }
    return '';
  }

  /** Asked for once, early, and never again — see the note on {@link mic}. */
  async function listenIn() {
    if (mic !== null) return;
    if (!navigator.mediaDevices || !navigator.mediaDevices.getUserMedia) {
      mic = false;
      return;
    }
    try {
      const heard = await navigator.mediaDevices.getUserMedia({ audio: true });
      mic = heard.getAudioTracks()[0] || false;
    } catch {
      // Refused, or there is no microphone. Clips go up silent, which is worth far more than no
      // clips at all — and nothing asks again.
      mic = false;
    }
  }

  /**
   * The still that stands in for a clip in a grid.
   *
   * <p>Taken here because it is the only place it can be taken: pulling a frame out of an encoded
   * video needs a decoder the API does not have, and the browser is holding the frame already.
   * Small on purpose — it is only ever shown as a tile.</p>
   */
  async function posterFrame() {
    const w = video.videoWidth;
    const h = video.videoHeight;
    if (!w || !h) return null;

    const edge = 1280;
    const factor = Math.min(1, edge / Math.max(w, h));
    const canvas = document.createElement('canvas');
    canvas.width = Math.max(1, Math.round(w * factor));
    canvas.height = Math.max(1, Math.round(h * factor));
    const ctx = canvas.getContext('2d');
    if (!ctx) return null;

    // Ungraded and unmirrored, to match the clip it stands for — see startRecording on why the
    // preview's grade comes off for the duration.
    ctx.drawImage(video, 0, 0, canvas.width, canvas.height);
    return await new Promise((resolve) => canvas.toBlob(resolve, 'image/jpeg', 0.82));
  }

  /**
   * Builds a recorder and starts it, or returns null.
   *
   * <p>Both halves matter. A MediaRecorder can refuse at construction, refuse at start, or — the
   * case that actually bit — accept both and then fail, which leaves it inactive with no error
   * anyone asked to hear. So the state is checked after starting rather than assumed from the
   * absence of a throw.</p>
   */
  function begin(tracks, type) {
    let made;
    try {
      made = new MediaRecorder(new MediaStream(tracks), type ? { mimeType: type } : undefined);
    } catch {
      return null;
    }

    made.ondataavailable = (e) => {
      if (e.data && e.data.size) chunks.push(e.data);
    };
    made.onstop = finishRecording;
    // A recorder that fails mid-clip fires this and goes inactive WITHOUT firing onstop on every
    // browser. Unhandled, that is a shutter stuck red forever: the chrome says recording, nothing
    // is, and every press falls through to taking a photograph instead. Salvage and tidy up.
    made.onerror = () => {
      if (recorder === made) finishRecording();
    };

    try {
      made.start(1000);
    } catch {
      return null;
    }
    // Claimed support is not the same as working support — some phones advertise an mp4 encoder
    // they cannot actually run, and this is where that shows up.
    return made.state === 'recording' ? made : null;
  }

  /**
   * What worked last time, so a phone that cannot do the first combination does not retry it on
   * every clip. Reset whenever the camera restarts, since the tracks are new.
   */
  let plan = 0;

  async function startRecording() {
    if (!track || recordingNow() || document.body.dataset.state !== 'live') return;

    const type = container();
    if (type === null) return;   // no MediaRecorder here: the hold simply does nothing

    // Before anything else changes on screen, so the tile matches the clip's first frame.
    recPoster = await posterFrame();
    if (!recPoster) return;

    // The finger may have come off during that. If it has, this was a photograph after all and the
    // release has already taken it.
    if (holdId === -1) {
      recPoster = null;
      return;
    }

    const sound = mic && mic.readyState === 'live' ? [track, mic] : [track];
    // Least compromise first. Sound is given up before the clip is, and the container we chose is
    // given up before the browser's own — a webm nobody at the party can open still beats nothing,
    // and by then it is the only thing left that works.
    const attempts = [
      [sound, type],
      [sound, ''],
      [[track], type],
      [[track], ''],
    ];

    chunks = [];
    for (let i = plan; i < attempts.length; i++) {
      recorder = begin(attempts[i][0], attempts[i][1]);
      if (recorder) {
        plan = i;   // start here next time rather than failing the same way again
        break;
      }
    }

    if (!recorder) {
      // Nothing on this device will record. Leave no chrome behind saying otherwise.
      recPoster = null;
      chunks = [];
      return;
    }

    recFrom = Date.now();
    locked = false;

    // A CSS filter grades the PREVIEW; the recorder reads the track behind it and never sees one.
    // Rather than hand back a clip that does not look like what was on screen, the grade comes off
    // for the duration — so the viewfinder tells the truth about what is being recorded.
    filterBeforeRecording = video.style.filter || 'none';
    video.style.filter = 'none';

    document.body.dataset.rec = '1';
    if (navigator.vibrate) navigator.vibrate(18);

    // Past this point the shutter is red, so anything that throws has to be caught here: this runs
    // from a setTimeout inside an async function, where an escaping error becomes an unhandled
    // rejection nobody sees and the red stays until the page is reloaded.
    try {
      tickClock();
    } catch (err) {
      blame(err);
      finishRecording();
    }
  }

  function engageLock() {
    if (!recordingNow() || locked) return;
    locked = true;
    lockedByThisPress = true;
    document.body.dataset.rec = 'locked';
    $('lock').classList.remove('near');
    if (navigator.vibrate) navigator.vibrate([12, 40, 12]);
  }

  function stopRecording() {
    // The watchdog in tickClock goes off the moment the recorder stops being 'recording', which a
    // deliberate stop does immediately — before the final chunk and onstop have been delivered. Left
    // running it would race the stop and file a truncated clip. This is a stop, so it does the
    // tidying; the clock has nothing left to watch.
    clearInterval(recTick);
    recTick = 0;

    if (!recordingNow()) {
      // Nothing to stop, but the UI may still be showing a recording that failed to start cleanly.
      if (document.body.dataset.rec) endRecordingUi();
      return;
    }
    try {
      recorder.stop();
    } catch {
      // A recorder that will not stop cleanly still has to release the UI, or the shutter is stuck
      // recording forever with no way back.
      recorder = null;
      endRecordingUi();
    }
  }

  /**
   * Everything the recording put on screen, taken back off it.
   *
   * <p>Ordered by consequence, and each cosmetic step guarded on its own. The first four lines are
   * what decides whether the shutter is usable again; the rest is tidying. Written as one run of
   * statements, a single failing DOM write — the clock, say — throws partway through and abandons
   * the ones after it, which is how a recovery path ends up leaving behind the exact state it was
   * called to clear.</p>
   */
  function endRecordingUi() {
    clearInterval(recTick);
    recTick = 0;
    locked = false;
    lockedByThisPress = false;
    // REMOVED, not blanked. `dataset.rec = ''` leaves data-rec="" on the element, and every rule
    // written as body[data-rec] matches on the attribute EXISTING rather than on its value — so
    // blanking it cleared the state in JavaScript and changed nothing at all on screen. The shutter
    // stayed red, the clock chip stayed up reading the 0:00 this had just reset it to, and the
    // filters and the queue stayed dimmed under pointer-events:none. Everything looked stuck
    // because everything WAS stuck, on an attribute that had supposedly been cleared.
    try { document.body.removeAttribute('data-rec'); } catch { /* nothing else can be done */ }

    try { $('lock').classList.remove('near'); } catch { /* cosmetic */ }
    try { $('lock').style.removeProperty('--reach'); } catch { /* cosmetic */ }
    try { $('rectime').textContent = '0:00'; } catch { /* cosmetic */ }
    try { video.style.filter = filterBeforeRecording; } catch { /* cosmetic */ }
  }

  async function finishRecording() {
    const type = (recorder && recorder.mimeType) || 'video/mp4';
    const parts = chunks;
    const poster = recPoster;

    recorder = null;
    chunks = [];
    recPoster = null;
    endRecordingUi();

    if (!parts.length || !poster) return;

    // The codec parameters go: they are true of the file but they are not a content type anyone
    // downstream wants to match on, and they would end up on the stored object verbatim.
    const base = type.split(';')[0];
    const blob = new Blob(parts, { type: base });
    // A clip of a few kilobytes is a press that was read as a hold. Nobody meant to film that.
    if (blob.size < 12000) return;

    await enqueue(blob, { poster, ext: base === 'video/webm' ? '.webm' : '.mp4' });
  }

  function tickClock() {
    const show = () => {
      try {
        // The clock is the watchdog too. If the recorder has died without saying so, this is what
        // notices — within a quarter second, rather than leaving a red shutter that records
        // nothing until somebody reloads the page.
        if (!recordingNow()) {
          finishRecording();
          return;
        }
        const ms = Date.now() - recFrom;
        const secs = Math.floor(ms / 1000);
        $('rectime').textContent = `${Math.floor(secs / 60)}:${String(secs % 60).padStart(2, '0')}`;
        if (ms >= MAX_MS) stopRecording();
      } catch (err) {
        // Nothing in here is worth a shutter that stays red. A clock that cannot draw itself is a
        // cosmetic problem; a recording nobody can stop is not.
        blame(err);
        finishRecording();
      }
    };

    // The interval FIRST, deliberately. Built the other way round — one call, then the interval — a
    // throw in that first call means the interval is never created at all: the chrome says
    // recording, the clock is frozen at the 0:00 it was born with, and nothing is left running that
    // could ever notice or undo it. That is precisely the state this is here to make impossible.
    clearInterval(recTick);
    recTick = setInterval(show, 250);
    show();
  }

  /**
   * Puts the reason on screen.
   *
   * <p>This runs on other people's phones at a party. There is no console anyone is going to open,
   * so a failure that says nothing is a failure nobody can report and nobody can fix — the shutter
   * simply "goes weird". A few seconds of plain text in the chip is the difference between a bug
   * report and a shrug.</p>
   */
  function blame(err) {
    // Wrapped whole, because the thing this reports on is often the very thing it needs in order to
    // report: if the clock is what broke, writing the reason INTO the clock throws again — out of
    // the catch block that called it, past the tidy-up, and the shutter stays red after all. An
    // error path that can fail is not an error path.
    try {
      const why = (err && (err.message || err.name)) || 'unknown';
      const chip = $('rectime');
      if (!chip) return;
      document.body.dataset.failed = '1';
      chip.textContent = String(why).slice(0, 60);
      clearTimeout(blameTimer);
      blameTimer = setTimeout(() => {
        try {
          document.body.removeAttribute('data-failed');
          chip.textContent = '0:00';
        } catch { /* nothing left to say */ }
      }, 6000);
    } catch { /* the reason is lost; the recovery below is what actually matters */ }
  }

  let blameTimer = 0;

  /**
   * The shutter's gesture, all four events of it.
   *
   * <p>A tap and a hold are the same press until they are not, so the decision is made by a timer
   * rather than by which handler ran: held past {@link HOLD_MS} it becomes a recording, released
   * before it and it was a photograph. The pointer is captured so a finger sliding off the button
   * on its way to the lock keeps being heard.</p>
   *
   * <p>Locking is a separate press from the one that started the clip. Once locked the finger is
   * gone and the shutter is a stop button, which is why the down handler leaves early for it —
   * otherwise the press that ends a locked recording would start a second one.</p>
   */
  let holdId = -1;
  let holdTimer = 0;

  function onShutterDown(e) {
    if (document.body.dataset.state !== 'live') return;
    e.preventDefault();

    if (locked) return;   // the release stops it; nothing to start

    holdId = e.pointerId;
    try {
      shutter.setPointerCapture(e.pointerId);
    } catch { /* older engines manage without it */ }

    clearTimeout(holdTimer);
    holdTimer = setTimeout(() => {
      // startRecording is async, so its failures are rejections rather than throws — and a rejection
      // raised from a timer is one nobody is listening for.
      startRecording().catch((err) => {
        blame(err);
        finishRecording();
      });
    }, HOLD_MS);
  }

  function onShutterMove(e) {
    if (e.pointerId !== holdId || !recordingNow() || locked) return;

    // Distance to the lock itself rather than travel from the start, so the target is a place on
    // the screen the thumb can aim at — which is what the icon appearing there promises.
    const box = $('lock').getBoundingClientRect();
    const away = Math.hypot(
      e.clientX - (box.left + box.width / 2),
      e.clientY - (box.top + box.height / 2),
    );

    $('lock').classList.toggle('near', away <= LOCK_NEAR);
    // 0 at the lock, 1 a long way off — the icon fills as the finger closes on it.
    $('lock').style.setProperty(
      '--reach', String(Math.max(0, Math.min(1, 1 - (away - LOCK_HIT) / (LOCK_NEAR - LOCK_HIT)))),
    );
    if (away <= LOCK_HIT) engageLock();
  }

  function onShutterUp(e) {
    if (e.pointerId !== holdId && holdId !== -1) return;
    clearTimeout(holdTimer);
    const held = holdId !== -1;
    holdId = -1;
    try {
      shutter.releasePointerCapture(e.pointerId);
    } catch { /* it may never have been captured */ }

    // The chrome says recording and nothing is. That is a recorder that died quietly, and this
    // press is someone trying to make it stop — so make it stop, rather than taking a photograph
    // they did not ask for and leaving the shutter red.
    if (document.body.dataset.rec && !recordingNow()) {
      finishRecording();
      return;
    }

    // The release that completed the lock. It is spent doing exactly that — the recording carries
    // on without a finger on it, which is the entire point of locking.
    if (lockedByThisPress) {
      lockedByThisPress = false;
      return;
    }
    // Locked, and the finger left long ago: this press is the stop button.
    if (locked) {
      stopRecording();
      return;
    }
    if (recordingNow()) {
      stopRecording();
      return;
    }
    // The hold never matured into a recording, so the press was a tap — and a tap is a photograph.
    if (held) shoot();
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

  /**
   * Puts a capture on disk and lets the pump find it.
   *
   * <p>A clip arrives with the poster {@link startRecording} drew and the extension its container
   * earned; a photograph passes neither and gets the defaults. Both are stored whole, because the
   * poster is the one thing that cannot be reconstructed later — the frame it came from is gone the
   * moment the recording ends, and the server has no decoder to find another.</p>
   */
  async function enqueue(blob, clip) {
    const item = {
      id: `${Date.now()}-${Math.random().toString(36).slice(2, 8)}`,
      blob,
      tries: 0,
      poster: (clip && clip.poster) || null,
      ext: (clip && clip.ext) || '.jpg',
    };
    await put(item);
    tile(item);
    pump();
  }

  function tile(item) {
    const el = document.createElement('div');
    el.className = 'shot';
    el.dataset.id = item.id;
    // A clip cannot be shown as itself in a 46px square, so the poster stands in for it and a badge
    // says which it is. Anything queued before clips existed has no poster and is a photograph.
    if (item.poster) el.dataset.kind = 'video';
    const img = document.createElement('img');
    img.src = URL.createObjectURL(item.poster || item.blob);
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
    body.append('file', item.blob, `${item.id}${item.ext || '.jpg'}`);
    // The server refuses a video that arrives without one, so the two travel together or not at all.
    if (item.poster) body.append('poster', item.poster, `${item.id}_p.jpg`);
    // Whatever the page has to say on every upload. A guest is authorized by a cookie and needs
    // none; somebody who scanned a printed code carries a ticket, and it rides here rather than in
    // the URL of a page their browser will remember.
    for (const [k, v] of Object.entries(cfg.fields || {})) body.append(k, v);

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
      // The whole record, not a rebuilt one: writing back only the file would strip a clip of the
      // poster it can never be given again, and every retry after that would be refused for it.
      await put(item);
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

    // Pointer events rather than click: a click has already decided the press was a tap, and the
    // hold is the whole of the video gesture. The synthetic click that follows is swallowed so a
    // finished recording does not also fire the shutter.
    shutter.addEventListener('pointerdown', onShutterDown);
    shutter.addEventListener('pointermove', onShutterMove);
    shutter.addEventListener('pointerup', onShutterUp);
    shutter.addEventListener('pointercancel', onShutterUp);
    // The browser must not be able to claim the drag. Without this a swipe up from the shutter is
    // read as a pan, the pointer is taken away mid-gesture, and the pointercancel that follows is
    // indistinguishable from letting go — so the recording stops on its way to the lock and the
    // lock can never engage. There is nothing to scroll on this page anyway.
    shutter.style.touchAction = 'none';
    shutter.addEventListener('click', (e) => e.preventDefault());
    // A shutter driven by pointers is one a keyboard cannot press, and this is the only control on
    // the page that matters. Space and Enter take a photograph; the hold has no keyboard equivalent.
    shutter.addEventListener('keydown', (e) => {
      if (e.key !== ' ' && e.key !== 'Enter') return;
      e.preventDefault();
      shoot();
    });
    // On the video itself, so the controls layered over it keep their own taps.
    video.addEventListener('click', focusAt);
    pinch();
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

    $('night').addEventListener('click', async () => {
      night = !night;
      $('night').classList.toggle('on', night);
      $('night').setAttribute('aria-pressed', String(night));
      document.body.classList.toggle('night', night);
      await applyNightBias();
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
