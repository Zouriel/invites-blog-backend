# Guidance: empty pages, tips, tours (web + app)

Owner-approved 2026-11-13. This file is the single spec for the web app (`invites-blog-frontend`, web-inviter),
the Flutter app (`invites-blog-app`), the backend and both UI libraries. Copy it into each repo's `docs/` as
`GUIDANCE.md` and keep the copies identical. Tip ids, tour ids, art keys and API shapes below are shared: never
rename one on one side only.

## 1. Rules (both apps)

1. Every tip, tour and offer shows **at most once per account, ever**. Seen = shown and dismissed, finished, skipped,
   or its action done. Once seen it never comes back (unless the user presses "Show tips again").
2. A tip is **skipped (and marked seen silently) when its action is already done** (`skipWhen` below), so
   experienced users see none of the basics.
3. **One tip on screen at a time**, at most **one tour offer per app session**. Others wait for a later visit.
4. Every tip/tour/offer has a **"Turn off tips"** action. Settings has a **Tips** switch and **"Show tips again"**.
5. State lives **on the account** (`/api/me/guidance`), so web and phone agree. The device keeps a cache for
   offline use and queues `seen` writes until online. Signed-out visitors: device storage only.
6. **Welcome tips/tours stop 30 days after the account was created** (`welcomeUntil`). Event-triggered offers
   (celebrant, first host, first guest) keep working after that, each once.
7. Never show a tip while: a dialog/sheet is open, the page is loading, the user is typing, the camera is open,
   or during the event wizard's Share/Send step.
8. Tips look different from real controls (NN/g): coach-mark style bubble, short text (title <= 6 words,
   body <= 20 words), never a sequence of more than 5 steps.
9. Accessibility: focus moves into a tour step and back, Escape closes, screen readers announce it, reduced
   motion turns off the spotlight animation.

## 2. API (backend)

All under the signed-in session. JSON camelCase.

```
GET  /api/me/guidance
  200 {
    tipsOn: bool,
    seen: string[],                 // tip/tour/offer ids
    welcomeUntil: string (ISO),     // account createdAt + 30 days
    progress: {                     // drives the Getting started checklist and skipWhen
      hasName: bool, hasPhoto: bool,          // profile
      hasEvent: bool,                         // hosts (organiser) at least one non-draft or draft event
      hasGuests: bool,                        // any hosted event has >= 1 guest
      hasSent: bool,                          // any hosted event has sent invitations or shared its link
      hasPhotoInAlbum: bool,                  // any hosted event's album has >= 1 photo/video
      hasDesign: bool                         // has made a template design
    },
    newRoles: [                      // events where I'm a celebrant/co-host and haven't opened them yet
      { campaignId, title, coverUrl|null, access: 'look'|'full', addedBy: string|null, addedAt }
    ]
  }
POST /api/me/guidance/seen   { ids: string[] }   -> 204   (idempotent; id = [a-z0-9.-]{1,64}; max 50 per call; max 500 stored)
PUT  /api/me/guidance        { tipsOn: bool }    -> 204
POST /api/me/guidance/reset                      -> 204   (seen = [], tipsOn = true)
POST /api/me/roles/{campaignId}/ack              -> 204   (sets acknowledgedAt on my celebrant row(s); also set
                                                          automatically when I open that event's dashboard)
GET  /api/demo/event                             -> 200   (public, cacheable) read-only sample event, see section 7
```

## 3. Getting started checklist (Me page, both apps)

Shown at the top of **Me** while the account is inside `welcomeUntil`, tips are on, `guide.checklist` not seen, and
not every item is done. Items tick themselves from `progress`:

| id | label | done when | action |
|---|---|---|---|
| name | Add your name and photo | hasName && hasPhoto | profile page |
| event | Start an event | hasEvent | New |
| guests | Add your guests | hasGuests | newest hosted event's dashboard, Guests tab |
| send | Send it or share the link | hasSent | newest hosted event dashboard |
| photo | Get your first photo | hasPhotoInAlbum | newest hosted event's album |

Header "Getting started" + "2 of 5". Closing it (x) marks `guide.checklist` seen. When all five are done, show a
one-time "All set" state, then mark it seen.

## 4. Offers (cards that ask "want a tour?")

| id | where | when | text | buttons |
|---|---|---|---|---|
| offer.welcome | Feed (top) | first open, inside welcomeUntil | "New to invites.blog? Take a quick tour." | Show me (tour.welcome) / Not now / Turn off tips |
| offer.role.{campaignId} | Feed + Inbox (top), and the dashboard on first open | for each `newRoles` item | "{addedBy} added you to "{title}" as a celebrant." (+ "You can see who's coming and all the photos." for look, "You can manage it with them." for full) | Open event / Show me around (tour.celebrant on that dashboard) / x |
| offer.host | Dashboard, first time I open an event I organise | not seen | "Your event is ready. Want a quick look around?" | Show me (tour.host) / Not now |
| offer.guest | Invitation page (/invitation/:id) first time | not seen | "You're invited! Here's how this works." | Show me (tour.guest) / Not now |
| offer.demo | Feed + Me empty pages (starter card, not a popup) | always while I host nothing | "See an example event" | opens the demo |

