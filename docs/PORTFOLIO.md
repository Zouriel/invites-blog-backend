# Studio portfolio (owner-approved 2026-11-13, URGENT: owner is approaching photographers now)

Single spec for backend, web (`invites-blog-frontend`: public page on invites.blog + controls on app.invites.blog),
Flutter app (controls only) and both UI libraries. Copy into each repo's `docs/` unchanged.

## 1. Who and where
- Every **Studio** account (plan kind `Venue`, shown "Studio"): photographers, venues, resorts, planners.
- Public page: **`https://invites.blog/@handle`** (public site, indexed, prerendered/SSR so link previews and Google
  work; OG image = cover). Loads in the browser, and in the app's embedded browser exactly like invitations do.
- Controls (the CMS): app.invites.blog (web) and the Flutter app. The app NEVER renders the portfolio natively; its
  Portfolio screen is only the controls plus "View" (embedded browser) and "Share link".
- App top bar: for Studio accounts the **Portfolio** button sits where Get Premium is today (Studio accounts no longer
  see Get Premium). Non-Studio accounts keep Get Premium. Same on web where the app shows Get Premium.

## 2. Handle
- `@handle`: 3-30 chars, a-z 0-9 . _ (stored lowercase, compared case-insensitively), UNIQUE across all accounts.
- Reserved words refused (admin, api, app, invites, invitesblog, support, help, studio, pricing, terms, privacy, ...
  plus every top-level route of invites.blog).
- Picker checks availability live as they type, suggests one from the studio's name.
- Changing it: old handle redirects (301) to the new one for 30 days, then is free.

## 3. Consent and choosing photos (NO celebrant involvement)
- Per event, once: the Studio confirms "The people this event is for agreed to its photos being shown in my
  portfolio" (a switch/tick in the event's album settings; recorded: who, when). No notice to celebrants, no opt-out
  flow for them.
- Only after that, in that event's album, when the Studio **selects photos** (existing long-press / select mode), the
  selection bar shows an **Add to portfolio** action (icon) -> pick or create a collection.
- Only photos the Studio account itself uploaded can be added (not guest uploads); others in the selection are
  skipped with a short note ("3 photos added. 2 were added by guests and can't be used.").
- Turning consent off for an event removes that event's photos from the portfolio (with a confirm).
- Portfolio photos are web-sized copies (e.g. 2048px WebP + thumb) that don't count against event space; deleting the
  event/album doesn't remove them. Limit 500 photos per portfolio.
- Public page footer: small "Ask to remove a photo" link (a report to us/the studio; no account needed).

## 4. The page (studio decides what goes where)
Sections the studio adds, removes, hides and drags into order:
- Cover (photo, logo, name, one line) - always first.
- Collections (each: title, cover photo, photo order by drag, layout: grid / masonry / slideshow).
- About (short text + photo).
- Featured invitation designs (their published templates), optional.
- Packages & prices (simple cards: name, price text, bullet lines), optional.
- Reviews (quote + name), optional.
- Contact: buttons for WhatsApp, Viber, Instagram, phone, email, and an enquiry form (name, contact, event date,
  message) -> studio's inbox notification + email. Use the existing `inquiries` table if it fits.
Look: theme (UI library themes incl. oxblood), accent colour, font pair, all as named choices with previews.
Publish / Unpublish switch. Unpublished or Studio lapsed -> page shows "This portfolio isn't available"; data kept
90 days after Studio ends.

## 5. Easy for non-technical people
- First time: 5-step setup wizard: handle -> logo & name -> look -> first collection (pick an event, tick consent,
  pick photos) -> Publish. Skippable steps except handle.
- Editor: list of section cards with drag handles, eye (hide), pencil (edit), trash; a sticky "Preview" and "Share".
- **Icons everywhere** (hugeicons via the app-icons convention; no text glyphs), short labels, no numbers to type,
  plain words, big touch targets, works at 360px.
- Every new UI piece goes into BOTH UI libraries first (section list with drag/hide/edit, handle field with live
  check, theme picker cards if missing), released, then used.

## 6. API (backend writes exact shapes to docs/PORTFOLIO-HANDOFF.md)
Studio-only endpoints under `/api/me/portfolio` (get/update settings, handle check/set, sections CRUD + order,
collections CRUD + photo order, add photos from an album, remove photos, publish), per-event consent
`PUT /api/me/campaigns/{id}/portfolio-consent`, public `GET /api/portfolio/@{handle}` (cached), enquiry
`POST /api/portfolio/@{handle}/enquiries` (rate limited via ClientAddress, spam-safe), remove-request
`POST /api/portfolio/@{handle}/report`.
