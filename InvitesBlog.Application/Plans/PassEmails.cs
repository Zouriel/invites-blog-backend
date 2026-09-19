using System.Net;
using InvitesBlog.Application.Abstractions;

namespace InvitesBlog.Application.Plans;

/// <summary>The emails about an event's pass: ready to finish and send, and ending soon.</summary>
public static class PassEmails
{
    private const string Sans = "-apple-system,'Segoe UI',Roboto,Arial,sans-serif";

    /// <summary>A pass we added to an event still waiting at its Plan step: come back and send it.</summary>
    public static EmailMessage Ready(string to, string eventTitle, string passName, string link) =>
        Build(to, $"Your {passName} is ready: {eventTitle}",
            $"We've added the <strong>{E(passName)}</strong> to <strong>{E(eventTitle)}</strong>. Finish setting it up and send your invitations.",
            "Finish and send", link);

    /// <summary>A month, then a week, before a pass ends: the photos' space and albums go with it.</summary>
    public static EmailMessage EndingSoon(string to, string eventTitle, string passName, DateTimeOffset endsAt, decimal extendPrice, string link)
    {
        var on = endsAt.ToOffset(TimeSpan.FromHours(5)).ToString("d MMMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("en-GB"));
        return Build(to, $"Your {passName} for {eventTitle} ends on {on}",
            $"The <strong>{E(passName)}</strong> on <strong>{E(eventTitle)}</strong> ends on {E(on)}. After that its extra space and albums go, " +
            $"and the photos start to wind down. Keep it another year for {PlanCatalog.Currency} {extendPrice:0} (no invitations included).",
            "Extend it", link);
    }

    private static EmailMessage Build(string to, string subject, string bodyHtml, string cta, string link)
    {
        var href = E(link);
        var html =
            $"<div style=\"font-family:{Sans};max-width:520px;margin:0 auto;padding:24px;color:#152026\">" +
            "<p style=\"font-size:16px;line-height:1.6\">Hello,</p>" +
            $"<p style=\"font-size:16px;line-height:1.6\">{bodyHtml}</p>" +
            $"<p style=\"text-align:center;margin:28px 0\"><a href=\"{href}\" style=\"display:inline-block;background:#1b3d59;color:#fff;text-decoration:none;padding:14px 30px;border-radius:999px;font-weight:600\">{E(cta)}</a></p>" +
            $"<p style=\"font-size:12px;color:#5a6b75;line-height:1.6\">Or paste this into your browser:<br><a href=\"{href}\" style=\"color:#1b3d59\">{href}</a><br>Sent via invites.blog</p></div>";
        return new EmailMessage(To: to, Subject: subject, Html: html, Stream: EmailStream.System);
    }

    private static string E(string s) => WebUtility.HtmlEncode(s);
}
