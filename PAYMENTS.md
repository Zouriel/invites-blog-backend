# Payments: Bank of Maldives (BML Connect)

How invites.blog takes card payments, and how to test it. BML's docs: https://bankofmaldives.stoplight.io/docs/bml-connect
(several in-page links there are broken; the raw pages are at
`https://bankofmaldives.stoplight.io/api/v1/projects/cHJqOjgzODg3/nodes/<slug>?branch=main`).

## How it works

| Step | Where |
|---|---|
| Buyer sees the **Review and pay** screen: item, total in MVR, merchant and country, delivery, refund terms, card logos, and for Premium/Studio (Venue) that it renews automatically; must tick "I agree" | web-inviter `shared/checkout/` |
| `POST /api/billing/checkout` refuses without the acceptance or with an old terms version; records `TermsAcceptedAt` + `TermsVersion` | `BillingService.CheckoutAsync`, `Legal/LegalTerms.cs` |
| A BML transaction is created (Redirect method): amount in laari, our payment id as `localId`, `customerReference`, `redirectUrl`, `webhook` | `Infrastructure/Payments/BmlPaymentProvider.cs` |
| **Webhook** (`POST /api/payments/webhook`) is the primary result, as BML's merchant rules require. Signature checked (`X-Signature` = sha256hex(nonce + timestamp + API key)); then the transaction is **re-fetched from BML's API**, and it counts only if its amount, currency and `localId` match our payment | `BmlPaymentProvider.HandleWebhookAsync`, `PaymentService.ProcessAsync` |
| Safety net: every 2 minutes, pending BML payments are asked about; the return page (`?paid=`) asks too, so a lost webhook never leaves a buyer without what they paid for. Pending past 8 days is closed as failed | `Infrastructure/Payments/PaymentSweeper.cs`, `GET /api/billing/payments/{id}` |
| Premium/Studio (Venue): the first payment saves the card (BML customer + `tokenizationDetails`, card-on-file, UNSCHEDULED). From a day before the plan ends the saved card is charged, the plan extended, and a receipt emailed. A failure is retried a day later; three in a row turns auto-renew off. The buyer can turn it off under Billing | `SubscriptionRenewalService`, `POST /api/billing/auto-renew/stop` |
| Refunds: BML has no refund API. Refunds are made in the BML merchant dashboard | |

Nothing retries a POST to BML: a repeated "create transaction" or "charge" would charge twice.

## Configuration

| Setting | Meaning |
|---|---|
| `Payments:Enabled` | Off: checkout says "being set up" and points at Ask us. Nothing background runs. |
| `Payments:Provider` | `Bml`, or `Fake` for local dev (dev checkout page, fake saved cards). |
| `Payments:Bml:ApiKey` | The app's **private** key from the BML merchant dashboard (Connect → Create App). Secret: env/.env only. |
| `Payments:Bml:BaseUrl` | UAT `https://api.uat.merchants.bankofmaldives.com.mv` (default) or production `https://api.merchants.bankofmaldives.com.mv`. |
| `Payments:Bml:RequireSignature` | Default true. Unsigned or badly signed webhooks are refused; the 2-minute sweep still finishes those payments. |

In `invites-blog-deploy`: `PAYMENTS_ENABLED`, `PAYMENTS_PROVIDER`, `BML_BASE_URL`, `BML_API_KEY`.

## Testing in UAT

Use the UAT test cards BML provides (ask BML; they are not kept in this repo).

1. Get UAT dashboard access from BML, create an App, copy its private key.
2. Run the API with `Payments__Enabled=true Payments__Provider=Bml Payments__Bml__ApiKey=<key>` (UAT is the default base URL).
   Locally BML can't reach the webhook; the return page and the 2-minute sweep finish the payment instead. On a
   public host the webhook arrives too.
3. Buy a Gold pass with each card: review → tick → Pay → BML page → card → back. Expect "Payment received", the pass on,
   and the payment `Paid` with `terms_version` set.
