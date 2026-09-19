using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Events;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Delivery;

/// <summary>
/// Email delivery channel — MVP priority 1 (§4.8.1). Renders the delivery message as HTML. This is
/// THE invitation email: the first send, resends and "add and send now" all come through here with
/// an <see cref="Application.Delivery.InviteLetter"/>, so the §15.2 footer (with the guest's own
/// "Remove my data" link) is on every one of them.
/// </summary>
public sealed class EmailInviteDeliveryProvider(IEmailSender email) : IInviteDeliveryProvider
{
    public string Channel => "email";

    public async Task<DeliveryResult> SendAsync(InviteDeliveryMessage m, CancellationToken ct)
    {
        var std = m.SaveTheDate;
        var subject = m.Subject ?? (std is not null ? $"Save the date: {std.Title}" : $"You're invited by {m.InviterName}");
        // Every sender now composes an InviteLetter, which always carries the guest's own removal
        // link; the privacy page is only a last resort for a message built some other way.
        var removal = m.RemovalLink ?? "https://invites.blog/privacy";
        var removalHref = System.Net.WebUtility.HtmlEncode(removal);
        var text = System.Net.WebUtility.HtmlEncode(m.MessageText);
        var host = System.Net.WebUtility.HtmlEncode(m.InviterName);
        var link = System.Net.WebUtility.HtmlEncode(m.InviteLink);

        // A branded, email-client-safe card (table layout + inline styles). Generous spacing around
        // the CTA so the button never crowds the message text.
        var sans = "-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif";
        var html =
            $"<div style=\"margin:0;padding:0;background:#14100c;\">" +
              $"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background:#14100c;padding:32px 12px;\"><tr><td align=\"center\">" +
                $"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"max-width:520px;background:#1c1611;border:1px solid #3a2f1e;border-radius:16px;\">" +
                  $"<tr><td style=\"padding:36px 40px 4px;text-align:center;font-family:Georgia,'Times New Roman',serif;font-size:22px;color:#f4efe6;\">" +
                    $"{InvitesBlog.Infrastructure.Email.EmailLayout.BrandMark}invites<span style=\"color:#d8b25a;\">.</span>blog</td></tr>" +
                  (std is not null ? SaveTheDateHeader(std, sans) : "") +
                  $"<tr><td style=\"padding:12px 40px 0;text-align:center;\">" +
                    $"<p style=\"font-family:{sans};font-size:16px;line-height:1.65;color:#e7ddca;margin:14px 0 0;\">{text}</p></td></tr>" +
                  $"<tr><td style=\"padding:30px 40px 34px;text-align:center;\">" +
                    $"<a href=\"{link}\" style=\"display:inline-block;background:#d8b25a;color:#14100c;font-family:{sans};font-size:16px;font-weight:700;text-decoration:none;padding:15px 36px;border-radius:999px;\">{(std is not null ? "See the save the date" : "Open your invitation")}</a>" +
                    (std is not null ? CalendarButtons(std, sans) : "") +
                    $"<p style=\"font-family:{sans};font-size:12px;color:#9c8f78;margin:20px 0 0;line-height:1.6;\">Or open this link:<br><a href=\"{link}\" style=\"color:#b9a88a;word-break:break-all;\">{link}</a></p></td></tr>" +
                  $"<tr><td style=\"padding:18px 40px;border-top:1px solid #3a2f1e;text-align:center;font-family:{sans};font-size:12px;color:#8a7d68;line-height:1.7;\">" +
                    $"Sent via invites.blog on behalf of {host}<br>" +
                    $"<a href=\"https://invites.blog/privacy\" style=\"color:#8a7d68;\">Privacy</a> &middot; <a href=\"{removalHref}\" style=\"color:#8a7d68;\">Remove my data</a></td></tr>" +
                $"</table></td></tr></table></div>"; // §15.2 footer

        var tags = new List<KeyValuePair<string, string>> { new("kind", std is not null ? "save_the_date" : "invite") };
        if (m.CampaignId is { } cid) tags.Add(new("campaign_id", cid.ToString()));
        if (m.InviteId is { } iid) tags.Add(new("invite_id", iid.ToString()));

        // List-Unsubscribe → the guest's own data-removal link (provider guide §2.3).
        var headers = new Dictionary<string, string> { ["List-Unsubscribe"] = $"<{removal}>" };

        return await email.SendAsync(new EmailMessage(
            To: m.RecipientAddress,
            Subject: subject,
            Html: html,
            Stream: EmailStream.Invites,
            ReplyTo: m.InviterEmail,
            Tags: tags,
            Headers: headers,
            // The same entry as a file: Apple Mail and Outlook offer to add it, and it is the only
            // way in for a calendar that isn't Google's or Microsoft's.
            Attachments: std is null ? null :
                [new EmailAttachment("save-the-date.ics", System.Text.Encoding.UTF8.GetBytes(CalendarLinks.Ics(std)), "text/calendar")]), ct);
    }

    /// <summary>The day, big — the one thing a save the date is for — with the time and place when known.</summary>
    private static string SaveTheDateHeader(CalendarEntry e, string sans)
    {
        var day = e.Start.ToOffset(EventDayWindow.Male);
        var when = day.ToString("dddd, d MMMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("en-GB"));
        var time = e.AllDay ? null : day.ToString("h:mm tt", System.Globalization.CultureInfo.InvariantCulture);
        var detail = string.Join(" · ", new[] { time, e.Location }.Where(x => !string.IsNullOrWhiteSpace(x)));
        string H(string? v) => System.Net.WebUtility.HtmlEncode(v ?? "");
        return
            $"<tr><td style=\"padding:22px 40px 0;text-align:center;\">" +
              $"<p style=\"font-family:{sans};font-size:12px;letter-spacing:.22em;text-transform:uppercase;color:#d8b25a;margin:0;\">Save the date</p>" +
              $"<p style=\"font-family:Georgia,'Times New Roman',serif;font-size:26px;line-height:1.25;color:#f4efe6;margin:10px 0 0;\">{H(e.Title)}</p>" +
              $"<p style=\"font-family:{sans};font-size:18px;font-weight:600;color:#f4efe6;margin:14px 0 0;\">{H(when)}</p>" +
              (detail.Length > 0 ? $"<p style=\"font-family:{sans};font-size:14px;color:#b9a88a;margin:6px 0 0;\">{H(detail)}</p>" : "") +
            "</td></tr>";
    }

    /// <summary>One tap into Google or Outlook; Apple Calendar and the rest open the attached file.</summary>
    private static string CalendarButtons(CalendarEntry e, string sans)
    {
        string Button(string href, string label) =>
            $"<a href=\"{System.Net.WebUtility.HtmlEncode(href)}\" style=\"display:inline-block;margin:6px 4px 0;border:1px solid #5a4a2e;color:#f4efe6;font-family:{sans};font-size:14px;font-weight:600;text-decoration:none;padding:10px 16px;border-radius:999px;\">{label}</a>";
        return
            $"<p style=\"font-family:{sans};font-size:13px;color:#b9a88a;margin:26px 0 4px;\">Add it to your calendar</p>" +
            Button(CalendarLinks.Google(e), "Google Calendar") +
            Button(CalendarLinks.Outlook(e), "Outlook") +
            Button(CalendarLinks.Office365(e), "Outlook (work)") +
            $"<p style=\"font-family:{sans};font-size:12px;color:#9c8f78;margin:10px 0 0;\">Apple Calendar or another app: open the attached file.</p>";
    }
}
