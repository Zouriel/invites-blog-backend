# Making an invites.blog template

> This file is published at **/template-guide.md** and kept word-for-word in step with
> `TEMPLATE-GUIDE.md` in the backend repo. The same guide is shown in the app at **/template-guide**.

**Contents**

1. What a template is
2. A minimal working example
3. The tags
4. Roles
5. Theming
6. Motion and JavaScript
7. The manifest
8. Packaging and uploading
9. Checklist and common mistakes

---

## 1. What a template is

A template is **one HTML file**. Your markup, your CSS in a `<style>` tag and, if you want it, your
JavaScript in a `<script>` tag. Everything lives in that one file.

You mark the spots that change from one invitation to the next with small `data-*` attributes. When a
guest opens their invitation, the platform fills those spots with the event's details and that guest's
own name, then sends them the finished page.

**Your tags build the editor.** The inviter only sees the fields your template asks for. Add
`data-var="event.hashtag"` and a "Hashtag" box appears in the builder. Add `<img data-src="…">` and
an upload slot appears. You don't write any other code.

---

## 2. A minimal working example

Copy this, open it in a browser, then change it.

```html
<!doctype html>
<html lang="en">
<head>
  <meta charset="utf-8">
  <meta name="viewport" content="width=device-width, initial-scale=1">
  <title>My Template</title>
  <style>
    :root{ --ib-accent:#c9a227; --ib-bg:#0b0b0f; --ib-text:#f6f2e8; }
    body{ background:var(--ib-bg); color:var(--ib-text); }
    h1{ color:var(--ib-accent); }
    .panel{ opacity:0; transform:translateY(40px); transition:opacity .8s, transform .8s; }
    .panel.is-visible{ opacity:1; transform:none; }
  </style>
</head>
<body>
  <header data-envelope>
    <span data-optional><img data-src="event.coverImage" data-slot-label="Cover photo" alt=""></span>
    <h1 data-var="event.title">Our Celebration</h1>
    <p>Dear <span data-var="guest.name">Guest</span></p>
  </header>

  <section class="panel" data-reveal>
    <p data-var="event.date">The date</p>
    <p data-optional>Dress code: <span data-var="event.dressCode"></span></p>
    <div data-dress-colors></div>
    <a data-href="rsvp.link" href="#" data-optional><span data-var="rsvp.label">Reply now</span></a>
  </section>

  <section class="panel" data-reveal data-block="familyNote">
    <p>Family, please arrive an hour early for photos.</p>
  </section>
</body>
</html>
```

What each piece does:

- `data-var` fills text, `data-src` fills an image, `data-href` fills a link.
- `data-optional` hides the element when its value is empty.
- `data-block` is a section only some guests see (see *Roles*).
- `data-dress-colors` shows the colours this guest is asked to wear.
- `data-reveal` and `data-envelope` give you classes to animate.
- `--ib-accent`, `--ib-bg` and `--ib-text` become colour pickers for the inviter.

A fuller example lives in the backend repo at `InvitesBlog.Infrastructure/RawTemplates/aurora-vows/`.

---

## 3. The tags

### Tags that fill something in

| Tag | What it does | Example |
|---|---|---|
| `data-var="PATH"` | Sets the element's **text** | `<h1 data-var="event.title">Our Day</h1>` |
| `data-href="PATH"` | Sets a **link's** `href` | `<a data-href="rsvp.link" href="#">RSVP</a>` |
| `data-src="PATH"` | Sets an **image's** `src`, and adds an upload slot to the builder | `<img data-src="event.coverImage" alt="">` |
| `data-dress-colors` | Filled with this guest's **dress colour swatches** (see *Roles*) | `<div data-dress-colors></div>` |

### Tags that show or hide something

| Tag | What it does | Example |
|---|---|---|
| `data-optional` | Hides the element when nothing inside it was filled | `<p data-optional>Dress code: <span data-var="event.dressCode"></span></p>` |
| `data-block="ID"` | A section only some guests see, by role | `<section data-block="bridesmaidInstructions">` |

