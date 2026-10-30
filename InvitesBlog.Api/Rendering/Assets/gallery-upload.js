/*
 * Choosing many photos on the guest gallery.
 *
 * Progressive enhancement over a form that already works: without this, every picked photo travels in
 * one request and one dropped connection loses them all. With it, each goes straight to storage on its
 * own, a few at a time, with retries, and the server finishes the work after the guest has moved on.
 * Anything this can't send direct (no holding area, a video, the switch is off) goes to the form's own
 * route, one file at a time.
 */
(() => {
  'use strict';

  const form = document.querySelector('.bar form[enctype]');
  if (!form || !window.fetch) return;
  const input = form.querySelector('input[type=file]');
  const button = form.querySelector('button[type=submit]');
  if (!input || !button) return;

  const WORKERS = 3;
  const TRIES = 4;
  const action = form.getAttribute('action');

  const status = document.createElement('p');
  status.className = 'sub';
  status.setAttribute('role', 'status');
  status.hidden = true;
  form.parentElement.parentElement.prepend(status);

  const wait = (ms) => new Promise((r) => setTimeout(r, ms));
  const say = (text) => { status.hidden = false; status.textContent = text; };

  /** A refusal is an answer: asking again gets the same one. Everything else is worth another go. */
  class Refused extends Error {}

  async function postJson(url, body) {
    const res = await fetch(url, {
      method: 'POST',
      credentials: 'same-origin',
      headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
      body: body === undefined ? undefined : JSON.stringify(body),
    });
    if (res.ok) {
      const json = await res.json();
      return json && json.data !== undefined ? json.data : json;
    }
    if (res.status >= 400 && res.status < 500 && res.status !== 429 && res.status !== 408) {
      const json = await res.json().catch(() => null);
      throw new Refused((json && (json.error || json.message)) || 'That photo could not be added.');
    }
    throw new Error(String(res.status));
  }

  async function sendOne(file) {
    const link = await postJson(`${action}/uploads`, {
      fileName: file.name, contentType: file.type, size: file.size,
    });
    if (!link.direct) {
      const body = new FormData();
      body.append('files', file, file.name);
      const res = await fetch(action, { method: 'POST', credentials: 'same-origin', body });
      if (!res.ok) throw new Error(String(res.status));
      return null;
    }

    const put = await fetch(link.url, { method: 'PUT', headers: { 'Content-Type': link.contentType }, body: file });
    if (!put.ok) throw new Error(String(put.status));

    const done = await postJson(`/api/uploads/${link.id}/complete`);
    if (done.status === 'failed') throw new Refused(done.error || 'That photo could not be added.');
    return link.id;
  }

  async function sendWithRetries(file) {
    for (let attempt = 1; ; attempt++) {
      try {
        return await sendOne(file);
      } catch (e) {
        if (e instanceof Refused || attempt >= TRIES) throw e;
        await wait(1000 * 2 ** attempt);
      }
    }
  }

  /** Waits for the server to finish, but not for long: the photos land whether anyone waits or not. */
  async function settle(ids) {
    const refused = [];
    const deadline = Date.now() + 60000;
    let open = ids;
    while (open.length && Date.now() < deadline) {
      await wait(2000);
      const batch = open.slice(0, 50);
      const rows = await postJson('/api/uploads/status', { ids: batch }).catch(() => null);
      if (!rows) continue;
      for (const row of rows) {
        if (row.status === 'failed') refused.push(row.error);
        if (row.status !== 'processing') open = open.filter((id) => id !== row.id);
      }
    }
    return refused;
  }

  async function run(files) {
    input.disabled = true;
    button.disabled = true;

    // Best effort: a locked phone can stop an upload, and not every browser lets us ask.
    let lock = null;
    try { lock = await navigator.wakeLock.request('screen'); } catch { /* carry on */ }

    const total = files.length;
    const queue = files.slice();
    const failed = [];
    const ids = [];
    let sent = 0;
    let lastError = null;
    say(`Adding ${total === 1 ? 'your photo' : `0 of ${total} photos`}. Keep this page open.`);

    const worker = async () => {
      for (let file = queue.shift(); file; file = queue.shift()) {
        try {
          const id = await sendWithRetries(file);
          if (id) ids.push(id);
          sent++;
        } catch (e) {
          failed.push(file);
          if (e instanceof Refused) lastError = e.message;
        }
        say(`Adding ${sent} of ${total} photos. Keep this page open.`);
      }
    };
    await Promise.all(Array.from({ length: WORKERS }, worker));

    if (ids.length) say('Nearly there. Your photos are being added, and you can close this page.');
    const refused = await settle(ids);
    if (lock) lock.release().catch(() => {});

    if (!failed.length && !refused.length) {
      window.location.reload();
      return;
    }

    input.disabled = false;
    button.disabled = false;
    const bad = failed.length + refused.length;
    say(`${bad === 1 ? '1 photo' : `${bad} photos`} couldn't be added. ${lastError || refused[0] || 'Check your connection and choose them again.'}`);
  }

  form.addEventListener('submit', (e) => {
    const files = Array.from(input.files || []);
    if (!files.length) return;
    e.preventDefault();
    run(files).catch(() => {
      // Falling back to the page flow beats leaving them stuck.
      input.disabled = false;
      button.disabled = false;
      form.submit();
    });
  });
})();