"Not now" marks the offer seen (it doesn't come back). The role card disappears once the event is opened
(`POST /api/me/roles/{id}/ack`).

## 5. Tours (3-5 steps, coach marks with spotlight)

Anchor ids are shared names; each app puts an anchor on the matching element.

- **tour.welcome**: `nav.feed` "Your events and the people in them show up here." → `nav.inbox` "Invitations sent
  to you land here." → `nav.new` "Start an event: pick a design, add guests, send." → `nav.me` "Events you host,
  your plan and settings."
- **tour.celebrant** (dashboard): `dash.guests` "See who's coming and what they replied." → `dash.album`
  "Every photo and video guests add lands here." → `dash.add-media` "Add your own photos too." → (full access only)
  `dash.celebrants` "You can manage this event with the organiser."
- **tour.host** (dashboard): `dash.guests` "Add guests one by one or upload a list." → `dash.send` "Send the
  invitations or share one link." → `dash.album-code` "Guests scan this QR code to add photos." → `dash.covers`
  "Pick the photos that head your event in the feed." → `dash.celebrants` "Add the people this event is for."
- **tour.guest** (invitation): `inv.view` "Open your invitation and reply here." → `inv.add-photos` "Add your
  photos from the day." → `inv.album` "Everyone's photos land here."

Each step: title-less short text, "2 of 4", Back / Next / Done, "Skip", "Turn off tips". Skipping or finishing
marks the tour seen.

## 6. One-off tips (anchored hints, shown on first visit to that screen)

| id | screen | anchor | text | skipWhen |
|---|---|---|---|---|
| tip.feed.templates | Feed | `feed.tab-templates` | "Browse designs for every occasion here." | hasEvent |
| tip.inbox.new | Inbox (only with >= 1 invitation) | first tile's New flag | "New marks invitations you haven't opened." | - |
| tip.new.who | New event, Who's it for stage | `new.who` | "Add the people it's for. They'll see guests and photos." | hasEvent |
| tip.me.settings | Me | `me.settings` | "Your plan, sign-in and tips live here." | - |
| tip.dash.people | Dashboard (organiser) | `dash.people-tabs` | "Switch between guests and celebrants." | hasGuests |
| tip.album.qr | Album / album tab (organiser) | `dash.album-code` | "Print or show this code so guests can add photos." | hasPhotoInAlbum |
| tip.album.select | Album with >= 4 photos | first photo | "Long-press a photo to select and share several." | - |
| tip.favourites | first album opened with photos | first photo | "Double-tap a photo to add it to Favourites." | - |
| tip.templates.drafts | Templates (with >= 1 draft) | drafts tab | "Invitations you started wait here." | - |

Pacing: at most one new one-off tip per screen visit, and at most 3 per day per device.

## 7. Demo event ("See an example event")

`GET /api/demo/event` returns the same shape as the organiser dashboard response, filled with a fixed sample
wedding ("Aisha & Ibrahim", in 3 weeks): ~24 guests with mixed replies (yes/no/maybe/not opened), 2 celebrants,
one album with ~12 photos, cover photos set. Plus `demo: true`. Photo URLs point to files the backend publishes to
storage at startup (like templates). Both apps open it in their normal dashboard screen in a read-only "Example"
mode: a banner "This is an example event. Nothing here is real." + "Start your own" button; every action button is
disabled or opens "Start your own". No writes ever hit the API from demo mode.

## 8. Empty pages and art

Each empty page: art (top), heading, one line on how the page fills, then 1-3 starter cards or buttons.
Art = custom oxblood illustrations that look like the content that will fill the page (blank versions of the real
tiles/rows/cards), never generic objects. Shipped as SVG, one file per theme: `empty-<key>-light.svg`,
`empty-<key>-dark.svg`, viewBox `0 0 320 200`. Keys:

| key | page(s) | picture | heading / line | starters |
|---|---|---|---|---|
| feed | Feed › Posts | blank feed post card (cover, title lines, hearts) | "Your feed is quiet" / "Events you host or are invited to show up here." | Start an event; See an example event; Browse templates |
| inbox | Inbox | 2-3 blank invitation poster tiles in the inbox grid, one lifted | "No invitations yet" / "Invitations sent to your email or phone appear here." | Add another email or phone (profile); Browse templates |
| hosting | Me › Hosting | blank event tile grid, first tile a dashed "+" | "You're not hosting anything yet" / "Events you start show up here." | Start an event; See an example event (+ checklist above) |
| photos | Dashboard album tab, /buckets/:id, invitation photos | blank photo grid, camera in first square | "No photos yet" / "Photos and videos from the event land here." | Open the camera; Add photos; Show the QR code (organiser) |
| guests | Dashboard › Guests | blank guest rows with reply ticks | "No guests yet" / "Add people and see who's coming." | Add a guest; Upload a list; Share one open link |
| celebrants | Dashboard › Celebrants | two blank person chips with a heart | "Who's it for?" / "Add the people this event is for. They'll see guests and photos." | Add someone |
| drafts | Templates › Drafts | blank half-finished invitation card | "No drafts" / "Invitations you start but don't send wait here." | Start an event |
| favourites | Favourites | blank photo tiles with a heart | "No favourites yet" / "Double-tap a photo to keep it here." | Go to your events |
| requested | Templates › Requested | blank design card with a ribbon | "Nothing made for you yet" / "Designs made for you show up here." | Ask for one |
| designs | Templates › My designs, designer home | blank design canvas with a pen | "No designs yet" / "Templates you publish show up here." | Make your first template |

Also: no "No payments yet" / "No code yet" / "Nobody yet" plain text anywhere; stat cards with all zeros are
hidden until there is data; never show an empty state while loading.

## 9. Settings

Settings › **Tips**: switch "Show tips" (PUT tipsOn) and button "Show tips again" (POST reset). Same on web and app.