### Tags that drive motion

| Tag | What it does | Example |
|---|---|---|
| `data-reveal` | Gets the class `is-visible` once its top edge is within 90% of the screen height | `<section data-reveal>` |
| `data-envelope` | Gets the class `is-open` once the reader has scrolled a quarter of the screen height | `<header data-envelope>` |

### Builder hints (optional)

The builder works without these. They make it nicer for the inviter.

| Hint | Put it on | What it does |
|---|---|---|
| `data-field-label="Gift note"` | a `data-var` or `data-href` element | Sets the label of the input. Without it, the label is made from the path. |
| `data-type="textarea"` | a `data-var` element | Picks the kind of input (see the next table). |
| `data-options="Formal,Casual"` | a `data-type="select"` element | The choices in the dropdown. **Required** for `select`. |
| `data-slot-label="Cover photo"` | a `data-src` image | Names the upload slot. |
| `data-multiple="true"` | a `data-src` image | Turns the slot into a **gallery**: the inviter adds, orders and removes several photos. |
| `data-min-images="2"` / `data-max-images="8"` | a `data-multiple` image | Limits the gallery size. Both optional. |
| `data-role-scope="bride"` | a `data-var`, `data-href` or `data-src` element | The field belongs to **one role** instead of being shared (see *Roles*). |

`data-field-type` is the old name for `data-type` and still works. If an element has both,
`data-type` wins.

### Input kinds (`data-type`)

