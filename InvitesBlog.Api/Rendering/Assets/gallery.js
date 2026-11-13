/*
 * The guest photo album's choosing, sharing and removing; and Share on the one-photo viewer page.
 *
 * Without script the album is a grid of links, each opening that photo's own page, where Download is
 * a link and Remove (for the guest's own) leads to a confirmation page. Nothing on a tile deletes.
 *
 * With script: a long press on a tile (or the Select button) starts choosing; taps then add or take
 * away. A bar at the bottom says how many, and offers Select all / Clear, Share (only where the
 * browser can hand files to the share sheet) and Remove (only when something chosen is the guest's
 * own). Remove asks first, then deletes each one through the same route the confirmation page posts
 * to, asked for as JSON, and takes the tiles away in place.
 *
 * Share fetches the originals (same origin), then opens the share sheet. A browser only allows that
 * straight from a tap; if fetching outlasted it, a "Ready to share" step asks for one more tap.
 */
(() => {
  'use strict';

  if (!window.__ibGallery) return;

  const SHARE_MAX_FILES = 100;
  const SHARE_MAX_BYTES = 2 * 1024 * 1024 * 1024;
  const HOLD_MS = 500;
  const SLOP = 10;

  const $ = (id) => document.getElementById(id);

  function canShareFiles() {
    try {
      return typeof navigator.share === 'function' && typeof navigator.canShare === 'function'
        && navigator.canShare({ files: [new File([new Uint8Array(1)], 'photo.jpg', { type: 'image/jpeg' })] });
    } catch {
      return false;
    }
  }
  const shareable = canShareFiles();

  function extOf(url, video) {
    const path = url.split('?')[0];
    const dot = path.lastIndexOf('.');
    const ext = dot < 0 ? '' : path.slice(dot).toLowerCase();
    return /^\.[a-z0-9]{2,5}$/.test(ext) ? ext : video ? '.mp4' : '.jpg';
  }
  function nameFor(id, url, video, n) {
    const short = id.replace(/-/g, '').slice(0, 8);
    return n == null ? `invitesblog-${short}${extOf(url, video)}` : `invitesblog-${n}-${short}${extOf(url, video)}`;
  }
  function typeOf(blob, url, video) {
    if (blob.type && !blob.type.startsWith('application/')) return blob.type;
    const ext = extOf(url, video);
    return { '.png': 'image/png', '.webp': 'image/webp', '.heic': 'image/heic', '.gif': 'image/gif', '.mp4': 'video/mp4', '.mov': 'video/quicktime', '.webm': 'video/webm' }[ext]
      || (video ? 'video/mp4' : 'image/jpeg');
  }

  // --- Fetching originals, with progress and Cancel ------------------------------------------------

  const busy = $('busy');
  const busyTitle = $('busy-title');
  const busyCount = $('busy-count');
  const busyCancel = $('busy-cancel');
  let abort = null;
  if (busyCancel) busyCancel.addEventListener('click', () => abort && abort.abort());

  /** items: [{ id, url, video }]. Returns File[] or null when stopped or failed. */
  async function fetchAll(items, title) {
    abort = new AbortController();
    const signal = abort.signal;
    const many = items.length > 1;
    const files = new Array(items.length);
    let next = 0;
    let done = 0;
    if (busy) {
      busyTitle.textContent = title;
      busyCount.textContent = `0 of ${items.length}`;
      busy.hidden = false;
    }
    const worker = async () => {
      while (next < items.length) {
        if (signal.aborted) throw new DOMException('Stopped', 'AbortError');
        const at = next++;
        const it = items[at];
        const res = await fetch(it.url, { signal, credentials: 'same-origin' });
        if (!res.ok) throw new Error('fetch');
        const blob = await res.blob();
        files[at] = new File([blob], nameFor(it.id, it.url, it.video, many ? at + 1 : null), { type: typeOf(blob, it.url, it.video) });
        done++;
        if (busyCount) busyCount.textContent = `${done} of ${items.length}`;
      }
    };
    try {
      await Promise.all(Array.from({ length: Math.min(3, items.length) }, worker));
      return signal.aborted ? null : files;
    } catch (e) {
      if (!signal.aborted) alert('One of these could not be fetched. Try again.');
      return null;
    } finally {
      if (busy) busy.hidden = true;
      abort = null;
    }
  }

  // --- The share sheet, with a second tap when the first one has worn off ----------------------------

  const ready = $('ready');
  const readyTitle = $('ready-title');
  let readyFiles = null;
  let afterShare = null;
  if (ready) {
    $('ready-yes').addEventListener('click', () => {
      const files = readyFiles;
      readyFiles = null;
      ready.hidden = true;
      if (files) openSheet(files);
    });
    $('ready-no').addEventListener('click', () => {
      readyFiles = null;
      ready.hidden = true;
    });
  }

  function offer(files) {
    if (!ready) return;
    readyFiles = files;
    readyTitle.textContent = files.length === 1 ? 'Ready to share' : `${files.length} ready to share`;
    const text = $('ready-text');
    if (text) text.textContent = files.length === 1 ? 'Tap Share to choose where it goes.' : 'Tap Share to choose where they go.';
    ready.hidden = false;
    $('ready-yes').focus();
  }

  async function openSheet(files) {
    try {
      await navigator.share({ files });
      if (afterShare) afterShare();
    } catch (e) {
      if (e && e.name === 'NotAllowedError') offer(files);
      else if (!e || e.name !== 'AbortError') alert('Sharing didn’t work. Try again, or download them.');
    }
  }

  async function share(items) {
    if (!items.length) return;
    if (items.length > SHARE_MAX_FILES) {
      alert(`You can share up to ${SHARE_MAX_FILES} at a time, and ${items.length} are selected.`);
      return;
    }
    const files = await fetchAll(items, items.length === 1 ? 'Getting it ready' : 'Getting your photos ready');
    if (!files) return;
    const bytes = files.reduce((n, f) => n + f.size, 0);
    if (bytes > SHARE_MAX_BYTES) {
      alert('These are more than a share can carry (2 GB). Pick fewer.');
      return;
    }
    if (!navigator.canShare({ files })) {
      alert("This browser can't share these files.");
      return;
    }
    if (navigator.userActivation && navigator.userActivation.isActive) openSheet(files);
    else offer(files);
  }

  // --- The one-photo viewer -----------------------------------------------------------------------

  const view = document.querySelector('.view[data-id]');
  const viewShare = $('view-share');
  if (view && viewShare && shareable) {
    viewShare.hidden = false;
    viewShare.addEventListener('click', () => {
      const video = !!view.querySelector('video');
      share([{ id: view.dataset.id, url: view.dataset.original, video }]);
    });
  }

  // --- The album: choosing several ------------------------------------------------------------------

  const grid = document.querySelector('.grid');
  const bar = $('sel');
  if (!grid || !bar) return;

  const pick = $('pick');
  const count = $('sel-count');
  const all = $('sel-all');
  const shareBtn = $('sel-share');
  const removeBtn = $('sel-remove');
  const dialog = $('confirm');
  const yes = $('confirm-yes');
  const no = $('confirm-no');
  const chosen = new Set();
  let choosing = false;
  // Without Premium or Studio (and on an event with no Gold or Platinum pass) the page carries a limit:
  // up to that many at a time, and trying for more explains why (pricing 2026-11).
  const limit = Number(bar.dataset.limit) || 0;
  const limitAsk = $('limit');
  function tooMany() {
    if (limitAsk) limitAsk.hidden = false;
  }
  if (limitAsk) $('limit-no').addEventListener('click', () => { limitAsk.hidden = true; });

  const tiles = () => Array.from(grid.querySelectorAll('.tile'));
  const mineChosen = () => tiles().filter((t) => chosen.has(t.dataset.id) && t.dataset.mine === '1');

  function render() {
    grid.classList.toggle('choosing', choosing);
    document.body.classList.toggle('choosing', choosing);
    bar.hidden = !choosing;
    if (pick) pick.hidden = choosing;
    for (const t of tiles()) {
      const on = choosing && chosen.has(t.dataset.id);
      t.classList.toggle('on', on);
      const link = t.querySelector('a.open');
      if (choosing) {
        link.setAttribute('role', 'checkbox');
        link.setAttribute('aria-checked', String(on));
      } else {
        link.removeAttribute('role');
        link.removeAttribute('aria-checked');
      }
    }
    const n = chosen.size;
    const narrow = window.matchMedia('(max-width: 400px)').matches;
    count.textContent = narrow ? String(n) : `${n} selected`;
    count.setAttribute('aria-label', `${n} selected`);
    const total = tiles().length;
    all.textContent = (n === total && total > 0) || (limit && n >= limit) ? 'Clear' : (limit && total > limit ? `Select ${limit}` : 'Select all');
    shareBtn.hidden = !shareable;
    shareBtn.disabled = n === 0;
    removeBtn.hidden = mineChosen().length === 0;
  }

  function start(id) {
    choosing = true;
    chosen.clear();
    if (id) chosen.add(id);
    render();
  }
  function stop() {
    choosing = false;
    chosen.clear();
    render();
  }
  function toggle(id) {
    if (!chosen.delete(id)) {
      if (limit && chosen.size >= limit) {
        tooMany();
        return;
      }
      chosen.add(id);
    }
    render();
  }
  afterShare = stop;

  if (pick) {
    pick.hidden = false;
    pick.addEventListener('click', () => start(null));
  }
  $('sel-cancel').addEventListener('click', stop);
  all.addEventListener('click', () => {
    const ids = tiles().map((t) => t.dataset.id);
    if (chosen.size === ids.length || (limit && chosen.size >= limit)) chosen.clear();
    else {
      for (const id of ids) {
        if (limit && chosen.size >= limit) break;
        chosen.add(id);
      }
      if (limit && ids.length > limit) tooMany();
    }
    render();
  });
  shareBtn.addEventListener('click', () => {
    const items = tiles().filter((t) => chosen.has(t.dataset.id))
      .map((t) => ({ id: t.dataset.id, url: t.dataset.original, video: t.dataset.kind === 'video' }));
    share(items);
  });

  // Long press: touch events for a finger (a browser sends no pointer events for a touch it is
  // treating as a scroll), pointer events for a mouse. Moving, scrolling or lifting first cancels it.
  let timer = null;
  let origin = null;
  let fired = false;
  function cancelHold() {
    if (timer) clearTimeout(timer);
    timer = null;
    origin = null;
  }
  function hold(tile, x, y) {
    cancelHold();
    fired = false;
    origin = { x, y };
    timer = setTimeout(() => {
      timer = null;
      fired = true;
      try { navigator.vibrate && navigator.vibrate(10); } catch { /* not allowed */ }
      if (!choosing) start(tile.dataset.id);
      else toggle(tile.dataset.id);
    }, HOLD_MS);
  }
  function moved(x, y) {
    if (origin && Math.hypot(x - origin.x, y - origin.y) > SLOP) cancelHold();
  }
  let touched = false;
  grid.addEventListener('touchstart', (e) => {
    touched = true;
    const tile = e.target.closest('.tile');
    if (!tile || e.touches.length > 1) return cancelHold();
    const t = e.touches[0];
    hold(tile, t.clientX, t.clientY);
  }, { passive: true });
  grid.addEventListener('touchmove', (e) => moved(e.touches[0].clientX, e.touches[0].clientY), { passive: true });
  grid.addEventListener('touchend', cancelHold, { passive: true });
  grid.addEventListener('touchcancel', cancelHold, { passive: true });
  grid.addEventListener('pointerdown', (e) => {
    if (touched || e.pointerType !== 'mouse' || e.button !== 0) return;
    const tile = e.target.closest('.tile');
    if (tile) hold(tile, e.clientX, e.clientY);
  });
  grid.addEventListener('pointermove', (e) => { if (e.pointerType === 'mouse') moved(e.clientX, e.clientY); });
  grid.addEventListener('pointerup', (e) => { if (e.pointerType === 'mouse') cancelHold(); });
  window.addEventListener('scroll', cancelHold, { passive: true });
  // The phone's own long-press menu (save image, open in new tab) would open here.
  grid.addEventListener('contextmenu', (e) => {
    if (e.target.closest('.tile')) e.preventDefault();
  });
  grid.addEventListener('dragstart', (e) => e.preventDefault());

  grid.addEventListener('click', (e) => {
    const tile = e.target.closest('.tile');
    if (!tile) return;
    if (fired) {
      // The click a long press leaves behind.
      fired = false;
      e.preventDefault();
      return;
    }
    if (!choosing) return; // an ordinary link: opens the photo's page
    e.preventDefault();
    toggle(tile.dataset.id);
  });

  // --- Remove the chosen ones that are the guest's own --------------------------------------------

  let removing = false;
  removeBtn.addEventListener('click', () => {
    const mine = mineChosen();
    if (!mine.length || !dialog) return;
    const others = chosen.size - mine.length;
    $('confirm-title').textContent = mine.length === 1 ? 'Remove 1 item?' : `Remove ${mine.length} items?`;
    $('confirm-text').textContent = "They disappear from everyone's photo box. This cannot be undone."
      + (others > 0 ? ` The other ${others} selected aren't yours to remove and stay.` : '');
    yes.textContent = 'Remove them';
    no.textContent = 'Keep them';
    dialog.hidden = false;
    yes.focus();
  });

  function closeAsk() {
    if (removing) return;
    dialog.hidden = true;
    yes.disabled = false;
  }
  if (dialog) {
    no.addEventListener('click', closeAsk);
    dialog.addEventListener('click', (e) => { if (e.target === dialog) closeAsk(); });
    document.addEventListener('keydown', (e) => { if (e.key === 'Escape' && !dialog.hidden) closeAsk(); });
    yes.addEventListener('click', async () => {
      const mine = mineChosen();
      if (!mine.length || removing) return;
      removing = true;
      yes.disabled = true;
      yes.textContent = 'Removing…';
      let failed = false;
      for (const tile of mine) {
        const base = tile.querySelector('a.open').getAttribute('href');
        try {
          // The confirmation page's own POST target, asked for as JSON (application/json): the same
          // route and authority, never a second way in.
          const res = await fetch(`${base}/delete`, {
            method: 'POST',
            credentials: 'same-origin',
            headers: { Accept: 'application/json' },
          });
          if (!res.ok) throw new Error(String(res.status));
          chosen.delete(tile.dataset.id);
          tile.remove();
        } catch {
          failed = true;
          break;
        }
      }
      removing = false;
      dialog.hidden = true;
      yes.disabled = false;
      if (failed) alert('Some of these could not be removed. Try again.');
      recount();
      stop();
    });
  }

  /** Keep the heading honest without asking the server for a number it already sent. */
  function recount() {
    const n = tiles().length;
    const sub = document.querySelector('.sub');
    if (sub) sub.textContent = `${n === 1 ? '1 photo' : n + ' photos'} from the event`;
    if (n === 0) window.location.reload();
  }

  render();
})();
