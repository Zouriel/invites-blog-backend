/*
 * The guest photo gallery's delete interaction.
 *
 * Progressive enhancement over links that already work: every Remove is an <a> to a confirmation
 * PAGE, which is what a browser with no script still gets. That page costs a full round trip before
 * the question even appears, and the POST behind it re-renders the whole grid — around a second per
 * photo on venue wifi, with the wait landing before any feedback. This makes the question instant
 * and the delete a single small request, and removes the tile in place.
 */
(() => {
  'use strict';

  const cfg = window.__ibGallery;
  if (!cfg) return;

  const grid = document.querySelector('.grid');
  if (!grid) return;

  const dialog = document.getElementById('confirm');
  const yes = document.getElementById('confirm-yes');
  const no = document.getElementById('confirm-no');
  if (!dialog || !yes || !no) return;

  let pending = null;

  function ask(tile, url) {
    pending = { tile, url };
    dialog.hidden = false;
    yes.focus();
  }

  function close() {
    pending = null;
    dialog.hidden = true;
    yes.disabled = false;
    yes.textContent = 'Remove it';
  }

  // Delegated, so tiles added or removed never need rebinding.
  grid.addEventListener('click', (e) => {
    const link = e.target.closest('a.rm');
    if (!link) return;
    e.preventDefault();
    ask(link.closest('.tile'), link.getAttribute('href'));
  });

  no.addEventListener('click', close);
  dialog.addEventListener('click', (e) => { if (e.target === dialog) close(); });
  document.addEventListener('keydown', (e) => { if (e.key === 'Escape' && !dialog.hidden) close(); });

  yes.addEventListener('click', async () => {
    if (!pending) return;
    const { tile, url } = pending;

    yes.disabled = true;
    yes.textContent = 'Removing…';

    try {
      // The confirmation page's own POST target, asked for as JSON. Same route, same authority —
      // the enhanced path must not be a second way in.
      const res = await fetch(url.replace(/\/remove$/, '/delete'), {
        method: 'POST',
        credentials: 'same-origin',
        headers: { Accept: 'application/json' },
      });
      if (!res.ok) throw new Error(String(res.status));

      tile.remove();
      recount();
      close();
    } catch {
      // Falling back to the page flow beats leaving them stuck: it is slower, but it works.
      window.location.href = url;
    }
  });

  /** Keep the heading honest without asking the server for a number it already sent. */
  function recount() {
    const n = grid.querySelectorAll('.tile').length;
    const sub = document.querySelector('.sub');
    if (sub) sub.textContent = `${n === 1 ? '1 photo' : n + ' photos'} from the night`;
    if (n === 0) window.location.reload();
  }
})();
