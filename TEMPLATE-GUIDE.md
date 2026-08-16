# Making an invites.blog template

A template is **one HTML file**. You write the markup and put your CSS in a `<style>` tag — all in
that single file. That's it. (No JavaScript — see *Animation* below; the platform handles motion.)

You mark the spots that should be filled in with little `data-*` tags. When an invite is sent, the
platform fills those spots with the event's details and each guest's personal info, inside a safe
sandboxed frame. Admins upload templates directly; community designers submit them for review
(see *Submitting as a designer* below). Either way they show up in the gallery once published.

**The big idea:** the builder is now driven by *your* template. It shows the inviter **exactly the
fields your template declares** — no more, no less. Add a `data-var` for `event.hashtag` and a
"Hashtag" box appears in the builder automatically. Add an `<img data-src>` and an image upload slot
appears. You never touch any other code.

---

## The tags

| Tag | What it does | Example |
|---|---|---|
| `data-var="PATH"` | Fills the element's **text** | `<h1 data-var="event.title">`
| `data-href="PATH"` | Fills a **link** (href) | `<a data-href="rsvp.link">RSVP</a>`
| `data-src="PATH"` | Fills an **image** (src) — becomes an upload slot in the builder | `<img data-src="event.coverImage">`
| `data-block="ID"` | A **section shown only to some guests** (by role/gender). A block no rule mentions is shown to everyone. | `<section data-block="maleDressCode">`
| `data-optional` | **Hides the element when its value is empty** — put it on anything that might be left blank so no empty label shows | `<p data-optional>Dress code: <span data-var="event.dressCode"></span></p>`
| `data-reveal` | Gets the class `is-visible` when scrolled into view — animate it in your CSS | `<section data-reveal>`
| `data-envelope` | The cover gets `is-open` after the first scroll — animate a seal/flap | `<header data-envelope>`

### Optional hints to make the builder nicer

These are optional — the builder works without them, but they polish the inviter's experience:

| Hint | Put it on | Does |
|---|---|---|
| `data-field-label="Gift note"` | a `data-var`/`data-href` element | Sets the box's label (otherwise it's guessed from the path) |
| `data-type="textarea"` | a `data-var` element | Forces the input kind — see the table below |
| `data-options="Formal,Casual"` | a `data-type="select"` element | The allowed values of the dropdown (**required** for `select`) |
| `data-slot-label="Cover photo"` | a `data-src` image | Names the image slot in the builder |
| `data-multiple="true"` | a `data-src` image | Makes the slot a **gallery** — the inviter adds/reorders/removes a list of photos |
| `data-min-images="2"` / `data-max-images="8"` | a `data-multiple` image | Bounds the gallery (both optional, unbounded by default) |
| `data-role-scope="groom"` | any `data-var`/`data-href`/`data-src` element | Marks the field/slot as belonging to **one role** instead of being shared by all |

`data-field-type` is the old name for `data-type` and still works; if an element carries both,
`data-type` wins. If you set neither, the builder guesses well: paths containing *date* get a date
picker, *time* a time picker, *description/schedule/note/message/address* a multi-line box, links a
URL box, everything else a normal text box.

#### The input kinds (`data-type`)

| `data-type` | Inviter gets |
|---|---|
| `text` | a single-line box |
| `textarea` | a multi-line box |
| `date` | a date picker |
| `time` | a time picker |
| `url` | a link box |
| `color` | a colour picker |
| `select` | a dropdown of your `data-options` — **must** be paired with `data-options` or the upload is rejected |
| `image` | an upload slot (usually you just use `data-src` instead) |

`data-options` takes either a comma list (`data-options="Formal,Casual,Black Tie"`) or a JSON array
(`data-options='["Formal","Casual"]'`).

```html
<span data-var="event.dressCode" data-type="select" data-options="Formal,Casual,Black Tie">Formal</span>
```

---

## Galleries (several photos in one slot)

