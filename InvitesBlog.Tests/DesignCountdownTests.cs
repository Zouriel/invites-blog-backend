using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using InvitesBlog.TemplateCompiler;
using InvitesBlog.TemplateCompiler.Design;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>
/// The countdown element: the page carries one span per unit and no date of its own; the runtime fills
/// it from the event's start (<c>event.startsAt</c>), and a page with no start hides it.
/// </summary>
public class DesignCountdownTests
{
    private static DesignScene Scene(params DesignElement[] elements)
    {
        var scene = DesignStarters.Create("blank")!;
        scene.Elements.Clear();
        scene.Elements.AddRange(elements);
        return scene;
    }

    private static DesignElement Countdown(string id, DesignCountdown? cd = null) => new()
    {
        Id = id, Type = "countdown", X = 30, Y = 100, W = 330, H = 96,
        Countdown = cd ?? new DesignCountdown { Style = new DesignTypography { Size = 40 } },
    };

    private static string Units(string html) =>
        string.Join(",", Regex.Matches(html, "data-u=\"([dhms])\"").Select(m => m.Groups[1].Value));

    [Fact]
    public void Counts_down_to_the_unit_asked_for_with_days_first_and_carries_no_date()
    {
        Assert.Equal("d,h,m,s", Units(DesignCompiler.Compile(Scene(Countdown("a")))));
        Assert.Equal("d", Units(DesignCompiler.Compile(Scene(Countdown("a", new DesignCountdown { Units = "days" })))));
        Assert.Equal("d,h", Units(DesignCompiler.Compile(Scene(Countdown("a", new DesignCountdown { Units = "hours" })))));
        var html = DesignCompiler.Compile(Scene(Countdown("a", new DesignCountdown { Units = "minutes" })));
        Assert.Equal("d,h,m", Units(html));
        Assert.Contains("data-countdown=\"m\"", html);
        // Not data-optional: nothing inside it is a field, so the empty-field rule would hide it on every page.
        Assert.DoesNotMatch("<div class=\"e e0\"[^>]* data-optional", html);
        Assert.DoesNotContain("2027", html);
    }

    [Fact]
    public void Says_its_own_words_on_the_day_as_text_never_markup()
    {
        var html = DesignCompiler.Compile(Scene(Countdown("a", new DesignCountdown { Done = "<b>Today</b> & \"now\"" })));
        Assert.Contains("<span class=\"cx\">&lt;b&gt;Today&lt;/b&gt; &amp; &quot;now&quot;</span>", html);
        Assert.Contains(".cd[data-done] .cx{display:block}", html);
        // Left out of the JSON: the default wording.
        var plain = DesignScene.Parse("""{"schema":3,"canvas":{},"elements":[{"id":"a","type":"countdown","countdown":{}}]}""");
        Assert.Contains("<span class=\"cx\">Today&#39;s the day</span>", DesignCompiler.Compile(plain));
    }

    [Fact]
    public void Tiles_get_the_box_and_a_line_wraps_as_text()
    {
        var tiles = DesignCompiler.Compile(Scene(Countdown("a", new DesignCountdown { Fill = "theme:accent", Stroke = "#000000", StrokeWidth = 2, Radius = 8, Gap = 6, Style = new DesignTypography { Size = 40 } })));
        Assert.Contains("justify-content:center;gap:", tiles);
        Assert.Matches(@"\.e0 \.cu\{flex:1 1 0;[^}]*background:var\(--ib-accent\);border:[^;]+ solid #000000;border-radius:", tiles);
        // The words under the numbers are a third of their size.
        Assert.Matches(@"\.e0 \.cu i\{font-style:inherit;font-size:calc\(12 \*", tiles);

        var line = DesignCompiler.Compile(Scene(Countdown("a", new DesignCountdown { Look = "line", Style = new DesignTypography { Align = "left" } })));
        Assert.Contains("flex-wrap:wrap;align-items:center;column-gap:0.4em;justify-content:flex-start;", line);
        Assert.DoesNotContain(".e0 .cu{flex:1 1 0", line);
    }

    [Fact]
    public void Check_names_units_and_looks_it_does_not_know()
    {
        var bad = Countdown("a", new DesignCountdown { Units = "weeks", Look = "boxes", Fill = "nope" });
        var issues = DesignValidator.Validate(Scene(bad));
        Assert.Equal(2, issues.Count(i => i.Code == "countdown" && i.Severity == "error"));
        Assert.Contains(issues, i => i.Code == "color" && i.ElementId == "a");
        Assert.DoesNotContain(DesignValidator.Validate(Scene(Countdown("b"))), i => i.Code is "countdown" or "color" or "element_type");
    }

    [Fact]
    public void Binding_keeps_it_for_an_event_with_a_start_and_hides_it_without_one()
    {
        var html = DesignCompiler.Compile(Scene(Countdown("a")));
        var withStart = ServerBinder.Bind(html, new JsonObject { ["event"] = new JsonObject { ["startsAt"] = "2027-08-28T13:30:00Z" } });
        Assert.Matches("<div class=\"cd\" data-countdown=\"s\">", withStart);
        // Shown: nothing on its way up the page hides it.
        Assert.DoesNotContain("display:none", withStart[withStart.IndexOf("<div class=\"e e0\"", StringComparison.Ordinal)..withStart.IndexOf("data-countdown", StringComparison.Ordinal)]);
        var without = ServerBinder.Bind(html, new JsonObject { ["event"] = new JsonObject() });
        Assert.Contains("<div class=\"cd\" data-countdown=\"s\" style=\"display:none\">", without);
    }

    [Fact]
    public void The_runtime_counts_from_the_events_start_and_the_preview_has_one()
    {
        Assert.Contains("querySelectorAll('[data-countdown]')", TemplateRuntime.Js);
        Assert.Contains("Date.parse(ev.startsAt)", TemplateRuntime.Js);
        var sample = DesignSampleData.Build(Scene(Countdown("a")), DesignSampleData.Empty);
        Assert.Equal(DesignSampleData.SampleStartsAt, sample["event"]!["startsAt"]!.ToString());
    }
}
