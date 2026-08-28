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

let fault = null;

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
  /*
   * `fault` reproduces what a real MediaRecorder does on a phone that claims more than it can do:
   *   'dies'      - starts, then fails mid-clip and goes inactive WITHOUT firing onstop
   *   'liesAtStart' - start() throws nothing but leaves the state inactive
   *   'needsVideoOnly' - refuses any stream carrying an audio track
   */
  const made = [];
  class FakeRecorder {
    static isTypeSupported() { return true; }
    constructor(stream, opts) {
      this.state = 'inactive';
      this.mimeType = (opts && opts.mimeType) || 'video/mp4';
      this.tracks = (stream && stream.t) || [];
      made.push(this);
      if (fault === 'needsVideoOnly' && this.tracks.some((t) => t.kind === 'audio')) {
        throw new Error('cannot record audio here');
      }
    }
    start() {
      if (fault === 'liesAtStart') { log.push('rec:start-failed'); return; }
      this.state = 'recording';
      log.push('rec:start');
    }
    stop() {
      this.state = 'inactive';
      log.push('rec:stop');
      this.ondataavailable?.({ data: { size: 999999 } });
      this.onstop?.();
    }
    /** Fails the way that leaves a shutter stuck red: inactive, and onstop never fires. */
    die() { this.state = 'inactive'; log.push('rec:died'); this.onerror?.(new Error('encoder gone')); }
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
  return { w, doc: w.document, log, made };
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

// 6. THE REPORTED BUG. The recorder starts, then dies without firing onstop.
{
  const t = await ready(boot());
  const shoot = t.doc.getElementById('shoot');
  press(shoot, 'pointerdown'); await wait(450);
  check('recording', t.doc.body.dataset.rec, '1');

  t.made[t.made.length - 1].die();
  await wait(400);                                   // the clock is the watchdog

  check('a dead recorder does not leave the shutter red', t.doc.body.dataset.rec || '', '');
  press(shoot, 'pointerup'); await wait(60);
}

// 7. And a press while it is stuck must clear it, not quietly take a photograph.
{
  const t = await ready(boot());
  const shoot = t.doc.getElementById('shoot');
  press(shoot, 'pointerdown'); await wait(450);

  const rec = t.made[t.made.length - 1];
  rec.state = 'inactive';                            // died with no error and no onstop at all
  t.w.clearInterval = () => {};                      // and the watchdog never got to run

  press(shoot, 'pointerup'); await wait(60);
  check('a press on a stuck shutter clears it', t.doc.body.dataset.rec || '', '');
  check('and takes no photograph', t.log.filter((l) => l === 'shot'), []);
}

// 8. A phone that refuses audio still records, silently, rather than not at all.
{
  fault = 'needsVideoOnly';
  const t = await ready(boot());
  await wait(80);                                    // let the microphone be picked up
  const shoot = t.doc.getElementById('shoot');
  press(shoot, 'pointerdown'); await wait(450);

  check('it falls back to video alone', t.log.includes('rec:start'), true);
  check('and says it is recording', t.doc.body.dataset.rec, '1');
  check('with no audio track on the recorder',
    t.made[t.made.length - 1].tracks.some((x) => x.kind === 'audio'), false);
  press(shoot, 'pointerup'); await wait(60);
  fault = null;
}

// 9. A start that silently does not start leaves nothing behind.
{
  fault = 'liesAtStart';
  const t = await ready(boot());
  const shoot = t.doc.getElementById('shoot');
  press(shoot, 'pointerdown'); await wait(500);

  check('a start that never started shows no recording', t.doc.body.dataset.rec || '', '');
  press(shoot, 'pointerup'); await wait(60);
  fault = null;
}

// 10. The reported shape exactly: locked, red, and a clock frozen at the 0:00 it was born with.
//     Whatever throws on a real phone, it must not be able to strand the shutter.
{
  const t = await ready(boot());
  const shoot = t.doc.getElementById('shoot');
  const lock = t.doc.getElementById('lock');
  lock.getBoundingClientRect = () => ({ left: 90, top: 290, width: 20, height: 20, right: 110, bottom: 310 });

  // The clock cannot draw itself — the one failure that used to happen BEFORE the interval existed,
  // so nothing was left running that could ever notice.
  const clock = t.doc.getElementById('rectime');
  Object.defineProperty(clock, 'textContent', { set() { throw new Error('no clock'); }, get: () => '0:00' });

  press(shoot, 'pointerdown'); await wait(450);
  press(shoot, 'pointermove', 1, 100, 300);
  press(shoot, 'pointerup', 1, 100, 300);
  await wait(500);

  check('a clock that cannot draw does not stand the shutter red', t.doc.body.dataset.rec || '', '');

  // And the shutter still works afterwards rather than being wedged.
  press(shoot, 'pointerdown'); await wait(40); press(shoot, 'pointerup'); await wait(60);
  check('the shutter still works after that', t.doc.body.dataset.rec || '', '');
}

// 11. A clock that ticks. Frozen at 0:00 was the symptom; this is the thing it was a symptom of.
{
  const t = await ready(boot());
  const shoot = t.doc.getElementById('shoot');
  press(shoot, 'pointerdown'); await wait(450);
  const started = t.doc.getElementById('rectime').textContent;
  await wait(1200);
  const later = t.doc.getElementById('rectime').textContent;
  check('the clock actually advances', [started, later], ['0:00', '0:01']);
  press(shoot, 'pointerup'); await wait(60);
}

console.log(failures ? `\n${failures} FAILED` : '\nall good');
process.exit(failures ? 1 : 0);