A normal `<img data-src>` is **one** photo. Add `data-multiple="true"` and the same slot becomes a
gallery — the inviter uploads as many pictures as they like and every image carrying that path is
repeated for them:

```html
<div class="photo-strip">
  <img data-src="event.gallery" data-multiple="true" data-min-images="2" data-max-images="8"
       data-slot-label="Photo strip" alt="">
</div>
```

`data-min-images` / `data-max-images` are optional — leave them off for unbounded. They're ignored
unless `data-multiple` is set.

---

## Theming (`--ib-*`)

Declare your palette as CSS custom properties named `--ib-…` in `:root`, with the value you want as
the default. The platform reads them out of your file and offers each one as a real control in the
inviter's **Theming** step, pre-filled with your default — so an inviter can recolour your template
without you writing any extra code. Use the variables everywhere in your CSS instead of hardcoding
colours.

Every template should expose at least these three:

| Property | Becomes | Meaning |
|---|---|---|
| `--ib-accent` | `accentColor` | Highlights, rules, buttons |
| `--ib-bg` | `backgroundColor` | Page background |
| `--ib-text` | `textColor` | Body text |

Any other `--ib-*` you declare shows up too — `--ib-heading-font` becomes `headingFont`, and so on.
A property whose value looks like a colour gets a colour picker; one whose name contains *font* gets
a font control; anything else gets a text box. **The first declaration wins**, so write your
defaults in `:root` before any `@media` override.

```html
<style>
  :root{
    --ib-accent:#c9a227;
    --ib-bg:#0b0b0f;
    --ib-text:#f6f2e8;
    --ib-heading-font:"Playfair Display", serif;
  }
  body{background:var(--ib-bg); color:var(--ib-text); }
  h1{font-family:var(--ib-heading-font); color:var(--ib-accent); }
</style>
```

Offer a font menu with a meta tag:

```html
<meta name="ib-fonts" content="Playfair Display, Cormorant, Inter">
```

---

## Roles

An invitation can have several **roles** (bride's side / groom's side, VIPs, family…). The inviter
picks and names them in the wizard's first step, then themes and fills each one.

Declare the roles your template understands with a meta tag — and/or just scope something to a role
and it counts as declared:

```html
<meta name="ib-roles" content="bride, groom">
...
<img data-src="bride.photo" data-role-scope="bride" data-slot-label="Bride's photo" alt="">
<h2 data-var="groom.name" data-role-scope="groom">The groom</h2>
```

Anything **without** `data-role-scope` is shared: the inviter fills it once and every role sees it.
Anything **with** it belongs to that role only. Every role can independently override every `--ib-*`
theme key, so a bride's side in blush and a groom's side in navy costs you nothing.

> `data-role-scope` is about *who fills what*. `data-block` (below) is about *who sees what* at send
> time. They're complementary — use both.

---

## Who fills what

Not everything is filled by the inviter. Some things are personal to each guest and are added
automatically at send time. This is why the split matters when you choose your paths:

- **Inviter fills these in the builder** → any `event.*` path (and its images). Example:
  `event.title`, `event.date`, `event.dressCode`, `event.hashtag`, `event.coverImage`.
- **Personal to each guest, added automatically** → `guest.name`, `guest.role`, `guest.gender`.
  Don't expect the inviter to type these.
- **Generated by the platform** → `rsvp.link`, `rsvp.status`, `invite.link`.
- **Have their own builder steps** → `event.venue.*` (the Venue step) and `inviter.*` (the Inviter
  step). Use these paths freely; the inviter fills them elsewhere, not in the main field list.
- **Role-based content** → use `data-block` sections (see below). The inviter maps roles to blocks
  in the Roles step, and each guest sees the blocks for their role.

### The data paths you can use

