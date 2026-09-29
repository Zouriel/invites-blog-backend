using System.Globalization;
using System.Net;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Plans;

namespace InvitesBlog.Application.Services.Billing;

/// <summary>The emails about a plan renewing by itself: renewed, couldn't renew, stopped.</summary>
public static class RenewalEmails
{
    private const string Sans = "-apple-system,'Segoe UI',Roboto,Arial,sans-serif";

    /// <summary>The saved card was charged: here is the receipt, and when it renews next.</summary>
    public static EmailMessage Renewed(string to, string plan, decimal amount, string currency, DateTimeOffset? until, string link) =>
        Build(to, $"Your {plan} plan has renewed",
            $"We've renewed <strong>{E(plan)}</strong> and charged <strong>{E(currency)} {amount:0.00}</strong> to your saved card." +
            (until is { } u ? $" It now runs until {E(Day(u))} and renews again then." : "") +
            " Please keep this email as your receipt. You can turn off automatic renewal at any time under Billing.",
            "See billing", link);

    /// <summary>A renewal didn't go through; it's tried again tomorrow unless it has failed three times.</summary>
    public static EmailMessage Failed(string to, string plan, bool stopped, string link) =>
        Build(to, stopped ? $"Your {plan} plan could not renew" : $"We couldn't renew your {plan} plan",
            stopped
                ? $"We tried to renew <strong>{E(plan)}</strong> with your saved card and it didn't go through, so automatic renewal is now off. " +
                  "Your plan runs to the end of the period already paid. You can renew it yourself from Billing."
                : $"We tried to renew <strong>{E(plan)}</strong> with your saved card and it didn't go through. We'll try again tomorrow. " +
                  "If your card has changed, renew from Billing to save the new one.",
            "Go to billing", link);

    /// <summary>No card is on file to charge, so the plan can't renew by itself.</summary>
    public static EmailMessage NoCard(string to, string plan, string link) =>
        Build(to, $"Renew your {plan} plan",
            $"<strong>{E(plan)}</strong> is due to renew, but there's no saved card to charge, so it won't renew by itself. " +
            "Renew it from Billing to keep it running.",
            "Renew now", link);

    private static string Day(DateTimeOffset d) =>
        d.ToOffset(TimeSpan.FromHours(5)).ToString("d MMMM yyyy", CultureInfo.GetCultureInfo("en-GB"));

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
