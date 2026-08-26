# Future plans

Things we've decided to do but haven't built. Each one records *why*, what it touches, and the traps
we already know about — so whoever picks it up isn't rediscovering them.

**Order:** R2 first (it's small and it unblocks cost/scale), then the app topology, then the auth
model, then the render app, then invites.lens. The topology and auth work come before the render app
on purpose: §2 decides which hosts exist and what runs on them, and §3 shrinks what a leaked session is
worth — which is the render app's whole reason for existing. Nothing here is started.

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

## 2. One app and one render app

**Decided.** There is no "inviter app" and "invitee app" any more. There are two things:

- **`invites.blog`** — the app. Account, builder, dashboard, inbox. Everything behind a sign-in.
- **`me.invites.blog`** — the render app. **The entire guest surface**, all server-rendered:
  `/i/:token`, `/e/:campaignId`, the RSVP pages, privacy-removal, and `/r/:renderId` — the invitation
  itself.

`web-invitee` is dissolved into those two. No third hostname: `me.invites.blog` already exists, is
already in Caddy, and is already where guests go.

**Guests keep the URL they already have.** Personal links are `{Urls:InviteeBase}/i/{token}`, and they
are sitting in mail and chat histories right now with **no expiry and no revocation**. Because the
render app takes over that host rather than vacating it, `me.invites.blog/i/:token` keeps working
natively — no permanent redirect, no `Urls:InviteeBase` switch, and no change to the `invite.link` /
`rsvp.link` values baked into every invitation. Nothing a guest holds has to be reissued.

**No guest ever loads an SPA again.** The guest surface is six pages of forms and one rendered
document; none of it needs Angular, and the render app is already a server-rendering app.

**The invitee inbox goes, and it takes the app with it.** `web-inviter`'s inbox is strictly better —
received *and* sent in one tabbed page, `signedInGuard`'d, matched on every identifier the account
holds — while `web-invitee`'s is received-only. And §3 kills it independently: once the inbox is an
account feature, the invitee inbox has no auth left to run on.

Removing it is not a page delete. The inbox is what `web-invitee` is built around: the home page's only
CTA, the default `returnTo` for `/login` and `/verify`, `TokenStore` + `jwt.interceptor`,
`api.service`'s 401-bounce logic, `invite-detail` (which exists only to be opened from an inbox card),
RSVP's back-navigation, and "save to my inbox" on both invitation pages. Pull it and what remains is
exactly the set the render app absorbs. So the inbox deletion and the app dissolution are one piece of
work, not two.

**"Save to my inbox" becomes "sign in to keep this"** — a link across to `invites.blog`. The claim flow
is already the seam (§3).

### The cost of one host: auth and template JS share an origin

The guest's session cookie now lives on the same origin that later runs a designer's JavaScript.
`HttpOnly` stops that script *reading* the cookie, but not from *using* it on a same-origin request. Two
changes close that, and both are worth doing on their own merits:

- **Drop the `/api/*` proxy from `me.invites.blog` in Caddy.** A server-rendered guest app has no
  browser-facing API; it calls the API internally over the Docker network. With no same-origin API
  surface there is nothing for a credentialed `fetch` to aim at, even if the CSP were misconfigured.
  `/assets/*` stays — images and template packages still resolve relatively.
- **Serve `/r/*` with a CSP `sandbox` header** (see §4). The invitation document gets an opaque origin,
  so its scripts cannot read cookies or make credentialed same-origin requests at all — the isolation
  the iframe used to provide, without the iframe.

`invites.blog` keeps the account session entirely to itself; no guest flow touches it.

### Smaller consequences

- `web-inviter`'s `environment.prod.ts` still carries `inviteeBase: 'https://me.invites.blog'`. That
  value stays correct — which is the point.
- The template CSP's `frame-ancestors` currently lists `https://invites.blog https://me.invites.blog`.
  Previews are framed only from `invites.blog` now, and the render origin serves top-level documents
  rather than framed ones, so `me.invites.blog` comes off that list.
- One Angular project, one container (`invites-blog-web-invitee`) and one build target go away; a
  render service takes their place.

---

## 3. Three authentications, and only three

**The intent.** There are three ways someone proves who they are here, and there should never be a
fourth:

1. **Account** — the web app. Email + password or Google/Microsoft sign-in.
2. **Personal invite link** — delivered by the system (email today; Viber/WhatsApp later). The link
   itself is the key; the first opener is auto-authenticated and the link binds to that IP.
3. **Public link for one invitation** — OTP proves the opener is on that campaign's guest list, and
   that **device** stays authenticated **for that invitation**.

**1 and 2 already work that way.** Account sign-in is `POST /api/auth/oauth/{provider}` verifying a
provider ID token, with `IssueForRoles` minting one session carrying every role the account holds — an
admin who also authors templates doesn't have to pick an identity per sign-in. The personal link is
`GetByTokenAsync` + `InviteTrustedIp`: first-ever open auto-trusts its IP, up to 3 are kept, a fourth
evicts the least-recently-seen, and an unrecognized IP gets an OTP sent to the contact **on the guest
row** — never one the visitor types, because the link already says who they are. It deliberately calls
`VerifyContactAsync` rather than `VerifyAsync`, so reauth mints no session. Adding Viber/WhatsApp is a
delivery-channel change (`IOtpSender` + dispatch), not an auth change.

### Where 3 has drifted

`POST /api/otp/verify` mints a **30-day identity JWT keyed on the contact alone** — no campaign claim,
no invite claim, no `jti` — held in `localStorage` on `me.invites.blog`. Measured against the intent:

- **Not device-bound.** It is copyable text that any script on the origin can read.
- **Not invitation-scoped.** It opens every campaign that contact appears on, and the whole inbox at
  `/api/me/invites`.
- **Not revocable.** No server-side session record. (`OtpTokensResponse.RefreshToken` is generated
  fresh on every verify, persisted nowhere, and accepted by nothing — a dead value, still typed in
  `api.types.ts`.)

The reason it grew identity-wide is the inbox: `/api/me/invites` means "every invitation I have ever
received", and the same token authorizes it.

### Decided: the invite cookie is scoped to one campaign; the inbox becomes an account feature

OTP on a shared campaign link mints an **HttpOnly, Secure, SameSite=Lax, host-only cookie on
`me.invites.blog`, scoped to that campaign** — a device authenticated for one invitation and nothing
else. The inbox stops being an OTP feature and folds into auth 1: if you want your invitations in one
place, you have an account. Three authentications, no fourth.

```
OTP on /e/:campaignId  ->  ib_inv=<opaque>; HttpOnly; Secure; SameSite=Lax; Path=/
                           (host-only — no Domain attribute)  scope: campaign X, this device

/inbox                 ->  account session (auth 1)
```

**Why this is cheap.** The guest surface is server-rendered by the render app on that same host (§2),
so the cookie rides along on ordinary navigations. No CORS, no `AllowCredentials`, no `SameSite=None`,
and no browser-facing API to attach it to — the render app calls the API server-side. Being host-only,
it never reaches `invites.blog`, so the account session and the guest session stay strictly apart.

It *does* share an origin with the rendered invitation, and therefore with a designer's JavaScript.
`HttpOnly` stops that script reading it; §2's dropped `/api/*` proxy and §4's CSP `sandbox` header stop
it being used. Those two are what make one host safe.

**Why it's worth doing before the render app.** It shrinks what a leak costs. Under the current token,
one exposed session is every invitation that person will ever receive; under a campaign-scoped cookie
it is one campaign on one device. The render origin's whole justification is keeping a template's JS
away from the session — this makes the thing being kept away far less valuable, which turns a
contained risk into a small one.

### What it touches

- `OtpService.VerifyAsync` — stops minting the identity JWT for the campaign-link flow; issues the
  scoped cookie instead. `RefreshToken` should go with it rather than being carried forward.
- `InviteService.IdentifiersAsync` — currently resolves an identity from *either* an account or a bare
  contact claim. The bare-contact branch stays for `GetMyInviteAsync` (that IS auth 3) and goes for
  `GetInboxAsync`.
- `Roles.Invitee` — loses `Inbox.Read`. The account roles already carry it, so the inbox needs no new
  permission.
- `ClaimAsync` — "attach this invitation to the verified identity" becomes "attach it to the account".
  It is the natural bridge between auth 3 and auth 1, and the save-to-inbox action becomes
  "sign in to keep this".
- `web-invitee` — deleted outright (§2). `TokenStore`, `jwt.interceptor` and both guards go with it;
  the OTP gate is re-authored as a server-rendered page in the render app, and the cookie rides along
  on its own.
- `RsvpByInviteIdAsync` — ownership shifts from verified contact to account.

### Still open

- **`VerifiedContactLink`.** It exists so someone invited by email can open their inbox with a phone.
  An account already holds both an `Email` and a `PhoneE164`, so this may fold into account contact
  linking rather than surviving as its own table.
- **Migration.** Live 30-day JWTs are sitting in guests' browsers right now. Honour them for the inbox
  until they expire (30 days, self-clearing), or cut over hard.

---

## 4. Server-rendered invitation (the "render app")

**Today.** A guest's invitation is an Angular page that creates a **sandboxed iframe**, then posts the
invitation data into it, and a script inside binds it to the markup.

**The plan.** Render the template into a **bare standalone document on the server** — no Angular shell,
no app CSS, **no iframe**. Per §2 the render app owns the whole guest surface on `me.invites.blog`: the
auth pages check, set a cookie and redirect to `/r/:renderId`, which is the invitation itself.

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

### Half of this is already built

`InviteRenderService` already does the whole *resolution* half server-side — the part that carries the
business logic. It resolves rules into `resolvedBlocks`, maps the theme onto the manifest's declared
`cssVar`s as `themeVars`, overlays the `fields`/`imageSlots` dot-path map with per-role scoping, honours
the campaign's **frozen** manifest and **pinned** package URL.

What is missing is only the *applier*: `data-var` → textContent, `data-href`, `data-src` (plus gallery
expansion), `data-optional` hiding, `data-block` display. That is the top ~120 lines of
`TemplateInjector.Js` and it is mechanical AngleSharp work against a payload that already exists. Don't
plan this as "reimplement the contract in C#" — the contract is already resolved.

### Decided: the server becomes the ONLY binder

The JS injector does not survive as a second binder. It collapses to a **data-blind scroll shim** —
roughly thirty lines that add `.is-visible` / `.is-open` and set `--ib-progress`, and know nothing about
invitation data at all.

Everything else it does today moves or disappears:

| Injector does today | Under SSR |
| --- | --- |
| `data-var` / `data-href` / `data-src` / galleries | server |
| `data-optional`, `data-block` | server |
| `themeVars` on `:root` | server, as an emitted `<style>:root{…}</style>` |
| stripping `@media (prefers-reduced-motion: reduce)` | **publish time**, in `TemplateCompiler` — not per request |
| `reportHeight` / progress postMessage handshake | gone (it only ever existed for the iframe) |
| `.is-visible`, `.is-open`, `--ib-progress` | **stays** — the shim |
| `invite:data` event + `window.invite` | stays; the shim dispatches it from the still-inlined `#invite-data` |

**This is the main prize.** With one implementation of binding, the behaviour-drift risk this plan used
to carry doesn't need managing — it doesn't exist.

**Why the shim can't go too.** Those last three are scroll state; the server cannot precompute them.
The CSS-only replacement is `animation-timeline: view()` / `scroll(root)` plus `@property --ib-progress`
— which is what TEMPLATE-GUIDE already recommends — but **iOS Safari only got scroll-driven animations
in Safari 26.** A guest on iOS 17 or 18 would get a completely static invitation, silently, and motion
is the entire product. Revisit when that support floor moves, not before.

(Note `html.no-js` in `a-love-story`'s CSS: nothing sets it today, so it is dead code — but it is the
right hook if a no-JS path is ever wanted.)

**Editor preview follows from this.** It previews by fetching a server-rendered document per edit
(debounced) and swapping it into the sandboxed iframe, rather than posting data to a JS binder. A
round-trip per edit instead of a postMessage is nothing at a ~300ms debounce, and it is what makes
"one binder, period" true.

**The iframe survives in exactly one place** — those previews inside the signed-in app, where a
designer's JS would otherwise run same-origin with the account session. Two containers, two consumers,
one isolation contract:

| | guest invitation | editor preview |
| --- | --- | --- |
| where | `me.invites.blog/r/:renderId` | `invites.blog`, inside the builder |
| container | top-level document | sandboxed iframe |
| isolation | CSP `sandbox` header | `sandbox` attribute |
| flags | see below | the same, minus top-navigation |
| bound by | the server | the server — the preview loads a rendered document |

### Decided: authorize with an HttpOnly cookie, never with the URL

**The trap.** Today the invite token sits in the *parent* URL and the opaque-origin iframe cannot read
it. Make the invitation a top-level document and the template's own JS can read `location.href`. With
the CSP `sandbox` below, `allow-top-navigation-by-user-activation` means a script cannot navigate away
on its own to leak it — but a URL is still the wrong place for a credential, because it survives in
history, in `document.referrer`, and in anything a guest screenshots or forwards.
**Nothing that authorizes goes in the URL.** A short-lived signed URL — the original sketch here — is
exactly that mistake: valid for its whole window, so a forwarded one is a working invitation for
someone else.

**How much is at stake — and why §3 comes first.** As things stand today, more than one invitation.
The invitee JWT (`InviteeJwt.Issue`) carries only `contact_type` + `contact` — no user id, no role, no
`jti`, no campaign or invite scope. That contact *is* the whole authorization: it reads the person's
entire inbox across every campaign, opens any invitation they're on, RSVPs as them, and claims invites,
for 30 days, with no server-side session record and no revocation. One leak is every invitation that
person will ever receive.

§3 replaces that with a cookie scoped to one campaign on one device, which is why it is sequenced
first: it turns the thing being kept away from template JS from "everything" into "this campaign". With
one host (§2) that sequencing matters more, not less — the sandbox is what keeps the script away from
the cookie, and §3 is what makes the cookie cheap if it ever fails.

**The handoff is a redirect, not a protocol.** §2 put the guest auth pages and the rendered document on
the same host, which deletes the cross-origin cookie problem outright — there is no handoff nonce, no
Redis round-trip, no second hop. The render app checks, sets its own cookie, and redirects:

1. `me.invites.blog/i/:token` runs the existing check — raw token → `InviteTrustedIp` (up to 3 IPs, OTP
   reauth from an unrecognized one). Or `/e/:campaignId` runs auth 3's OTP gate + guest-list match.
2. It sets the cookie and `302 → /r/<renderId>`.

```
GET  https://me.invites.blog/i/<token>
302  https://me.invites.blog/r/8f2c1a9e4b7d
Set-Cookie: ib_inv=<opaque>; HttpOnly; Secure; SameSite=Lax; Path=/
            (no Domain attribute — host-only)
```

An earlier draft of this section routed auth through `invites.blog` and handed off with a single-use
nonce, because a cookie's `Domain` may only name the setting host or a parent — never a sibling. That
constraint is real; it simply stops applying once both halves live on one host.

- The guest lands on a URL with **no credential**. A leaked render id without the cookie 302s back to
  `/i/:token` and re-enters the normal IP-gated flow.
- The token never appears in the rendered document's own URL, and `Referrer-Policy: no-referrer` on the
  auth pages keeps it out of `document.referrer` too.
- Template JS cannot read an HttpOnly cookie.
- Refresh, back-button and re-open all work — which a bare single-use render URL does not, and guests
  refresh invitations constantly.
- The cookie grants exactly one thing — render this invitation — and there is no browser-facing API on
  the host to spend it against (§2). That, plus the sandbox, is what replaced the "cookieless separate
  origin" instinct in the first draft: what actually mattered was never the absence of a cookie, but
  the absence of anything worth stealing and any way to use it.

**Two entry points, one exit.** `/i/:token` is anonymous and IP-bound and holds no session at all;
`/e/:campaignId` is session-bound (auth 3's campaign cookie, §3) with no IP trust whatsoever. Neither
can be derived from the other — half the guests never have a session. Both already funnel through the
single `Render` delegate in `InvitesController`, which is where the render cookie should be minted.

**Do not re-run the IP check when serving `/r/*`.** `IsTrustedIpAsync` *writes* on every call — it
bumps `LastSeenAt`, or auto-trusts on a first-ever open. Re-checking on each render double-writes, and
mobile egress IPs change mid-session, so a guest could pass at `/i/:token` and fail at `/r/…` seconds
later, mid-invitation. The cookie is the proof; the IP is checked once, when it is issued.

**The render app must not publish a port.** `Program.cs` clears `KnownProxies`/`KnownNetworks` and
trusts `X-Forwarded-For` unconditionally, which is only safe because Caddy is the sole hop. A directly
reachable render service makes IP trust and both rate limiters spoofable.
### Decided: drop the shell chrome, but fix the contract first

The Angular page is not only an iframe wrapper — `event-invite.html` also owns the floating RSVP button,
the save-to-inbox FAB, and the loading / not-on-list / cancelled / error states. **Three** routes do
this, not one: `/i/:token`, `/e/:campaignId`, `/invites/:inviteId`.

RSVP moves into the template, where it half is already. But **`a-love-story` has no RSVP link at all** —
its finale asks "Will you be there?" and ends with "Fin." It depends entirely on the shell button today.
And nothing in the manifest or `RawTemplateContractTests` requires an `rsvp.link` element, so a community
designer can ship an unresponddable invitation and pass review by accident.

Order of work, and it matters:

1. Make `[data-href="rsvp.link"]` a **required** contract element, enforced at publish/review time next
   to the other contract checks.
2. Add one to `a-love-story`.
3. *Then* the shell RSVP button is genuinely redundant and can go.

Save-to-inbox has no template equivalent — decide whether the render document emits it itself or it goes.
Non-invitation states (cancelled, not on the guest list, error) are served by the render app's own
auth pages, unsandboxed: only redirect to `/r/:renderId` once there is an invitation to show.

### The isolation, without the iframe

Losing the iframe looks like losing the opaque origin that `sandbox="allow-scripts"` (without
`allow-same-origin`) gives a template today. It isn't: **CSP's `sandbox` directive applies to a
top-level document too.** Serve `/r/*` with it and the invitation gets the same opaque origin while
still being one document with no nested viewport — the property we thought we were trading away for
the `vh` fix.

**Measured in Chrome, not assumed.** A rendered `gilded-hour` served with the header below reports
`origin: null`; `document.cookie` and `localStorage` both throw `SecurityError`; the document contains
zero iframes; the gallery is six images rather than thirty-six; and the template's own "Reply now" link
still navigates on a click.

```
Content-Security-Policy:
  sandbox allow-scripts allow-popups allow-popups-to-escape-sandbox;
  default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline';
  img-src 'self' data:; font-src 'self' data:; base-uri 'none'; form-action 'none'
Referrer-Policy: no-referrer
```

| flag | needed by |
| --- | --- |
| `allow-scripts` | templates may carry their own JS |
| `allow-popups` + `allow-popups-to-escape-sandbox` | the `target="_blank"` venue links (`aurora-vows:130`, `gilded-hour:785`). Without *escape*, the map opens inside the sandbox with an opaque origin and breaks |

That is exactly today's iframe sandbox (`event-invite.html:52`) — no additions.

**`allow-top-navigation-by-user-activation` was tried and dropped.** An earlier draft of this section
added it for `rsvp.link` and claimed it would also stop a script navigating away to leak the URL. Both
halves were wrong, and testing said so: the RSVP link follows a click **without** the flag, and **with**
the flag a script still navigates away on a timer with no user gesture at all. Those flags govern a
*nested* document navigating its top-level ancestor; a top-level document navigating *itself* is not
gated by them.

**So a hostile template can navigate away and take `location.href` with it.** The design survives that
only because nothing in the URL authorizes anything — the render id is opaque and worthless without the
HttpOnly cookie, and `Referrer-Policy: no-referrer` keeps the invite token out of `document.referrer`
on the way in. It is not a defence that can be added later: **never put a credential in this URL.**

The header goes on `/r/*` only. The auth pages are ordinary documents on the same host and run nobody
else's code.

> **Still unverified: iOS Safari.** Everything above was measured in Chromium. The engine that matters
> most for guests has not been checked.

**And `/assets/*` must resolve on the render origin.** Campaign photos are stored relative
(`Urls:AssetsBase=/assets`) and the CSP is `img-src 'self'`. Caddy already routes `/assets/*` → MinIO on
every host, so this is free — and exactly the kind of thing that gets missed and then looks like "the
images broke". Note this is the one proxy that **stays** on `me.invites.blog`; §2 removes `/api/*`.

### Still open

- Whether `invites.blog/invitation/:campaignId` (opening a received invitation from the account inbox)
  redirects to the render origin too, or keeps a framed preview — it is reached from a signed-in
  session, which is the case for keeping the iframe.
- Cookie lifetime, and what happens when it expires mid-read. An expiry mid-invitation should bounce
  to `/i/:token` and silently re-issue, not show an error — but that path only exists for the personal
  link; the shared link has no token to bounce to and has to re-run the OTP gate.
- Personal invite tokens have **no expiry and no revocation** (`Invite` has neither `ExpiresAt` nor
  `RevokedAt`), so a leaked one is valid forever unless the campaign is re-finalized. That is true today
  and SSR doesn't change it — but the render origin is a good moment to decide whether it should stay
  true.

**Done when:** a guest link renders a filled invitation as **one top-level document with no iframe**,
opaque-origin under CSP `sandbox`, with no credential in its URL; the invitation carries its own RSVP;
and the editor preview renders through the same server binder.

**What it will NOT fix:** scroll frame rate. Measured — swapping all six photos for 1×1 pixels changed
nothing (20.4 ms → 20.1 ms). The cost was 56 continuously animating decorations, and they cost the same
in a top-level document.

---

## 5. invites.lens — the event photo box

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
