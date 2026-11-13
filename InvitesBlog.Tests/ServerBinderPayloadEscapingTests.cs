using System.Text.Json.Nodes;
using InvitesBlog.TemplateCompiler;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>The bound data travels in a script block; nothing in it (a title, a guest's name) may end that block.</summary>
public sealed class ServerBinderPayloadEscapingTests
{
    [Fact]
    public void A_name_with_a_closing_script_tag_stays_inside_the_payload()
    {
        const string html = "<html><body><h1 data-field=\"title\">x</h1></body></html>";
        var bound = ServerBinder.Bind(html, new JsonObject
        {
            ["title"] = "</script><script>alert(1)</script>",
            ["guestName"] = "<b>Mariyam</b>",
        });

        Assert.DoesNotContain("<script>alert(1)", bound);
        // Exactly as many script elements as with a harmless title: none was opened or closed by the text.
        var plain = ServerBinder.Bind(html, new JsonObject { ["title"] = "Our wedding", ["guestName"] = "Mariyam" });
        Assert.Equal(Count(plain, "<script"), Count(bound, "<script"));
        Assert.Equal(Count(plain, "</script"), Count(bound, "</script"));
        var start = bound.IndexOf("\\u003c/script>", StringComparison.Ordinal);
        Assert.True(start > 0, "the payload keeps the text, escaped");
        // The JSON still reads back as exactly what was bound.
        var json = bound[bound.LastIndexOf('{', start)..(bound.IndexOf("</script>", start, StringComparison.Ordinal))];
        Assert.Equal("</script><script>alert(1)</script>", JsonNode.Parse(json)!["title"]!.GetValue<string>());
    }

    private static int Count(string s, string what)
    {
        var n = 0;
        for (var i = s.IndexOf(what, StringComparison.OrdinalIgnoreCase); i >= 0; i = s.IndexOf(what, i + 1, StringComparison.OrdinalIgnoreCase)) n++;
        return n;
    }
}
