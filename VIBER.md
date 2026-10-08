# Viber guest invitations (Infobip)

When a host chooses to have invites.blog send each guest their own link, each guest is sent it:

1. **by Viber** if they have a phone number and Viber is configured,
2. **by email** if they have no phone number, Infobip refuses the message, or Viber later reports
   them unreachable (not on Viber, blocked, expired), and they have an email address,
3. otherwise **not sent**, and the dashboard says so.

Host notices and OTP codes stay on email. The route is `Application/Delivery/GuestRoute.cs`, shared by
the first send (`CampaignService.FinalizeAsync`) and resends / "add and send now" (`DispatchService`).
A guest counts once against the event's sent invitations, whichever channel reached them.

## Settings (server `.env` only, never git)

| Variable | What |
|---|---|
| `INFOBIP_BASE_URL` | The account's API host, `https://xxxxx.api.infobip.com` |
| `INFOBIP_API_KEY` | API key with the Viber scope. Empty = no Viber, every guest is emailed |
| `INFOBIP_VIBER_SENDER` | The approved Viber sender name |
| `INFOBIP_WEBHOOK_TOKEN` | Long random secret; delivery reports must carry it |
| `INFOBIP_ONLY_TO` | Comma-separated numbers that get Viber; empty = everyone. For testing in production |
| `INFOBIP_VIBER_LABEL` | `TRANSACTIONAL` / `PROMOTIONAL`; empty = Infobip's default for the sender |

## Delivery reports

Every message is sent with its own report URL, `{Urls:ApiBase}/api/delivery/infobip/webhook?t={token}`,
so nothing needs setting in the Infobip portal. `InfobipReportHandler` marks the attempt Delivered or
Failed; on UNDELIVERABLE / EXPIRED / REJECTED it emails the guest once, unless a newer send already
went out.

## Going live

1. Set the variables with `INFOBIP_ONLY_TO` = the tester's number, and redeploy the API.
2. Send an event to a guest list holding that number (Viber), and an email-only guest (email).
3. Check the dashboard shows "via Viber", and the API log for `Infobip` warnings.
4. Empty `INFOBIP_ONLY_TO` and recreate the API container.
