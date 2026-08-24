namespace InvitesBlog.Infrastructure.Email;

/// <summary>
/// The shared branded shell for transactional email — the dark-gold card the invite email already
/// used (see <c>EmailInviteDeliveryProvider</c>), extracted so every stream looks like the same
/// product. Table layout + inline styles only: Gmail/Outlook strip &lt;style&gt; blocks and most
/// modern CSS, so anything structural has to be an attribute or an inline style.
/// </summary>
public static class EmailLayout
{
    public const string Sans = "-apple-system,BlinkMacSystemFont,'Segoe UI',Roboto,Helvetica,Arial,sans-serif";
    public const string Serif = "Georgia,'Times New Roman',serif";

    // Palette — kept in one place so the streams can't drift apart again.
    private const string PageBg = "#14100c";
    private const string CardBg = "#1c1611";
    private const string Border = "#3a2f1e";
    private const string Gold = "#d8b25a";
    private const string TextBright = "#f4efe6";
    private const string TextMuted = "#8a7d68";

    /// <summary>
    /// Wraps <paramref name="bodyHtml"/> in the branded card.
    /// </summary>
    /// <param name="preheader">
    /// The grey snippet inboxes show next to the subject. Hidden in the body itself — if it isn't
    /// set, clients fall back to scraping the first visible text, which reads badly.
    /// </param>
    /// <param name="footerHtml">Optional extra footer line above the standard privacy link.</param>
    public static string Wrap(string bodyHtml, string preheader, string? footerHtml = null)
    {
        var footer = footerHtml is { Length: > 0 } ? $"{footerHtml}<br>" : "";
        return
            $"<div style=\"margin:0;padding:0;background:{PageBg};\">" +
              // Preheader: pulled out of view but still read by the inbox preview.
              $"<div style=\"display:none;max-height:0;overflow:hidden;opacity:0;\">{preheader}</div>" +
              $"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"background:{PageBg};padding:32px 12px;\"><tr><td align=\"center\">" +
                $"<table role=\"presentation\" width=\"100%\" cellpadding=\"0\" cellspacing=\"0\" style=\"max-width:520px;background:{CardBg};border:1px solid {Border};border-radius:16px;\">" +
                  $"<tr><td style=\"padding:36px 40px 4px;text-align:center;font-family:{Serif};font-size:22px;color:{TextBright};\">" +
                    $"<span style=\"color:{Gold};\">&#10022;</span> invites<span style=\"color:{Gold};\">.</span>blog</td></tr>" +
                  $"<tr><td style=\"padding:0 40px;\">{bodyHtml}</td></tr>" +
                  $"<tr><td style=\"padding:18px 40px;border-top:1px solid {Border};text-align:center;font-family:{Sans};font-size:12px;color:{TextMuted};line-height:1.7;\">" +
                    $"{footer}<a href=\"https://invites.blog/privacy\" style=\"color:{TextMuted};\">Privacy</a></td></tr>" +
                $"</table></td></tr></table></div>";
    }

    /// <summary>A centred heading for the top of a card body.</summary>
    public static string Heading(string text) =>
        $"<p style=\"font-family:{Serif};font-size:19px;color:{TextBright};margin:22px 0 0;text-align:center;\">{text}</p>";

    /// <summary>Body copy.</summary>
    public static string Paragraph(string html, bool muted = false) =>
        $"<p style=\"font-family:{Sans};font-size:{(muted ? "13px" : "15px")};line-height:1.65;" +
        $"color:{(muted ? TextMuted : "#e7ddca")};margin:14px 0 0;text-align:center;\">{html}</p>";

    /// <summary>
    /// The one-time code, shown large and monospaced in a bordered slab so it is easy to read off a
    /// phone and easy to select for copy/paste.
    /// </summary>
    public static string CodeBlock(string code) =>
        $"<div style=\"margin:26px 0 4px;text-align:center;\">" +
          $"<span style=\"display:inline-block;background:{PageBg};border:1px solid {Border};border-radius:12px;" +
          $"padding:16px 26px;font-family:'SFMono-Regular',Consolas,'Liberation Mono',Menlo,monospace;" +
          $"font-size:30px;font-weight:700;letter-spacing:9px;text-indent:9px;color:{Gold};\">{code}</span></div>";

    /// <summary>Bottom padding for the last element in a card body.</summary>
    public static string EndSpacer() => "<div style=\"height:30px;line-height:30px;\">&nbsp;</div>";
}
