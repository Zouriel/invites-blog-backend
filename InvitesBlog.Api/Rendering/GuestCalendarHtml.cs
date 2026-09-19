using System.Net;
using System.Text.Json.Nodes;

namespace InvitesBlog.Api.Rendering;

/// <summary>
/// "Add to calendar" on a save the date: a pill at the foot of the page that opens a short list of
/// calendars. Built from the payload's <c>calendar</c> links, so it says what the email says.
///
/// <para>No script: a <c>&lt;details&gt;</c> opens the list. Every choice opens in a new tab — the
/// invitation is sandboxed without downloads, and a popup is allowed to escape the sandbox, which is
/// what lets the .ics download (and Safari hand it to Apple Calendar).</para>
/// </summary>
public static class GuestCalendarHtml
{
    public static bool IsSaveTheDate(JsonObject data) =>
        data["invitation"]?["type"]?.GetValue<string>() == "saveTheDate";

    public static string Inject(string html, JsonObject data)
    {
        if (!IsSaveTheDate(data) || data["calendar"] is not JsonObject cal) return html;
        var google = Link(cal, "google");
        var outlook = Link(cal, "outlook");
        var office = Link(cal, "office365");
        var ics = Link(cal, "ics");
        if (google is null && ics is null) return html;

        const string item = "display:block;padding:12px 16px;color:#1b1b1b;text-decoration:none;font:500 15px/1.2 system-ui,-apple-system,sans-serif;border-top:1px solid rgba(0,0,0,.08)";
        string Row(string? href, string label, bool first = false) => href is null ? "" :
            $"""<a href="{E(href)}" target="_blank" rel="noopener" style="{item}{(first ? ";border-top:0" : "")}">{label}</a>""";

        var bar = $"""
            <div data-ib-calendar style="position:fixed;left:50%;bottom:52px;transform:translateX(-50%);z-index:2147483001;width:max-content;max-width:calc(100vw - 32px)">
            <details style="position:relative">
            <summary style="list-style:none;cursor:pointer;display:flex;align-items:center;gap:8px;padding:12px 20px;border-radius:999px;background:#1b1b1b;color:#fff;font:600 15px/1 system-ui,-apple-system,sans-serif;box-shadow:0 8px 24px rgba(0,0,0,.25)">
            <svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true"><rect x="3" y="4" width="18" height="18" rx="2"/><path d="M16 2v4M8 2v4M3 10h18M12 14v4M10 16h4"/></svg>
            Add to calendar</summary>
            <div style="position:absolute;left:50%;bottom:calc(100% + 8px);transform:translateX(-50%);min-width:240px;background:#fff;border-radius:14px;overflow:hidden;box-shadow:0 12px 32px rgba(0,0,0,.28)">
            {Row(google, "Google Calendar", first: true)}{Row(ics, "Apple Calendar")}{Row(outlook, "Outlook.com")}{Row(office, "Outlook (work or school)")}{Row(ics, "Other (.ics file)")}
            </div>
            </details>
            </div>
            """;

        var at = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        return at < 0 ? html + bar : html.Insert(at, bar);
    }

    private static string? Link(JsonObject cal, string key)
    {
        try { return cal[key]?.GetValue<string>() is { Length: > 0 } s ? s : null; }
        catch (InvalidOperationException) { return null; }
    }

    private static string E(string s) => WebUtility.HtmlEncode(s);
}
