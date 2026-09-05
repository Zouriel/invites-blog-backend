# invites-blog-backend

The API for **invites.blog** — animated digital invitations, and what happens around them.

A host picks or commissions a template, builds an invitation, and sends every guest their own
link. Each guest opens a **server-rendered, personalized** invitation — their name, their role,
the blocks that apply to them — and replies without creating anything. On the night, they open a
**camera inside that same invitation**, and everything anyone shoots collects in one place — open to
add to, and visible to the people who were invited.

This repo is the ASP.NET Core / .NET 10 backend: REST API, domain and business logic, EF Core
persistence, the template compiler, the server-rendered guest path, and the worker.

## What it does

**Invitations.** A builder that walks content → theme → roles → guests → venue → RSVP questions →
delivery. Guest lists arrive by hand or as an uploaded spreadsheet. Personalization is per guest:
their name, role-scoped content blocks, gender variants, and a rules engine deciding what each
person sees. A campaign **pins** its template package at booking, so an invitation sent months ago
still renders exactly as it did the day it was sent.

**Delivery and replies.** Every guest gets a unique tokenized link by **email**. Opening it needs no
account at all; the token is the credential, and an unfamiliar network is challenged with a code.
RSVPs land live on the host's dashboard. A guest who would rather use an account can sign in with an
email code, Google or Microsoft, and find every invitation ever sent to their address — including
ones sent before they signed up.

**Media buckets.** The place a night's photographs and clips end up — and a product of its own, sold
by the gigabyte. Guests open a camera from their invitation — front and rear, colour grades, tap to
focus, an exposure bias for a dark room — and every shot queues to a store that survives a locked
phone or a dead connection. Photos and **video** both, from the camera or straight off a camera roll.
Nothing is capped per file: the shot as taken is kept, alongside a screen-sized copy and a grid tile.
Adding is deliberately wider than looking — anyone at the party can contribute, because not everyone
who comes to one is on a list, while the grid itself is for the people who were invited. The host
moderates.

A bucket has its own name, its own cover, its own size and its own **date**, and it does **not** need
an invitation behind it — a trip, a reunion or a season of somebody's football club is a bucket with
no event attached. Sizes are 10, 20, 30 and 50 GB on a six-month term; every event still gets a free
one, so nothing that worked before costs anything now.

A bucket is an occasion rather than a drive, so it only **takes** anything on its night: open from
the start of that day in Malé until 24 hours after the event begins. That is the same window that
decides whether a guest is offered the camera on their invitation — one definition, in
`EventDayWindow`, because answered separately they drift and what that looks like is a camera leading
to a bucket that refuses every photo taken with it. Looking is never gated; the point of the thing is
what you have afterwards.

**Contribution codes.** A bucket's owner generates a **QR code**, prints it, and puts it on the
tables. Two kinds, chosen per code rather than per bucket, because the card on the table and the link
in a follow-up email want opposite answers:

- **Anonymous** — no sign-in at all. It asks for a name, believes it, and credits the photographs to
  it. Right for a room where everyone present was invited by the person holding the party.
- **Verified** — a one-time code to an email or phone, and only contacts on the event's **guest
  list** get in. The credit is then the host's name for that person, not one the contributor typed.

A code can be turned off, since a printed card cannot be recalled — and the last one made stays in
the dashboard to reprint, as an image, because the token behind it is stored hashed and can never be
read back.

**Who can look is a different question from who can add.** A campaign is the unit: it owns a guest
list, and may have an invitation, a media bucket, or both. That one list is who can see either of
them. Contributing is never a way in — anyone at the party can add through a printed code, and only
the people who were invited can look.

**Templates.** Three sources: first-party templates in this repo, community templates submitted by
designers and reviewed before publication, and bespoke commissions arranged through an inquiry.
Designers set a per-use fee; commissioned templates can be reserved to one customer.