```
event.title          event.subtitle       event.description
event.date           event.time           event.schedule       event.dressCode
event.coverImage     event.couplePhoto    (any image path you invent, via data-src)
event.<anything>     (any custom field you invent, via data-var/href)
event.venue.name     event.venue.address  event.venue.mapLink
guest.name           guest.role           guest.gender
inviter.name         inviter.phone        inviter.email
invite.link          rsvp.link            rsvp.status
```

Always write sensible fallback text between the tags (e.g. `<h1 data-var="event.title">Our
Celebration</h1>`). It shows until the real value loads, and if a value is left blank. For anything
optional, wrap it in `data-optional` so a blank value disappears cleanly instead of leaving a
dangling label.

---

## Images

Every `<img data-src="...">` becomes an **upload slot** in the builder. Want three photos? Add three
`<img data-src>` tags. The inviter uploads a picture for each and sees them in the live preview.
Wrap an image in `data-optional` if it's fine to leave empty:

```html
<span data-optional>
  <img class="cover" data-src="event.coverImage" data-slot-label="Cover photo" alt="">
</span>
```

---

## Role-based content (dress codes, special messages)

Put a `data-block="someId"` on a section. Whether a guest sees it is decided by the campaign's
**roles** (set by the inviter in the Roles step), which map a guest's role to block ids:

```json
{ "rules": [
  { "condition": { "field": "role", "operator": "equals", "value": "bridesmaid" }, "contentBlock": "bridesmaidInstructions" },
  { "condition": { "field": "gender", "operator": "equals", "value": "male" },     "contentBlock": "maleDressCode" }
] }
```

Operators: `equals`, `notEquals`, `in`, `notIn`, `exists`, `notExists`. **A block no rule mentions is
shown to everyone** — so put universal content in unmentioned blocks (or in no block), and always
give every guest a complete invite. Common ids: `bridesmaidInstructions`, `groomsmenInstructions`,
`maleDressCode`, `femaleDressCode`, `vipSchedule`, `familyNote` (just conventions — name them
anything and map rules to them).

---

## Animation (CSS only)

Templates are **HTML and CSS — no JavaScript**. Every upload is scanned and any `<script>`, inline
`onclick=`/`onload=` handler, `javascript:` URL or `<meta http-equiv="refresh">` is **rejected
outright**, for every author including admins. Nothing is silently stripped; you'll get a clear error
naming what to remove.

You don't need JS for motion. The platform drives it for you:

- `data-reveal` gets the class `is-visible` when the element scrolls into view — animate that class.
- `data-envelope` gets `is-open` after the first scroll — animate a seal or flap opening.
- The whole invite is scroll-driven, so CSS transitions and keyframes on those two classes cover
  cover reveals, parallax-feel fades, staggered entrances and envelope openings.

```css
.panel{opacity:0; transform:translateY(40px); transition:opacity .8s, transform .8s}
.panel.is-visible{opacity:1; transform:none}
.envelope .flap{transform-origin:top; transition:transform 1s}
.envelope.is-open .flap{transform:rotateX(-180deg)}
@media (prefers-reduced-motion: reduce){ .panel,.flap{transition:none; opacity:1; transform:none} }
```

---

## The rules (the sandbox)