4. Cancel on BML's page: expect "didn't go through" and the payment `Failed`.
5. Buy the Premium pass (monthly): expect "Renews automatically" under Billing. Set the account's `subscription_ends_at` to an hour
   from now and restart the API: expect a second payment "renewed automatically", the plan a month longer, and a receipt email.
6. Turn off automatic renewal under Billing.

## BML documentation issues found while building this

- The API reference's request schema for `POST /public/v2/transactions` is an ambiguous `oneOf`: BML's own examples fail
  validation on their mock server, so that call can only be verified in UAT.
- Looking up a transaction: the API reference has `/public/transactions/{id}`, the guides `/public/v2/transactions/{id}`.
  The first is used; the second is the fallback.
- `POST /public-customers/charge`: the reference requires `tokenId`, the guide says it's optional. It's always sent.

## Staging (staging.invites.blog) and UAT results — 2026-09-29

Staging runs from `invites-blog-deploy/compose.staging.yml` (own Postgres, MinIO bucket and secrets; routes in
`Caddyfile.d/invites-blog-staging.caddy`; noindex). It uses BML's UAT app "BML Merchant Services App", merchant
"APU MERCHANT USD 7", which accepts **USD only**, so staging sets `Payments:ChargeCurrency=USD` (MVR prices charged in
dollars at the price book's rate; the review screen shows both). Production charges MVR.

Verified on UAT with the real site:
- Creating a payment, BML's page, 3-D Secure (Mastercard's test ACS), return to `/billing?paid=…`: works.
- **The webhook arrives with a valid signature and is what marks the payment paid** (log: `Payment webhook:
  PaymentSucceeded` then `Payment … paid`). Repeated deliveries are applied once.
- `/public/transactions/{id}` and `/public/v2/transactions/{id}` both answer on UAT.
- BML's first test card pays. The second test card is **declined** by BML's test issuer ("declined by issuer
  or payer authentication was not able to be successfully completed"); BML lets the buyer retry on the same page.
- BML's page has no Cancel button: a buyer leaves with Back, and the payment closes after BML's 7-day expiry.
- **Open with BML: card-on-file.** Payments made with `tokenizationDetails.tokenize=true` (UNSCHEDULED or RECURRING)
  come back with a token (`tokenProvider: pomelo-vault`), but the card is never added to the customer
  (`GET /public-customers/{id}/tokens` stays empty), no `NOTIFY_TOKENISATION_STATUS` webhook is sent, and
  `POST /public-customers/charge` answers `PP-TKN-007 No default token found` (or `PP-G-400` when the transaction's
  token is passed). Until BML enables/explains card-on-file for this merchant, automatic renewal can't charge:
  the renewal sweep then turns auto-renew off and emails the account to renew by hand.

## Google Play (the app)

The website pays by BML as above, untouched. The Android app pays through Google Play billing, and a purchase has the same
effect by the same path: `BillingService.FulfilAsync`. The code is `Infrastructure/Payments/GooglePlay`, the product table `GooglePlayCatalog`.

| Step | Where |
|---|---|
| The app asks what can be bought (`GET /api/billing/google-play/offers?eventId=`); it shows what the server offers and Play also returns | `GooglePlayPurchaseService.OffersAsync` |
| The purchase carries the account and event ids (obfuscated account and profile ids, the account and event's GUIDs: random, nothing personal) | the app |
| `POST /api/billing/google-play/purchases` `{productId, purchaseToken, eventId?}` asks the Android Publisher API (`purchases.products.get`, `purchases.subscriptionsv2.get`), and only what it answers counts | `RedeemAsync` |
| In one transaction, under the token's unique row: a `payments` row (`provider = google_play`, Google's order id, amount and currency from `orders.get`) and `FulfilAsync`. Repeats and races find it done | |
| After the commit: consumed or acknowledged at Google. If that fails, the 5-minute sweep retries, and the app does it too | `GooglePlaySweeper` |
| `POST /api/billing/google-play/notifications`: Real-time Developer Notifications by Pub/Sub push, verified by its Google-signed token (audience and sender). Only says what to ask Google about | |
| Every 6 hours: subscriptions near their end re-checked, the voided list read, amounts Google hadn't shown filled in. All of it works with no notifications at all, only later | |

A subscription's end date is Google's. Refunds and revocations take back what was bought (the pass, the year, the emails, the plan).
Store money isn't MVR: amounts are in the buyer's currency, and `XXX` with amount 0 means Google hasn't shown it yet (`orders.get` needs the
"View financial data" permission). Nothing here adds store money to anything. Event cancel doesn't refund a store payment: only Google can.