| `data-type` | The inviter gets |
|---|---|
| `text` | a one-line box |
| `textarea` | a multi-line box |
| `date` | a date picker (shown to guests as e.g. "Saturday, 28 August 2026") |
| `time` | a time picker (shown to guests as e.g. "10:00 PM") |
| `url` | a link box |
| `color` | a colour picker |
| `select` | a dropdown of your `data-options`. An upload without `data-options` is rejected. |
| `image` | an upload slot (you'd normally use `data-src` instead) |

If you don't set a type, the builder guesses from the last part of the path:

- contains *date* → date picker
- contains *time* → time picker
- contains *description*, *schedule*, *note*, *message*, *story* or *address* → multi-line box
- a `data-href`, or contains *link* or *url* → link box
- anything else → one-line box

`data-options` takes a comma list (`data-options="Formal,Casual,Black Tie"`) or a JSON array
(`data-options='["Formal","Casual"]'`).

### Galleries

A plain `<img data-src>` holds **one** photo. Add `data-multiple="true"` and it holds several. The
platform copies your `<img>` element once per photo, so every copy keeps your classes and styling.

```html
<div class="photo-strip">
  <img data-src="event.gallery" data-multiple="true" data-min-images="2" data-max-images="8"
       data-slot-label="Photo strip" alt="">
</div>
```

`data-min-images` and `data-max-images` only count when `data-multiple` is set.

Using the same path on several elements asks the inviter **once** and fills every element with it.

### The paths you can use

Who fills what:

| Path | Filled by |
|---|---|
| `event.*`: any name you invent, e.g. `event.title`, `event.hashtag`, `event.coverImage` | The inviter, in the builder |
| `event.venue.name`, `event.venue.address`, `event.venue.mapLink` | The inviter, on the Venue step |
| `inviter.name`, `inviter.phone`, `inviter.email` | The inviter, on the Inviter step |
| `guest.name`, `guest.role`, `guest.gender` | Each guest's own details, added automatically |
| `rsvp.link`, `rsvp.label`, `rsvp.status` | The platform |
| `invite.link`, `camera.link`, `photos.link` | The platform |

A few of these need explaining:

- `event.title`, `event.date` and `event.time` fall back to the event's own name, date and time when the
  inviter leaves them blank.
- `guest.role` is the guest's **first** role. `guest.roles` is the full list, which is useful in
  JavaScript.
- `rsvp.label` is the right button wording for this guest: "Reply now", "Confirm your reply" or
  "Change your reply". Guests who already said they're coming get no link and an empty label, so wrap
  the button in `data-optional`.
- `camera.link` opens the event camera. It only has a value on the event day, and only for guests who
  said they're coming. Wrap it in `data-optional` too.
- `photos.link` opens the shared photo gallery.

### How filling works

- **A missing value blanks the element.** The text you write between the tags shows only until the
  data is applied. It is not a fallback. Test your template with fields left empty.
- **Wrap anything that can be empty in `data-optional`**, so a blank value removes its label too.
- **A `data-href` with no value is hidden** if its own `href` is empty or `#`, so an empty button
  never jumps the reader to the top.
- **Guest text goes in as text, never as HTML.**

---

## 4. Roles

An invitation can have **roles**: the bride's side and the groom's side, bridesmaids, VIPs, family.
The inviter names them on the Roles step. Then they choose which of your sections each role sees,
and optionally a dress colour palette per role.

Roles do three different jobs in a template. Each one has its own attribute.

| You want… | Use | Decided when |
|---|---|---|
| a field that each role fills with its own value | `data-role-scope` | the inviter fills the builder |
| a section that only some guests see | `data-block` | the invitation is sent |
| the colours each guest should wear | `data-dress-colors` | the invitation is sent |

### Declaring roles

List the roles your template has in mind with a meta tag. Any role named in a `data-role-scope`
counts as declared too.

```html
<meta name="ib-roles" content="Bride, Groom">
```

The Roles step suggests these names. Role names are turned into lowercase slugs, so `Bride & Groom`
becomes `bride-groom`.

### Fields that belong to one role: `data-role-scope`

```html
<img data-src="bride.photo" data-role-scope="bride" data-slot-label="Bride's photo" alt="">
<h2 data-var="groom.name" data-role-scope="groom">The groom</h2>
```

A field **without** `data-role-scope` is shared: the inviter fills it once and everyone sees it. A
field **with** it is filled separately for that role.

Each role can also have its own values for every `--ib-*` theme colour. A bride's side in blush and a
groom's side in navy needs no extra work from you.

### Sections only some guests see: `data-block`

```html
<section data-block="bridesmaidInstructions">
  <p>Bridesmaids, please meet at 3pm for hair and make-up.</p>
</section>
```

On the Roles step, the inviter ticks which blocks each role sees. Then:

- A block that **no role** ticked is shown to **everyone**.
- A block that **any role** ticked is shown **only** to guests with one of those roles.

So put content everyone needs outside any block, or in a block nobody ticks. Every guest should still
get a complete invitation.

Block names are up to you. Common ones are `bridesmaidInstructions`, `groomsmenInstructions`,
`maleDressCode`, `femaleDressCode`, `vipSchedule` and `familyNote`.

Behind the scenes the Roles step saves rules like these. Rules can also test `gender`, with the
operators `equals`, `notEquals`, `in`, `notIn`, `exists` and `notExists`, compared ignoring case.

```json
{ "rules": [
  { "condition": { "field": "role", "operator": "equals", "value": "Bridesmaids" }, "contentBlock": "bridesmaidInstructions" }
] }
```

### Guests with several roles

A guest can hold more than one role. "Ali and family" might be both *Groom family men* and
*Groom family women*. For that guest:

| Thing | What they get |
|---|---|
| `data-block` sections | Every section **any** of their roles would see |
| `data-role-scope` fields | Filled from any of their roles |
| Dress colours | One row of swatches **per role** that has a palette |
| Theme colours | Their **first** role's theme |
| `guest.role` | Their **first** role (`guest.roles` has them all) |

### Dress colours: `data-dress-colors`

The inviter can give each role a palette of up to six colours. Put an empty element where you want
the swatches:

```html
<div data-dress-colors></div>
```

The platform fills it with this markup, which you style in your CSS:

```html
<div class="ib-dress">
  <p class="ib-dress__role">Groom family men</p>   <!-- only when the guest has more than one palette -->
  <div class="ib-dress__swatches">
    <span class="ib-dress__swatch" title="#1f3a5f" style="background:#1f3a5f"></span>
  </div>
</div>
```

- The element is hidden when the guest has no colours.
- Each swatch only gets `background` inline. Its size and shape are up to you.
- **If your template has no `data-dress-colors`**, the platform adds its own "Dress colours" section
  near the end of the invitation.

---

## 5. Theming

Declare your palette as CSS custom properties named `--ib-…` in `:root`. Each one becomes a control
on the inviter's Theming step, pre-filled with your value. Use the variables everywhere instead of
fixed colours.

```html
<style>
  :root{
    --ib-accent:#c9a227;
    --ib-bg:#0b0b0f;
    --ib-text:#f6f2e8;
    --ib-heading-font:"Playfair Display", serif;
  }
  body{ background:var(--ib-bg); color:var(--ib-text); }
  h1{ font-family:var(--ib-heading-font); color:var(--ib-accent); }
</style>
```

Every template should declare these three:

| Property | Saved as | Use it for |
|---|---|---|
| `--ib-accent` | `accentColor` | Highlights, rules, buttons |
| `--ib-bg` | `backgroundColor` | Page background |
| `--ib-text` | `textColor` | Body text |

Any other `--ib-*` property shows up too. `--ib-heading-font` is saved as `headingFont`, and so on.
The kind of control depends on the value:

- a value that starts like a colour (`#…`, `rgb(`, `hsl(`, `color(`) → colour picker
- a name containing *font* → font control
- anything else → text box

**The first declaration wins**, so write your defaults in `:root` before any `@media` override. The
inviter's choices are set inline on `<html>`, so they beat your defaults.

To offer a list of fonts, add a meta tag:

```html
<meta name="ib-fonts" content="Playfair Display, Cormorant, Inter">
```

The platform also sets `--ib-progress` on `<html>`: a number from `0` at the top to `1` at the bottom
of the page. Use it to scrub an animation in plain CSS, e.g. `calc(var(--ib-progress) * 360deg)`.

---

## 6. Motion and JavaScript

Templates can use **CSS, JavaScript, or both**. Motion is the whole product, so write the animation
you actually want.

### The built-in hooks

You need no code for these:

- `data-reveal` → class `is-visible` when the element scrolls into view.
- `data-envelope` → class `is-open` after the reader scrolls a little. Use it for a seal or a flap.
- `--ib-progress` → how far down the page the reader is, from 0 to 1.

```css
.panel{ opacity:0; transform:translateY(40px); transition:opacity .8s, transform .8s; }
.panel.is-visible{ opacity:1; transform:none; }
.envelope .flap{ transform-origin:top; transition:transform 1s; }
.envelope.is-open .flap{ transform:rotateX(-180deg); }
```

### Prefer CSS scroll-driven animation to a scroll handler

If the motion follows the scroll, use `animation-timeline` with a `view-timeline`. This is measured,
not taste:

- A scroll-driven CSS animation runs on the **compositor**. A `requestAnimationFrame` handler runs on
  the **main thread**, every frame.
- Reading layout in a scroll handler (`getBoundingClientRect()`, `offsetTop`, `scrollHeight`) makes the
  browser work out the layout of the whole page before it can answer. If you must, read it once, cache
  it, and recompute on `resize`, not on `scroll`.

### Your JavaScript and the invitation data

By the time your script runs, the tags are already filled. If you need the data itself:

```js
addEventListener('invite:data', e => { /* e.detail is the invitation data */ });
addEventListener('invite:progress', e => { /* e.detail is 0..1 down the page */ });
// window.invite.data and window.invite.progress hold the same values.
```

### Where your template runs

- **Guests** get one finished page. It was filled on the server before it was sent, and it isn't
  inside a frame.
- **The builder preview** and the gallery run your template inside a sandboxed frame. Here the data is
  applied again **on every edit** the inviter makes.

Write for both. See *Common mistakes* for what that means in practice.

---

## 7. The manifest

You never write the manifest by hand. When a template is uploaded, the platform reads your tags and
saves a `manifest.json` next to it. The builder, the Roles step and the Theming step are all built
from it.

| Manifest field | Built from |
|---|---|
| `variables` | every `data-var`, `data-href` and `data-src` path |
| `fields` | one entry per `data-var` / `data-href` path: `key`, `label`, `type`, `options`, `roleScope` |
| `imageSlots` | one entry per `data-src` path: `key`, `label`, `multiple`, `minImages`, `maxImages`, `roleScope` |
| `contentBlocks` | every `data-block` name |
| `roles` | the `ib-roles` meta tag plus every `data-role-scope`, as slugs |
| `roleDefinitions` | per role: `slug`, `label`, `themeKeys`, and the `fields` and `imageSlots` scoped to it |
| `theme` | every `--ib-*` property (`keys`: `key`, `cssVar`, `label`, `type`, `default`), the `ib-fonts` list, and the three required colours |

The **Check** button on the submission form, and the admin upload response, show what was detected.
It's the quickest way to confirm your tags are right.

**Versions are frozen.** Every invitation keeps the exact package and manifest it was created with. An
edit publishes a new version and never changes invitations that already exist.

---

## 8. Packaging and uploading

### The rules every template follows

- **One self-contained file.** Inline your CSS in `<style>` and your JavaScript in `<script>`.
  - `<link rel="stylesheet">` and `<script src="…">` are **rejected**. What a reviewer approves has to
    be what actually runs, and a file fetched from elsewhere can change after approval.
- **Embed images and fonts as `data:` URIs.** The guest page is served with a strict content policy.
  - Scripts and styles must be inline.
  - Images and fonts load only from the platform itself or from `data:` URIs.
  - Network requests and form submissions are blocked.
  - An image or web font linked from another site won't show for guests.
- **Size:** aim for under **300 KB**, which is where the Check starts warning you. **800 KB** is a hard
  limit and anything over it is rejected.
- **Sandboxed.** Your page runs on an opaque origin. It cannot read cookies, `localStorage` or the app's
  session. Links with `target="_blank"` (a map, say) still open.

### Three ways to add a template

**Option A: submit it as a designer.** This is the way for community creators.

1. Make a creator account at `/signup`, or turn an existing account into one under
   **My account → Creator**.
2. Open `/designer` and upload two files:
   - `index.html`, your template.
   - A preview image. It's **required**, and it's the card people see in the gallery.
3. **The automatic check runs straight away.** It rejects:
   - an external stylesheet or `<script src>`;
   - a file over 800 KB;
   - a `select` with no `data-options`, or `data-options` that isn't valid JSON.

   Use **Check** first to try it without submitting. It lists every field, image slot, role and theme
   key it found.
4. **A person reviews it.** A reviewer reads your markup and either approves it or rejects it with a
   reason, shown on your submissions list. Reviewers use a different permission from designers, so
   nobody approves their own work.
5. **On approval it's published** in the gallery at version `1.0.0`.

To change a published template, submit the change. It goes through review again, approval bumps the
version, and **the old version stays exactly as it was**.

**Option B: commit it to the repo.** This is for the invites.blog team. Add a folder
`InvitesBlog.Infrastructure/RawTemplates/<your-slug>/` in `invites-blog-backend`:

```
index.html     # the whole template
meta.json      # name, slug, version, category, description
poster.webp    # optional but wanted: the still the gallery card shows
```

```json
{ "name": "Aurora Vows", "slug": "aurora-vows", "version": "1.0.0",
  "category": "Wedding", "description": "A warm gold-on-ink wedding invite." }
```

About `poster.webp`:

- The gallery shows this still, not your live template.
- Portrait, about 720×1280, works best. The card crops from the top.
- Capture it with sample text filled in, on a frame that shows the design, not a bare
  "scroll to open" screen.
- Without it, the card renders your template live instead, which is slower.

To reserve a template for one person, add `"visibility": "Dedicated"` and
`"assignedEmail": "someone@example.com"`. It then never appears in the public gallery. That person
finds it under **My templates → My requests** after signing in with that address. Two things to know:

- A dedicated template is single-use. The first campaign made from it turns it into a read-only
  showcase.
- Once it has been released to the public gallery, it can't be made private again by re-seeding.

Raising `version` replaces the gallery card. Invitations made from the old version keep using it.
Commit, push, then on the server:

```bash
git -C /opt/apps/invites-blog-backend pull && \
cd /opt/apps/invites-blog-deploy && docker compose -f compose.prod.yml up -d --build api
```

**Option C: upload it through the admin API.**

```bash
# One sign-in for everyone; admin rights come from the account's roles.
TOKEN=$(curl -s -X POST https://invites.blog/api/auth/login \
  -H 'Content-Type: application/json' \
  -d '{"email":"admin@invites.blog","password":"YOUR_ADMIN_PASSWORD"}' \
  | python3 -c 'import sys,json;print(json.load(sys.stdin)["data"]["token"])')

curl -s -X POST https://invites.blog/api/admin/templates \
  -H "Authorization: Bearer $TOKEN" \
  -F name="Aurora Vows" -F slug="aurora-vows" -F version="1.0.0" \
  -F category="Wedding" -F description="A warm gold-on-ink wedding invite." \
  -F index=@index.html
```

- The response lists the `variables`, `fields`, `imageSlots` and `contentBlocks` it found.
- Uploading the same slug and version again updates it in place.
- Add `-F visibility=Dedicated -F assignedEmail=someone@example.com` to reserve it for one person.

---

## 9. Checklist and common mistakes

### Before you submit

- [ ] One file. No `<link rel="stylesheet">`, no `<script src>`.
- [ ] Images and fonts embedded as `data:` URIs.
- [ ] Under 300 KB if you can, and never over 800 KB.
- [ ] `--ib-accent`, `--ib-bg` and `--ib-text` declared in `:root` and used in your CSS.
- [ ] Every value that can be empty is wrapped in `data-optional`.
- [ ] Every `data-type="select"` has `data-options`.
- [ ] The RSVP button binds `rsvp.link` and `rsvp.label`, inside `data-optional`.
- [ ] A `data-dress-colors` spot, styled, if your design has a place for it.
- [ ] Content every guest needs is outside any `data-block`.
- [ ] Tested with fields empty, with a guest with no role, and with a guest with two roles.
- [ ] Tested in the builder preview while editing, and on a phone.
- [ ] **Check** passes and lists the fields you expect.

### Common mistakes

**Cloning elements on every data pass.** In the builder preview, the data is applied again on every
edit. If your JavaScript clones or generates elements, tag what you made and clear it before making it
again. We once shipped a gallery that cloned itself on each pass: six photos became thirty-six, then
two hundred and sixteen. It looked exactly like an animation bug.

**Treating placeholder text as a fallback.** A missing value blanks the element. Your placeholder only
shows before the data arrives.

**Per-child CSS variables with no default.** Say you number children
(`.page:nth-child(1){--i:1}` … `:nth-child(6){--i:6}`) and use `--i` in an `animation-range`. A
seventh child then gets an undefined variable, and that element animates across the **whole**
timeline. Set a default on the base rule, and consider `:nth-child(n+7){animation:none}`.

**Too many animations that run forever.** One template had 56 always-moving decorations. They cost
about 4 ms a frame, more than six full-size photos. Hiding half of them brought it back to 60 fps.

**Designing for full-size photos.** Gallery images are resized to **512 px** on the long edge. Design
prints to be small.

**A scroll track built from viewport units.** Sizes in `vh` / `dvh` change when the phone's address bar
grows or shrinks mid-scroll, and inside the preview frame. That throws the reader backwards through
your animation. Tie ranges to the element (`contain`), not to the viewport.

**Relying on `prefers-reduced-motion`.** The platform removes
`@media (prefers-reduced-motion: reduce)` rules, because an invitation without motion is broken. Don't
use that block to fix anything.

**Linking fonts or images from another site.** They may work in the preview and then fail for guests.
Embed them.

**Putting everything in blocks.** A block any role ticked is hidden from everyone else. A guest with no
matching role then gets a half-empty invitation.
