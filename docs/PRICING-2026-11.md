# Pricing change 2026-11-13 (owner-approved): cheaper Premium, AI designer add-on, Download all

Single spec for backend, web (`invites-blog-frontend`, web-inviter), app (`invites-blog-app`), UI libraries and
store config. Copy into each repo's `docs/` unchanged.

## 1. Prices

| what | web (BML, MVR) | Google Play / App Store (USD tier) |
|---|---|---|
| Premium, a month | MVR 77 | $4.99 (`premium_monthly`, existing product, price lowered) |
| Premium + AI designer, a month | MVR 154 (77 + 77) | $9.99 (`premium_ai_monthly`, NEW product) |
| AI designer added mid-month (web) | days left in the paid month x 77/30, rounded up to whole MVR (min MVR 5) | the store's own upgrade proration |
| Studio (`venue_monthly`) | unchanged (MVR 775) | unchanged |
| Gold / Platinum / Keep photos / sending | unchanged | unchanged |

Rate stays `MvrPerUsd` 15.42. Prices are admin-editable (`IPriceBook`); production may have a SAVED price list
that overrides code defaults: update the saved list too (new field `DesignerAiMonthly`, default 77; Premium 77).

Later (NOT now): vector generation (200 a month) is another +$5 / +MVR 77 add-on. Design the add-on as a list
(`addOns: ["designer-ai"]`) so a second one fits without a new model; stores will then get further levels.

## 2. What each plan gets

**Premium ("everything there is")**: every perk the product has for the subscriber's events and account:
2 GB per event they organise, no "Made with invites.blog" mark, everything kept while subscribed, face grouping on
their events, Download all / download or share more than 10 at once (anywhere they can see photos), Favourites
download, plus whatever else Premium gets today. **AI designer (Ask AI / agentic designing) only with the add-on.**

**Studio**: unchanged, and keeps the AI designer included (as today) plus Download all.

**Gold / Platinum pass events**: unchanged; they keep face grouping. Download all is also allowed inside an event
that has a Gold or Platinum pass.

**Free**: download or share up to **10 at a time** (select up to 10; an 11th tap explains "Download more than 10 at
once with Premium" with a Get Premium action where selling is allowed). No "Download all" zip; the button stays
visible and opens the same Premium offer.

Rule for "more than 10 / Download all": allowed when **the viewer's own account** is on Premium or Studio, **or**
the event has an active Gold/Platinum pass. Enforce on the SERVER (archive/download-link endpoints for host, guest,
celebrant, favourites: 403 with a clear code `download_limit` when > 10 ids or no ids), not only in the UI. Sharing
is device-side: limit the selection to 10 in both apps.

Store rule: inside the apps on the Maldives storefront / Play, upsell text must go through the existing
`withoutSelling` / store-rules helpers (docs/STORE-RULES.md); never link to the website to buy.

## 3. AI designer add-on

- Subscribing to Premium (web Plans, app Premium page, Plans page, Billing): a tick "Add the AI designer
  (+$5 / +MVR 77 a month)" under the Premium card. Ticked = buy `premium-ai-monthly` (web) / `premium_ai_monthly`
  (stores); not ticked = `premium-monthly`.
- Already on Premium: Billing (web + app) shows "AI designer: On/Off" with "Add it" or "Turn it off".
  - Web add: BML charge for the prorated amount now, add-on active immediately, and the monthly renewal charges 154
    from then on. Web turn off: AI stays until the paid month ends, renewal charges 77 after.
  - Stores: add = upgrade within the subscription group to `premium_ai_monthly` (App Store group levels, highest
    first: venue_monthly, premium_ai_monthly, premium_monthly; Play: replacement with prorated charge). Turn off =
    downgrade to `premium_monthly`, which the stores apply at the next renewal.
- Entitlement check: the designer agent (`DesignAssistantService` and wherever else Premium/Venue gates it) allows
  Studio, or Premium with the add-on active (`DesignerAiUntil > now` or add-on renewing).
- Existing Premium subscribers (on 2026-11-13 they have AI): keep AI until their current period ends
  (`SubscriptionEndsAt`, or store expiry); at that renewal they move to the new 77/$4.99 Premium without AI and
  can add it. Migration sets their add-on end = current period end, not renewing.

## 4. API shape (backend decides exact names; keep these meanings)

- `GET /api/plans`: Premium price 77; new `addOns: [{ key: "designer-ai", name: "AI designer", price: 77,
  billing: "per month", usd: 5, forKinds: ["Premium"] }]`; every `PlanDto` gets `perks: string[]` (the card
  bullets, server-written, so web and app show identical lists).
- Account / me: `addOns: [{ key: "designer-ai", active: bool, renews: bool, endsAt }]` and
  `canDownloadAll: bool` (account-level; events add their own via a field on the album/event response such as
  `downloadAll: bool`).
- Billing items: new `premium-ai-monthly` (subscribe with AI), `designer-ai-add` (prorated add-on for an existing
  Premium), `designer-ai-off` / `designer-ai-on` toggles for renewal. Review step (checkout-review) shows the right
  delivery + refund lines and "Renews automatically ... MVR 154".
- Stores: map `premium_ai_monthly` -> Premium + designer-ai in GooglePlay.cs / AppStore.cs and their notification
  handlers (upgrade, downgrade at renewal, expiry).

## 5. Cards must be the same everywhere

Every place that sells or describes Premium/Studio (web Plans page, Billing, checkout review, the FAQ in
pricing-faq.ts, guide/llms.txt; app plans.dart, plans_page.dart, premium_page.dart, studio_plan_page.dart,
static pages generator, docs/APP-STORE-LISTING.md) renders the server's `perks` and prices; no hand-written perk
lists. Grep for "Made with", "Premium pass", "200", "face grouping" and the old prices when done.