**Privacy.** EXIF, IPTC and XMP are stripped from every uploaded image — these are photographs of
other people's guests, and a GPS tag would publish where a wedding was. There is a suppression list,
per-guest data removal by token, and a retention job that deletes a campaign's data on a timer.

## Companion repos

- [`invites-blog-frontend`](https://github.com/Zouriel/invites-blog-frontend) — Angular 22 workspace: `web-inviter` (invites.blog) + `web-invitee` (me.invites.blog) + the shared `ui` library
- [`invites-blog-deploy`](https://github.com/Zouriel/invites-blog-deploy) — Docker Compose + Caddy production topology
- **Authoring templates?** See [`TEMPLATE-GUIDE.md`](./TEMPLATE-GUIDE.md) in this repo.

## Stack

- **.NET 10** / ASP.NET Core (controller-based API)
- **EF Core 10** on **PostgreSQL 17**
- Layered architecture with **Scrutor** auto-DI
- **xUnit** test suite (**138 tests**)
- Full **RBAC** — every protected endpoint gates on `[HasPermission("…")]`
- Every endpoint returns a standard envelope: `{ success, message, data, errors }`

## Project layout

```
InvitesBlog.Domain/            # Entities + enums, RBAC authorization primitives
InvitesBlog.Application/       # Services, DTOs, pricing, tokens, rules, phone (E.164),
                               #   guest parsing, validation, ports/abstractions
InvitesBlog.Infrastructure/    # EF Core (Migrations/Persistence), storage, delivery/OTP/payment
                               #   providers, rendering, seeding, RawTemplates packager
InvitesBlog.Api/               # Controllers, middleware, authorization, DI wiring
InvitesBlog.TemplateCompiler/  # Template packaging + the trusted injector (SceneCompiler,
                               #   TemplateInjector, TemplateManifest)
InvitesBlog.Worker/            # Background retention job (auto-delete after retention window)
InvitesBlog.Tests/             # xUnit (pricing, tokens, phones, rules, compiler, services)
```

## Quick start

Requires the .NET 10 SDK and a reachable PostgreSQL 17 (see `.env.example` for the connection
string). Everything else runs with **no external services** by default.

```bash
dotnet run --project InvitesBlog.Api
# → http://localhost:8080   (OpenAPI at /openapi/v1.json)
```

On first start the API **applies EF migrations** and **seeds** the template gallery plus RBAC
(roles/permissions and the admin account from env). Out of the box it uses:

- **Local-filesystem storage**, served at `/assets`
- **Console** email/OTP — codes and magic links are written to the log
- A **fake** payment provider

Swap in real providers (PostgreSQL, MinIO/S3, Resend, Stripe) via `appsettings`/environment —
see [`.env.example`](./.env.example).

## Tests

```bash
dotnet test          # 138 tests
```

## How it fits together

1. Browse the seeded template gallery → create a campaign (you get a 256-bit access token; **only
   its hash is stored** — no account).
2. Build the invite in a **dynamic, manifest-driven builder**: the API exposes the chosen
   template's manifest so the frontend renders exactly the fields that template declares — one
   input per `data-var`/`data-href`, one image-upload slot per `<img data-src>`.
3. Add roles (each role unlocks template content blocks), venue, and inviter details (triggers a
   "resume your invite" magic-link email).
4. Upload guests from Excel (E.164 normalization, validation, duplicate + role/gender
   distribution) or add them manually.
5. Checkout — pricing is `$5` min incl. 50 invites, then `$1/10` (designer discount `$1/20`) —
   settled via an **idempotent** payment webhook, then dispatched.
6. Dispatch mints a per-guest secure token (hash stored only), renders the personalized message,
   and sends by email.
7. Invitee opens `/i/:token` → the API resolves the token, **resolves personalization rules
   server-side**, and returns the render payload; the invitee app injects it into a sandboxed
   `allow-scripts` iframe under a strict CSP. Guest content is bound as **text, never markup**.
8. RSVP with zero login; optional **email OTP** unlocks the inbox; a magic-link dashboard shows the
   delivery/RSVP report.
9. Guest "remove my data" anonymizes the guest and adds a **hashed suppression entry** honored on
   all future uploads.

## What's new / highlights

- **Dynamic manifest-driven builder** — fields and image slots are auto-derived from the template's
  tags; authors add arbitrary fields with no code change.
- **Template image slots** — inviters upload an image per slot; stored as campaign assets and
  injected at each `<img data-src>` path.
- **Managed template types** — categories are a first-class, admin-managed entity (add/deactivate),
  not free text.
- **Roles step** — per-role content blocks compile into personalization rules.
- **Public vs Dedicated templates** — a template can be reserved for one person's email, claimed
  via "Did you request a template?" with an **email OTP** code.
- **Email-only OTP at launch** (phone OTP disabled).
- **Resend** email provider with a signature-verified delivery webhook
  (delivered/bounced/complained → suppression, idempotent).
- **Server-rendered invitations** — the guest path renders on the server and is served as one
  top-level document under `sandbox; default-src 'none'`. No iframe, and the authority is an
  HttpOnly cookie rather than the URL, because a template may ship its own JavaScript and a
  document can read its own address.
- **The media bucket and camera** — an in-browser camera on the guest path: front/rear, colour
  grades baked in at capture, tap to focus, an exposure bias for a dark room, and an upload queue
  in IndexedDB so a shutter press never waits for the network. Originals are kept uncapped, with a
  2048px viewing copy and a 400px tile derived from each. **Video** is stored as uploaded with a
  poster frame drawn in the browser — pulling a frame out of an encoded clip needs a decoder the API
  does not have, and the browser is holding one already.
- **Media buckets as a product** — `MediaBucket` owns the storage: a title, a cover, a size chosen
  from 10/20/30/50 GB on a six-month term, and a quota enforced before a single object is written. A
  bucket may be attached to a campaign or stand entirely alone. Tier prices live in configuration
  (`MediaBuckets:Prices`), and `CapacityBytes` is frozen onto the bucket at purchase so repricing a
  tier can never resize one already sold.
- **QR contribution codes** — `MediaBucketQr`: a printed code that authorizes adding to one bucket
  and nothing else. The token is stored as a SHA-256 hash and the rendered PNG alongside it, so the
  dashboard can always show the code without the database ever holding a working one. Each code
  records whether it admits anonymously, counts its own scans and uploads, and can be revoked
  independently of the others.
- **Cloudflare R2** for assets behind a custom domain, with cache headers set per key so template
  packages revalidate while campaign images stay immutable.

## Not yet real

Worth knowing before reading the pricing code:

- **Media buckets are not billed.** Choosing a size grants it outright and starts the six-month
  term; nothing is charged. The price list is real and comes from configuration, and when checkout
  arrives it slots in front of `ChooseTierAsync` rather than replacing it.
- **Payments are not live.** `PricingCalculator` is complete and tested — $5 minimum, 50 invites
  included, $1 per block beyond, a per-use designer fee — but the only registered `IPaymentProvider`
  is `FakePaymentProvider`. No real money has moved through this.
- **Delivery is email only.** The landing page's Telegram and WhatsApp are marked "coming soon"
  and there is no provider behind either.
- **The worker is not deployed.** `RetentionCleanupService` lives in `InvitesBlog.Worker`, which has
  no container in production, so retention does not currently run. Background work that must run is
  registered in the API host instead.

## Security & privacy

- **No accounts.** Inviters hold a possession token + magic links; invitees use the link itself,
  with optional OTP. Only token **hashes** are stored.
- **Sandboxed templates.** Compiled templates run in a `sandbox="allow-scripts"` iframe under a
  strict CSP; guest content is bound as text; rules are resolved server-side.
- **Data protection.** Tokenized self-service removal, hashed suppression list, and a
  retention auto-delete worker.
