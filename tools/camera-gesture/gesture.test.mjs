/*
 * Drives the camera's shutter gesture in jsdom.
 *
 * There is no camera here and there never will be, so getUserMedia, MediaRecorder and IndexedDB are
 * all stood in for. What is real is camera.js itself and the state machine inside it — which is the
 * part that decides whether a press is a photograph, a clip, or a clip that keeps running after the
 * finger leaves. That decision is the whole feature and it cannot be checked by holding a phone
 * twice and hoping.
 */
import { JSDOM } from 'jsdom';
import { readFileSync } from 'node:fs';

const SRC = new URL('../../InvitesBlog.Api/Rendering/Assets/camera.js', import.meta.url);
const ids = ['cam','queue','shoot','lock','rectime','reticle','flashfx','pending','filters','torch','night','flip','zoom','why'];

function boot() {
  const dom = new JSDOM(
    `<!doctype html><body>${ids.map((id) =>
      id === 'cam' ? `<video id="cam"></video>` : `<div id="${id}"></div>`).join('')}</body>`,
    { runScripts: 'outside-only', pretendToBeVisual: true },
  );
  const w = dom.window;
  const log = [];

  // --- stand-ins -----------------------------------------------------------------------------
  const track = { kind: 'video', readyState: 'live', stop() {}, getCapabilities: () => ({}),
                  getSettings: () => ({}), applyConstraints: async () => {} };
  const audio = { kind: 'audio', readyState: 'live', stop() {} };
  w.navigator.mediaDevices = {
    getUserMedia: async (c) => c.audio && !c.video
      ? { getAudioTracks: () => [audio], getTracks: () => [audio] }
      : { getVideoTracks: () => [track], getAudioTracks: () => [], getTracks: () => [track] },
  };
  class FakeRecorder {
    static isTypeSupported() { return true; }
    constructor() { this.state = 'inactive'; this.mimeType = 'video/mp4'; }
    start() { this.state = 'recording'; log.push('rec:start'); }
    stop() {
      this.state = 'inactive';
      log.push('rec:stop');
      this.ondataavailable?.({ data: { size: 999999 } });
      this.onstop?.();
    }
  }
  w.MediaRecorder = FakeRecorder;
  w.MediaStream = class { constructor(t) { this.t = t; } };
  w.indexedDB = { open: () => { const r = {}; setTimeout(() => r.onerror?.(), 0); return r; } };
  w.HTMLCanvasElement.prototype.getContext = () => ({ drawImage() {}, filter: '' });
  w.HTMLCanvasElement.prototype.toBlob = (cb) => cb({ size: 4242, type: 'image/jpeg' });
  w.URL.createObjectURL = () => 'blob:x';
  w.URL.revokeObjectURL = () => {};
  Object.defineProperty(w.HTMLVideoElement.prototype, 'videoWidth', { get: () => 1280 });
  Object.defineProperty(w.HTMLVideoElement.prototype, 'videoHeight', { get: () => 720 });
  w.__ibCamera = { upload: '/upload' };
  w.fetch = async () => ({ ok: true });

  w.eval(readFileSync(SRC, 'utf8'));
  return { w, doc: w.document, log };
}

const press = (el, type, id = 1, x = 100, y = 400) => {
  const e = new el.ownerDocument.defaultView.Event(type, { bubbles: true });
  Object.assign(e, { pointerId: id, clientX: x, clientY: y });
  el.dispatchEvent(e);
};
const wait = (ms) => new Promise((r) => setTimeout(r, ms));

let failures = 0;
function check(name, got, want) {
  const ok = JSON.stringify(got) === JSON.stringify(want);
  if (!ok) failures++;
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${ok ? '' : `\n        got  ${JSON.stringify(got)}\n        want ${JSON.stringify(want)}`}`);
}

const ready = async (t) => { await wait(60); t.doc.body.dataset.state = 'live'; return t; };

// 1. A tap is a photograph, and never starts a recording.
{
  const t = await ready(boot());
  const shoot = t.doc.getElementById('shoot');
  press(shoot, 'pointerdown'); await wait(40); press(shoot, 'pointerup');
  await wait(120);
  check('a tap does not record', t.log, []);
  check('a tap leaves no recording chrome', t.doc.body.dataset.rec || '', '');
}

// 2. A hold records, and the release stops it.
{
  const t = await ready(boot());
  const shoot = t.doc.getElementById('shoot');
  press(shoot, 'pointerdown'); await wait(450);
  check('a hold starts recording', t.log, ['rec:start']);
  check('the shutter shows it', t.doc.body.dataset.rec, '1');
  press(shoot, 'pointerup'); await wait(60);
  check('the release stops it', t.log, ['rec:start', 'rec:stop']);
}

// 3. THE LOCK. Slide onto it, let go, and the recording must still be running.
{
  const t = await ready(boot());
  const shoot = t.doc.getElementById('shoot');
  const lock = t.doc.getElementById('lock');
  lock.getBoundingClientRect = () => ({ left: 90, top: 290, width: 20, height: 20, right: 110, bottom: 310 });

  press(shoot, 'pointerdown'); await wait(450);
  press(shoot, 'pointermove', 1, 100, 300);          // finger arrives at the lock
  check('locked', t.doc.body.dataset.rec, 'locked');

  press(shoot, 'pointerup', 1, 100, 300);            // and lets go
  await wait(60);
  check('LETTING GO KEEPS IT RUNNING', t.log, ['rec:start']);
  check('still shows as locked', t.doc.body.dataset.rec, 'locked');

  press(shoot, 'pointerdown'); press(shoot, 'pointerup');   // the next press is the stop
  await wait(60);
  check('the next press stops it', t.log, ['rec:start', 'rec:stop']);
  check('chrome cleared', t.doc.body.dataset.rec, '');
}

// 4. A drag that never reaches the lock does not lock.
{
  const t = await ready(boot());
  const shoot = t.doc.getElementById('shoot');
  const lock = t.doc.getElementById('lock');
  lock.getBoundingClientRect = () => ({ left: 90, top: 290, width: 20, height: 20, right: 110, bottom: 310 });
  press(shoot, 'pointerdown'); await wait(450);
  press(shoot, 'pointermove', 1, 100, 380);          // still 80px short
  check('not locked yet', t.doc.body.dataset.rec, '1');
  press(shoot, 'pointerup', 1, 100, 380); await wait(60);
  check('so the release still stops it', t.log, ['rec:start', 'rec:stop']);
}

// 5. The phone locking its screen must not lose the clip.
{
  const t = await ready(boot());
  const shoot = t.doc.getElementById('shoot');
  press(shoot, 'pointerdown'); await wait(450);
  Object.defineProperty(t.doc, 'hidden', { value: true, configurable: true });
  t.doc.dispatchEvent(new t.w.Event('visibilitychange'));
  await wait(60);
  check('hiding the page closes the clip off rather than dropping it', t.log, ['rec:start', 'rec:stop']);
}

console.log(failures ? `\n${failures} FAILED` : '\nall good');
process.exit(failures ? 1 : 0);
