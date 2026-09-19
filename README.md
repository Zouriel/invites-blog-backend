# invites-blog-backend

The API for **invites.blog** — animated digital invitations, and what happens around them.

A host picks a template (or has one made), builds an invitation, and sends every guest their own
link. Each guest opens a **server-rendered, personalized** invitation — their name, their role,
the blocks that apply to them — and replies without creating anything. On the night, they open a
**camera inside that same invitation**, and everything anyone shoots collects in one place — open to
add to, and visible to the people who were invited.

This repo is the ASP.NET Core / .NET 10 backend: REST API, domain and business logic, EF Core
persistence, the template compiler, the server-rendered guest path, and the background jobs (photo
digests and media retention), which run inside the API host.

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

**Media buckets.** The place a night's photographs and clips end up — and a product of its own,
sized by the event's plan. Guests open a camera from their invitation — front and rear, colour grades, tap to
focus, an exposure bias for a dark room — and every shot queues to a store that survives a locked
phone or a dead connection. Photos and **video** both, from the camera or straight off a camera roll.
Nothing is capped per file: the shot as taken is kept, alongside a screen-sized copy and a grid tile.
Adding is deliberately wider than looking — anyone at the party can contribute, because not everyone
who comes to one is on a list, while the grid itself is for the people who were invited. The host
moderates.

A bucket owns a **size** and a **term** and nothing else: its name, its cover, its date and its guest
list are the event's, shared with the invitation. It does **not** need an invitation behind it — a trip, a reunion or a season of somebody's football club is a bucket with
no event attached. How much an event's buckets may hold, and for how long, comes from its plan
(`PlanCatalog`): Free for every event, a Party or Wedding pass bought per event, or a venue's plan
for events held there. Studio (designers and planners) and Venue (resorts and halls) are the only
subscriptions; hosts pay per event. Prices are in rufiyaa.

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

**One way in.** Everything starts as an event: a name and a night, `POST /api/campaigns/bare`.
What it *has* is chosen after that and is never final — `PUT /api/campaigns/{id}/template` pins a
gallery design onto an event that has none, `POST /api/campaigns/{id}/design` does the same with one
the customer brought, and `POST /api/campaigns/{id}/bucket` gives it somewhere for the photographs.
Both attach endpoints refuse an event that already renders something, because pinning exists so that
what was sent stays what was sent. Creating is deliberately anonymous, so the date rides in on the
create rather than a second call: the possession token that makes the event theirs is minted by that
very request and cannot be presented on it.

**Templates.** Two sources: first-party templates in this repo, and templates made in the template
designer by designer accounts, published privately, for one customer (reserved to their email), or to
the gallery. Made-to-order requests arrive through the inquiry form and are then designed and
published for the customer in the designer.

**Privacy.** EXIF, IPTC and XMP are stripped from every uploaded image — these are photographs of
other people's guests, and a GPS tag would publish where a wedding was. There is a suppression list,
per-guest data removal by token (the link is in every invitation email), and a media retention job
that removes an event's photos 90 days after its plan's cover ends, with notices along the way.

## Companion repos

- [`invites-blog-frontend`](https://github.com/Zouriel/invites-blog-frontend) — Angular 22 workspace: `web-inviter` (invites.blog) + `web-invitee` (me.invites.blog) + the shared `ui` library
- [`invites-blog-deploy`](https://github.com/Zouriel/invites-blog-deploy) — Docker Compose + Caddy production topology
- **Authoring templates?** See [`TEMPLATE-GUIDE.md`](./TEMPLATE-GUIDE.md) in this repo.

## Stack

- **.NET 10** / ASP.NET Core (controller-based API)
- **EF Core 10** on **PostgreSQL 17**
- Layered architecture with **Scrutor** auto-DI
- **xUnit** test suite (**883 tests**)
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
dotnet test          # 883 tests
```

## How it fits together

1. Browse the seeded template gallery → create a campaign (you get a 256-bit access token; **only
   its hash is stored** — no account needed, though a signed-in creator owns it from the start).
2. Build the invite in a **dynamic, manifest-driven builder**: the API exposes the chosen
   template's manifest so the frontend renders exactly the fields that template declares — one
   input per `data-var`/`data-href`, one image-upload slot per `<img data-src>`.
3. Add roles (each role unlocks template content blocks), venue, and inviter details (triggers a
   "resume your invite" magic-link email).
4. Upload guests from Excel (E.164 normalization, validation, duplicate + role/gender
   distribution) or add them manually.
5. Finish — the host gets the link to share, and if they chose email, every guest is mailed their
   own link: a per-guest secure token (hash stored only) and the personalized message. That first
   email, a resend and "add and send now" are the same email, with the guest's data-removal link.
   Sending isn't charged yet (see *Not yet real*).
6. Invitee opens `/i/:token` → the server admits the token, **resolves personalization rules
   server-side**, and renders the invitation as one top-level document under
   `sandbox; default-src 'none'`. Guest content is bound as **text, never markup**.
7. RSVP with zero login; optional **email OTP** unlocks the inbox; the host's dashboard (signed in,
   or from an older emailed dashboard link) shows the delivery/RSVP report.
8. Guest "remove my data" anonymizes the guest and adds a **hashed suppression entry** honored on
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
- **Media buckets as a product** — `MediaBucket` owns the storage and only the storage: a size
  within what the event's plan allows (`PlanCatalog`), and a quota enforced before a single object
  is written. Its name, cover, date and guest list are the campaign's, because every bucket has one —
  a bucket bought on its own is a campaign with no invitation, not a loose object.
- **QR contribution codes** — `MediaBucketQr`: a printed code that authorizes adding to one bucket
  and nothing else. The token is stored as a SHA-256 hash and the rendered PNG alongside it, so the
  dashboard can always show the code without the database ever holding a working one. Each code
  records whether it admits anonymously, counts its own scans and uploads, and can be revoked
  independently of the others.
- **Cloudflare R2** for assets behind a custom domain, with cache headers set per key so template
  packages revalidate while campaign images stay immutable.

## Not yet real

Worth knowing before reading the pricing code:

- **Plans are not billed.** The plans and their limits are real (`PlanCatalog`) and enforced, but
  passes, "Keep your photos", Studio, Venue and Studio pass credits are all granted by an admin in
  Admin settings; nothing is sold online yet.
- **Payments are not live.** `PricingCalculator` is complete and tested — MVR 50 per 100 invitations,
  after the 100 a Party pass or 500 a Wedding pass includes — but the only registered
  `IPaymentProvider` is `FakePaymentProvider`, and sending invitations is not charged at all today.
  No real money has moved through this.
- **Delivery is email only.** The landing page's Telegram and WhatsApp are marked "coming soon"
  and there is no provider behind either.
- **Guest-data retention cleanup does not run.** There is no background job deleting guest data
  after a campaign's retention period (photographs have one — `MediaRetentionService`). Background
  work that must run is registered in the API host.

## Security & privacy

- **Accounts are optional.** Inviters can work from a possession token and invitees from their own
  link, with OTP from an unfamiliar network; signing in adds history and cross-device access. Only
  token **hashes** are stored.
- **Sandboxed templates.** The guest path serves a template as one top-level document under a
  `sandbox` CSP (an opaque origin), with the credential in an HttpOnly cookie rather than the URL;
  guest content is bound as text; rules are resolved server-side.
- **Data protection.** Tokenized self-service removal (linked from every invitation email), a hashed
  suppression list, and media retention that removes an event's photos after its plan lapses.
