# Creating invites.blog Templates (raw HTML/CSS)

A template is **one self-contained `index.html`** you write by hand — your markup, your CSS in an inline
`<style>`, and (optionally) your JS in an inline `<script>`, all in that single file. Since only you
upload them, you **may include your own `<script>`** for custom animation (see "Custom JavaScript" below).
Separate `.css` files and external `<link>`/`<script src>` are rejected on upload — everything lives in
the one file.
The platform injects a trusted script that fills in the data. You place special **`data-*` tags** on
elements; at send time the system finds those tags and injects each guest's personalized info into them,
inside a sandboxed iframe.

Only an admin (you) can add templates. Once added, they appear in the public gallery for inviters to pick.

A template's **category** is now a managed **template type** (an entity, not free text). Manage the list
in the admin templates page (add / deactivate), and pick a type when uploading. The seeded types are
Wedding, Engagement, Anniversary, Birthday, Baby Shower, Graduation, Ceremony, Religious Event, Corporate
Event, Conference, Workshop, Launch Event, Private Dinner, Custom Event — add your own any time.

---

## The tags (this is the whole contract)

| Tag | What it does | Example |
|---|---|---|
| `data-var="PATH"` | Sets the element's **text** to the value at `PATH` | `<h1 data-var="event.title"></h1>` |
| `data-href="PATH"` | Sets the element's **href** (links) | `<a data-href="rsvp.link">RSVP</a>` |
| `data-src="PATH"` | Sets the element's **src** (images) | `<img data-src="event.coverImage">` |
| `data-block="ID"` | Marks a **conditional section** shown only to matching guests (role/gender). Sections whose `ID` no rule references are treated as neutral and **always shown**. | `<section data-block="maleDressCode">…</section>` |
| `data-optional` | Marks a wrapper that is **auto-hidden when its field is empty**. Put it on the element (or a wrapper around a label + `data-var`) for any field the inviter might leave blank — if the bound value is empty the whole element is `display:none`, so no stray label/blank row shows. | `<p data-optional>Dress code: <span data-var="event.dressCode"></span></p>` |
| `data-reveal` | Element gets the class **`is-visible`** when scrolled into view — animate it in your CSS via a transition on `.is-visible` | `<section data-reveal>…</section>` |
| `data-envelope` | The cover element gets the class **`is-open`** after the first scroll — animate a seal/flap opening | `<header data-envelope>…</header>` |

You style everything else yourself. Use a `transition` + the `.is-visible` / `.is-open` classes for
animation, and a `@media (prefers-reduced-motion: reduce)` block that turns motion off (required for
accessibility).

---

## Available data paths (use these in `data-var` / `data-href` / `data-src`)

```
event.title            event.subtitle          event.description
event.date             event.time              event.schedule        event.dressCode
event.venue.name       event.venue.address     event.venue.mapLink
guest.name             guest.role              guest.gender
inviter.name           inviter.phone           inviter.email
invite.link            rsvp.link               rsvp.status
```

Any path missing for a given guest simply renders empty — always write sensible fallback text between
the tags (e.g. `<h1 data-var="event.title">Our Celebration</h1>`), which shows until data loads and if a
value is absent.

**Most fields are now optional** — inviters can leave things blank in the builder. So a good template
gives *every* field a place, and wraps the ones that may be empty in **`data-optional`** so blank values
disappear cleanly instead of leaving a dangling label. Rule of thumb: only `event.title` and `event.date`
are effectively always present; wrap the rest (dress code, schedule, subtitle, venue address/map, second
host, etc.) in `data-optional`.

---

## Conditional blocks (role / gender content)

Put a `data-block="someId"` on a section. Whether a guest sees it is decided by the **campaign's rules**
(set by the inviter or an admin default), which map a guest attribute to a block id, e.g.:

```json
{ "rules": [
  { "condition": { "field": "role",   "operator": "equals", "value": "bridesmaid" }, "contentBlock": "bridesmaidInstructions" },
  { "condition": { "field": "gender", "operator": "equals", "value": "male" },       "contentBlock": "maleDressCode" },
  { "condition": { "field": "gender", "operator": "equals", "value": "female" },     "contentBlock": "femaleDressCode" }
] }
```

Operators: `equals`, `notEquals`, `in`, `notIn`, `exists`, `notExists`. **A block that no rule mentions
is always shown** — so put universal content in blocks with no rule (or in no block at all), and always
give every guest a complete invite.

Suggested block ids (just conventions — you can name them anything, then map rules to them):
`bridesmaidInstructions`, `groomsmenInstructions`, `maleDressCode`, `femaleDressCode`, `vipSchedule`,
`familyNote`.

---

## Custom JavaScript & animation (allowed for your templates)

Because only you (admin) upload templates, **you can include your own `<script>`** for richer animation
(GSAP, canvas, WebGL, custom scroll-scrubbing, etc.). The platform still binds your `data-*` tags and
handles the iOS-safe scroll — your JS runs alongside it. Two hooks are provided:

