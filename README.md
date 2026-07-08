# invites-blog-backend

The API for **invites.blog** — a premium animated digital-invitation platform. Inviters create
beautiful, role-aware, personalized invites **with no account** and send unique links by email;
invitees open their link and RSVP with **zero login**.

This repo is the ASP.NET Core / .NET 10 backend: REST API, domain and business logic, EF Core
persistence, the template compiler, and the retention worker.

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

## Security & privacy

- **No accounts.** Inviters hold a possession token + magic links; invitees use the link itself,
  with optional OTP. Only token **hashes** are stored.
- **Sandboxed templates.** Compiled templates run in a `sandbox="allow-scripts"` iframe under a
  strict CSP; guest content is bound as text; rules are resolved server-side.
- **Data protection.** Tokenized self-service removal, hashed suppression list, and a
  retention auto-delete worker.