- **One self-contained file.** Inline your CSS in `<style>` and embed images as `data:` URIs.
  External `<link rel="stylesheet">` is rejected. (Need a CDN allow-listed? Ask and I'll add it.)
- **No JavaScript at all** — see above. Use CSS with `data-reveal` / `data-envelope` for motion.
- **Keep it light** — aim under ~300KB; **800KB is a hard limit** and an upload over it is rejected.
  Keep embedded images small.
- **Respect reduced motion** with a `@media (prefers-reduced-motion: reduce)` block.
- **Guest text is inserted as text, never HTML** — safe by design.

---

## Adding a template

### Option A — commit it (recommended)
In `invites-blog-backend`, add a folder
`InvitesBlog.Infrastructure/RawTemplates/<your-slug>/` with:

```
index.html     # the whole template: markup + inline <style>
meta.json      # { "name","slug","version","category","description" }
```

`meta.json`:
```json
{ "name": "Aurora Vows", "slug": "aurora-vows", "version": "1.0.0",
  "category": "Wedding", "description": "A warm gold-on-ink wedding invite." }
```

Commit + push, then on the server:
```bash
git -C /opt/apps/invites-blog-backend pull && \
cd /opt/apps/invites-blog-deploy && docker compose -f compose.prod.yml up -d --build api
```
The template is packaged, its fields and image slots are auto-detected from your tags, and it shows
up in the gallery. **A full working example lives at `RawTemplates/aurora-vows/` — copy it to start.**

### Option B — upload at runtime (admin API)
```bash
TOKEN=$(curl -s -X POST https://invites.blog/api/admin/login \
  -H 'Content-Type: application/json' \
  -d '{"email":"admin@invites.blog","password":"YOUR_ADMIN_PASSWORD"}' \
  | python3 -c 'import sys,json;print(json.load(sys.stdin)["data"]["token"])')

curl -s -X POST https://invites.blog/api/admin/templates \
  -H "Authorization: Bearer $TOKEN" \
  -F name="Aurora Vows" -F slug="aurora-vows" -F version="1.0.0" \
  -F category="Wedding" -F description="A warm gold-on-ink wedding invite." \
  -F index=@index.html
```
The response lists the `variables`, `fields`, `imageSlots`, and `contentBlocks` it detected — a quick
way to confirm your tags are right. Re-uploading the same slug+version updates it in place.

### Option C — submit it as a community designer

Make a designer account at `/designer/register` (email + password, or Google/Microsoft if the server
has them configured), then submit from `/designer/submit`. You upload two files:

```
index.html     # the template
preview.png    # a static preview image — REQUIRED, it's the card art in the gallery
```

What happens next:

1. **The automatic scan runs immediately.** Scripts, inline handlers, `javascript:` URLs, meta
   refresh, an external stylesheet, anything over 800KB, or a `select` with no `data-options` are
   rejected on the spot — nothing reaches a human. You can dry-run it with the **Check** button on
   the form, which also shows every field, image slot, role and theme key we detected.
2. **It enters the review queue** as `Submitted`. An admin sees your markup and a plain-language
   summary of what it declares, and either approves it or rejects it with a reason you'll see on
   your submissions list.
3. **On approval it's published** — a real gallery template at version `1.0.0`.

Editing an already-published template works the same way: submit the change and it goes through
review again. Approval bumps the version; **the old version stays exactly as it was**, so invitations
already built on it never change.

---

> **Public vs Dedicated:** add `-F visibility=Dedicated -F assignedEmail=someone@example.com` to make
> a template reserved for one person (they claim it via "Did you request a template?" with an email
> code). Leave it off for a normal public gallery template.

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
    @media (prefers-reduced-motion: reduce){.panel{opacity:1;transform:none;transition:none}}
  </style>
</head>
<body>
  <header class="cover" data-envelope>
    <span data-optional><img data-src="event.coverImage" data-slot-label="Cover photo" alt=""></span>
    <h1 data-var="event.title">Our Celebration</h1>
    <p>Dear <span data-var="guest.name">Guest</span></p>
    <p>Scroll ↓</p>
  </header>

  <section class="panel" data-reveal>
    <p data-var="event.date">The date</p>
    <p data-optional data-var="event.dressCode">Dress code</p>
    <a data-href="rsvp.link" href="#">RSVP</a>
  </section>

  <section class="panel" data-reveal data-block="maleDressCode"><p>Gentlemen: formal suit.</p></section>
  <section class="panel" data-reveal data-block="femaleDressCode"><p>Ladies: evening formal.</p></section>
</body>
</html>
```

Add the `data-*` tags, drop it in, and it's live — with the builder showing exactly the fields you
declared.