```html
<script>
  // 1) React to the guest's data (fires once it's injected; also on window.invite.data).
  window.addEventListener('invite:data', (e) => {
    const d = e.detail;            // { event, guest, venue, inviter, rsvp, resolvedBlocks, ... }
    // e.g. start a name-reveal animation for d.guest.name
  });

  // 2) React to scroll progress 0..1 (the platform scrolls the page; you scrub your animation).
  window.addEventListener('invite:progress', (e) => {
    const p = e.detail;           // 0 (top) .. 1 (end)
    // e.g. gsap.to('.hero', { opacity: 1 - p }) or drive a canvas frame
  });
</script>
```

> If you ever open **community** template submissions, uploads from non-admins are stripped of JS
> automatically — the JS freedom applies to trusted/admin templates only.

## Rules & constraints (the sandbox)

- **One self-contained file — inline everything.** The template runs under a strict CSP in a sandboxed
  iframe, and separate/external files are rejected: put CSS in a `<style>` tag, JS in a `<script>` tag,
  paste library code inline (e.g. minified GSAP), and embed images as `data:` URIs. (Want a specific CDN allow-listed
  instead? Ask and I'll add it to the CSP.)
- **Keep it light.** Aim to keep the package under ~300KB critical path; lazy-load / keep images small.
- **Respect reduced motion** with a `@media (prefers-reduced-motion: reduce)` block (and check
  `matchMedia('(prefers-reduced-motion: reduce)')` in JS).
- **Guest/inviter values are injected as text** (never HTML) via `data-var`, so they can't inject markup —
  keep using the tags for content and your JS for motion.

---

## How to add a template

### Option A — commit it to the repo (recommended for you)
In `invites-blog-backend`, create a folder `InvitesBlog.Infrastructure/RawTemplates/<your-slug>/` with:
```
RawTemplates/aurora-vows/
  index.html     # the whole template: markup + inline <style> + optional inline <script>
  meta.json      # { "name","slug","version","category","description" }
```
`meta.json` example:
```json
{ "name": "Aurora Vows", "slug": "aurora-vows", "version": "1.0.0",
  "category": "Wedding", "description": "A warm gold-on-ink wedding invite." }
```
Commit + push, then on the server: `git -C /opt/apps/invites-blog-backend pull && \
cd /opt/apps/invites-blog-deploy && docker compose -f compose.prod.yml up -d --build api`.
The template is packaged, its manifest auto-derived from your tags, and it appears in the gallery.
**A full working reference lives at `RawTemplates/aurora-vows/`** — copy it to start.

### Option B — upload at runtime (admin API)
```bash
# 1) get an admin token
TOKEN=$(curl -s -X POST https://invites.blog/api/admin/login \
  -H 'Content-Type: application/json' \
  -d '{"email":"admin@invites.blog","password":"YOUR_ADMIN_PASSWORD"}' \
  | python3 -c 'import sys,json;print(json.load(sys.stdin)["data"]["token"])')

# 2) upload the template (a single self-contained index.html)
curl -s -X POST https://invites.blog/api/admin/templates \
  -H "Authorization: Bearer $TOKEN" \
  -F name="Aurora Vows" -F slug="aurora-vows" -F version="1.0.0" \
  -F category="Wedding" -F description="A warm gold-on-ink wedding invite." \
  -F index=@index.html
```
The response lists the `variables` and `contentBlocks` the system detected from your tags — a quick way
to confirm your tags are right. Re-uploading the same `slug`+`version` updates it in place.

---

## Minimal starter (one file)

```html
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>My Template</title>
  <style>
    .panel{opacity:0;transform:translateY(40px);transition:opacity .8s,transform .8s}
    .panel.is-visible{opacity:1;transform:none}
    .cover.is-open{/* animate your seal/flap */}
    @media (prefers-reduced-motion: reduce){.panel{opacity:1;transform:none;transition:none}}
  </style>
</head>
<body>
  <header class="cover" data-envelope>
    <h1 data-var="event.title">Our Celebration</h1>
    <p>Dear <span data-var="guest.name">Guest</span></p>
    <p class="hint">Scroll ↓</p>
  </header>

  <section class="panel" data-reveal>
    <p data-var="event.date">The date</p>
    <p data-var="event.venue.name">The venue</p>
    <a class="btn" data-href="rsvp.link" href="#">RSVP</a>
  </section>

  <section class="panel" data-reveal data-block="maleDressCode"><p>Gentlemen: formal suit.</p></section>
  <section class="panel" data-reveal data-block="femaleDressCode"><p>Ladies: evening formal.</p></section>

  <!-- optional: your own animation -->
  <script>
    window.addEventListener('invite:progress', (e) => { /* scrub with e.detail (0..1) */ });
  </script>
</body>
</html>
```

That's it — one `index.html` with inline `<style>` (and optional `<script>`), drop in the `data-*` tags,
add it, and it's live in the gallery.