Test purchases (`purchaseType` 0, `testPurchase`) are refused unless `GooglePlay:AllowTestPurchases` is true, and are recorded with `is_test`.

| Setting | Meaning |
|---|---|
| `GooglePlay:PackageName` | `blog.invites.app`. Unset: off. Offers say nothing is available, purchases answer 503 and stay open at Google |
| `GooglePlay:ServiceAccountJson` or `GooglePlay:ServiceAccountFile` | The service account's key, one line of JSON or a path. Secret: never in git |
| `GooglePlay:AllowTestPurchases` | Default false |
| `GooglePlay:Notifications:Audience` | The audience set on the Pub/Sub push subscription. Unset: notifications answer 503 |
| `GooglePlay:Notifications:ServiceAccountEmail` | The service account set on that subscription |

In `invites-blog-deploy`: `GOOGLE_PLAY_PACKAGE_NAME`, `GOOGLE_PLAY_SERVICE_ACCOUNT_JSON`, `GOOGLE_PLAY_ALLOW_TEST_PURCHASES`, `GOOGLE_PLAY_NOTIFICATIONS_AUDIENCE`, `GOOGLE_PLAY_NOTIFICATIONS_EMAIL`.

Tests: `GooglePlayPurchaseTests`, the real client over a socket to a stub of Google, on real Postgres (`IB_TEST_POSTGRES`). Never tried against Google itself until the Play Console is set up.

## App Store (the iPhone app)

The iPhone app pays through Apple's In-App Purchase (StoreKit 2), with the same products, ids and rules as Google Play
(`AppStoreCatalog` is `GooglePlayCatalog`), and the same effect by the same path: `BillingService.FulfilAsync`. The code is
`Infrastructure/Payments/AppStore`. Rules and sources: the app repo's `docs/APPLE-PURCHASES.md`.

| Step | Where |
|---|---|
| The app asks what can be bought (`GET /api/billing/app-store/offers?eventId=`): the same answer as Play's offers (a test checks it) | `AppStorePurchaseService.OffersAsync` |
| Before buying, `POST /api/billing/app-store/intents` `{productId, eventId?}` records account, event and product and returns a UUID. Refused unless it's on offer now. The app sets it as the purchase's `appAccountToken`; Apple signs it into the transaction. StoreKit has no other field of ours, so this is how a transaction is tied to an account and an event | `IntentAsync` |
| `POST /api/billing/app-store/transactions` `{signedTransaction, eventId?}`: the JWS StoreKit returned. Its signature and certificate chain are checked against Apple Root CA - G3 (below); then Apple's server is asked for the same transaction (Get Transaction Info, or Get All Subscription Statuses), and only what it answers counts | `RedeemAsync` |
| In one transaction, under the original transaction id's unique row: a `payments` row (`provider = app_store`, Apple's transaction id, amount and currency from Apple's signed `price`/`currency`) and `FulfilAsync`. Repeats and races find it done | |
| The app finishes the StoreKit transaction only after "applied". Unfinished, StoreKit hands it to the app again at every start; there is nothing to settle with Apple from here | the app |
| `POST /api/billing/app-store/notifications` `{signedPayload}`: App Store Server Notifications V2, signed by Apple and checked the same way (and the transaction inside it too). Only says what to ask Apple about | `HandleNotificationAsync` |
| Every 6 hours: subscriptions near their end re-checked, and Apple's notification history for the last 3 days read and acted on, so refunds, renewals and purchases whose app never came back are seen with no notification at all | `AppStoreSweeper` |

