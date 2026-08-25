# Future plans

Things we've decided to do but haven't built. Each one records *why*, what it touches, and the traps
we already know about — so whoever picks it up isn't rediscovering them.

**Order:** R2 first (it's small and it unblocks cost/scale), then the render app, then invites.lens.
Nothing here is started.

---

## 1. Cloudflare R2 for storage — *do this one sooner*

**Today.** Assets live in a **MinIO** container (`invites-blog-minio`, bucket `invites-assets`, volume
`invites-minio-data`) on the same box as everything else. Caddy proxies `/assets/*` → MinIO on every
host. That means our object storage is one disk on one server, backed up by nothing in particular, and
growing with every campaign photo.

**Why R2.** No egress fees, replicated, and it stops storage being a thing that can fill up a VPS.
Guest photo uploads are only going to grow — and *especially* if invites.lens (below) ever ships,
because that turns every event into a bulk photo upload.

**Why it should be easy.** `S3StorageService` is already generic S3 — `ServiceURL` + `ForcePathStyle`,
selected by `Storage:Provider`. R2 is S3-compatible, so this is mostly configuration:

```
Storage:Provider = S3
Storage:Endpoint = https://<accountid>.r2.cloudflarestorage.com
Storage:Bucket   = invites-assets
Storage:AccessKey / Storage:SecretKey   (R2 API token)
```

**The single best thing about our current setup:** stored URLs are **relative** — `Urls:AssetsBase=/assets`,
so the database holds `/assets/campaigns/…`, not an absolute host. Swapping the backend needs **no data
migration**, as long as `/assets/*` keeps resolving. Don't break that by writing absolute URLs.

### Traps we already know about

- **Cache headers must move with the assets.** Right now *Caddy* sets them, not the storage:
  `/assets/templates/*` → `no-cache, must-revalidate` (packages are republished at the same URL) and
  `/assets/campaigns/*` → `public, max-age=31536000, immutable`. If `/assets/*` starts being served from
  an R2 custom domain instead of through Caddy, **those rules do not follow automatically.** Prefer
  setting `CacheControl` on `PutObject` so the correct header is a property of the object, not of
  whichever proxy happens to be in front of it.
- We got burned by exactly this in August 2026: corrected posters kept serving stale for hours behind a
  4-hour `max-age`. Poster filenames are now content-addressed, which is the durable fix — keep that
  property.
- **Region must be `auto`** for R2; the AWS SDK may need `AuthenticationRegion` set explicitly.
- **No object ACLs.** Public reads come from a public bucket URL, a custom domain, or a Worker — not
  from per-object ACLs the way MinIO's anonymous-download policy works today.
- `DisablePayloadSigning` is currently avoided because MinIO is reached over plain `http://`. R2 is
  HTTPS, so that constraint disappears — but there's no need to change it either.
- **Keep MinIO mounted until the mirror is verified.** `mc mirror` MinIO → R2, diff object counts and a
  few checksums, then flip the config. The disk volume is the rollback.

**Done when:** a fresh container boots against R2, `TemplateSeeder` republishes into it, campaign image
upload works, `/assets/*` resolves with the right cache headers, and MinIO can be stopped without
anything 404ing.

---

## 2. Server-rendered invitation on its own origin (the "render app")

**Today.** A guest's invitation is an Angular page that creates a **sandboxed iframe**, then posts the
invitation data into it, and a script inside binds it to the markup.

**The plan.** Render the template into a **bare standalone document on the server**, on **its own
origin** — no Angular shell, no app CSS, no iframe. Auth/OTP stays where it is; on success it redirects
to a short-lived signed render URL.

**Why.** It removes two classes of bug *by construction*, both of which cost us real days:

- **Data applied more than once.** Binding runs on load and again on every host update; the builder
  sends one per edit. A cloning bug there turned 6 photos into 36, then 216 — and it looked exactly
  like an animation bug, so we rewrote the animation twice before finding it. Server-side binding
  happens once.
- **Viewport units inside a resizing frame.** `vh`/`dvh` are relative to the frame, and the frame
  follows the phone's URL bar; a scroll track sized that way gets remapped mid-scroll and throws the
  reader backwards. No nested viewport, no problem.

It's also **much faster to first paint**: one pre-filled document, instead of shell → JS boot → fetch →
create iframe → load template → postMessage → bind.

### Non-negotiable: it must be a separate origin

The guest JWT lives in **`localStorage`** (`web-invitee/shared/services/token-store.service.ts`), and
templates are now allowed to carry their own JavaScript. Same origin + designer JS = one line to steal
the token of every guest of every campaign using that template. Either move the token to an HttpOnly
cookie *or* (better) render on a cookieless origin with nothing worth taking.

**Keep the sandboxed iframe for previews inside the signed-in inviter app** — gallery card, editor,
template detail. That's where the valuable session is, and it isn't worth server-rendering a thumbnail.

### What it costs

Reimplementing the binding contract in C# against an HTML parser (AngleSharp): `data-var`, `data-src`
with `data-multiple`, `data-optional`, `data-block` role resolution, theme variables. The parsing is
the easy part — the real risk is **behaviour drift** between the server implementation and the
in-browser one while both exist. Consider making the server the only implementation for guests, and
leaving the JS binder to the editor preview alone.

**What it will NOT fix:** scroll frame rate. Measured — swapping all six photos for 1×1 pixels changed
nothing (20.4 ms → 20.1 ms). The cost was 56 continuously animating decorations, and they cost the same
in a top-level document.

**Done when:** a guest link renders a filled invitation as one document with no iframe, on an origin
that holds no session, and the editor preview still works.

---

## 3. invites.lens — the event photo box

**The idea.** A mobile app guests use *at* the event to take photos. Everything they shoot collects into
that campaign's photo box, for the host and guests to look through or download afterwards. The
invitation gets people to the party; this is what they leave with.

**Why it fits.** Every campaign already owns a photo collection (`campaign_assets`) and already has a
guest list with authenticated identities. This is largely a new *client* onto things the platform has:
a campaign, its guests, and its assets.

**Sketch, not a design.**

- Guests are already authenticated per campaign (invite token → OTP → trusted device). The app should
  reuse that rather than inventing a second identity system.
- Uploads land in the campaign's asset store, tagged with which guest took them.
- Host gets moderation — an event photo box needs a way to remove something, before it needs anything
  clever.
- A "download everything" path (zip) matters more than it sounds; it's the thing people actually want
  the week after.

### Things to settle before building

- **Storage cost and scale is the whole risk.** A wedding with 80 guests shooting freely is thousands
  of full-resolution photos. Do **R2 first** — this is the feature that makes storage a real bill, and
  doing it on a single VPS disk would be a mistake.
- **Server-side shrinking already exists** (`ImageSharpOptimizer`), currently 2048px for covers and
  512px for gallery prints. Neither is right for "photos people want to keep" — originals probably need
  preserving, with derived sizes for viewing. That's a new decision, not a reuse.
- Retention: how long does a photo box live after the event? Someone has to answer this before we start
  storing other people's memories indefinitely.
- Consent: guests photographing other guests. Worth thinking about before launch, not after.

**Done when:** a guest can shoot from the app, the host sees it in the campaign, and everyone can
download the set.