**Signature check.** What Apple's own app-store-server-library (`SignedDataVerifier`/`ChainVerifier`) checks; Apple publishes no .NET
version. ES256 only; `x5c` of exactly leaf, intermediate, root; the leaf carries OID 1.2.840.113635.100.6.11.1 and the intermediate
1.2.840.113635.100.6.2.1; the chain is built from the leaf through the intermediate that was sent to a root we hold (the root in `x5c`
is never trusted for being there), valid now, revocation checked online (a check that can't be made is a 503, not a yes or a no); the
signature by the leaf's P-256 key; the payload's `bundleId` is ours. The root is compiled in (`AppStoreTrust`, SHA-256
63:34:3A:BF:…:3E:91:79, a test checks it) and can only be replaced in code, never by configuration: tests use their own.
Transactions from Xcode's local StoreKit testing are signed by Xcode, not Apple, and are always refused.

Refunds and revocations (`revocationDate`, `REFUND`/`REVOKE`) take back what was bought, as for Play (shared `StoreRefunds`). A
subscription's end date is Apple's (grace period included). Apple has no API to stop someone's subscription, so account deletion
tells them to cancel it in their Apple account and forgets who bought; the original transaction id stays, so it can't be redeemed again.
Event cancel doesn't refund an App Store payment: only Apple can. Not handled: `REFUND_REVERSED` (a refund Apple undid stays taken
back; grant it by hand) and `CONSUMPTION_REQUEST` (no consumption data is sent; Apple decides the refund without it).

Sandbox purchases (TestFlight, sandbox testers, **App Review**) are refused unless `AppStore:AllowTestPurchases` is true or the
buying account is in `AppStore:TestAccountIds`; they are checked with the sandbox server and recorded with `is_test`. App Review
buys in the sandbox against the production server, so the review demo account must be listed there (or test purchases switched
on while in review), or the reviewer's purchase is refused and the app rejected. Production notifications naming another app
(`appAppleId`) are ignored.

| Setting | Meaning |
|---|---|
| `AppStore:BundleId` | `blog.invites.app`. With the three below unset: off. Offers say nothing is available, everything else answers 503 |
| `AppStore:IssuerId`, `AppStore:KeyId` | From App Store Connect > Users and Access > Integrations > In-App Purchase |
| `AppStore:PrivateKey` or `AppStore:PrivateKeyFile` | The In-App Purchase key (.p8), its text (one line with `\n` is fine) or a path. Secret: never in git |
| `AppStore:AppAppleId` | The app's numeric Apple ID. Optional; when set, production notifications for another app are ignored |
| `AppStore:AllowTestPurchases` | Default false: sandbox purchases count for nobody but the accounts below |
| `AppStore:TestAccountIds` | Our account ids (GUIDs, comma-separated) whose sandbox purchases count: the App Review demo account, our testers |
| `AppStore:ApiBaseUrl`, `AppStore:SandboxApiBaseUrl` | Tests only. Default `https://api.storekit.apple.com/` and `https://api.storekit-sandbox.apple.com/` |

In `invites-blog-deploy`: `APP_STORE_BUNDLE_ID`, `APP_STORE_ISSUER_ID`, `APP_STORE_KEY_ID`, `APP_STORE_PRIVATE_KEY`, `APP_STORE_APP_APPLE_ID`,
`APP_STORE_ALLOW_TEST_PURCHASES`, `APP_STORE_TEST_ACCOUNT_IDS`. In App Store Connect the Server Notifications V2 URL is `https://invites.blog/api/billing/app-store/notifications`
for production (and the same for sandbox, on staging if preferred).

Tests: `AppStorePurchaseTests`, the real client over a socket to a stub of Apple that signs with a chain made like Apple's, on real
Postgres. Never tried against Apple itself: that needs the App Store Connect setup and a device or TestFlight build.
