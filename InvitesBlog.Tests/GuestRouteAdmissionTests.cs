using System.Text.RegularExpressions;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>
/// Every route under <c>/r/{renderId}</c> must prove admission before it does anything.
///
/// <para>The render id is deliberately not a credential — it appears in the address bar of a
/// document that may run a template's own JavaScript, which is exactly why the actual authority is
/// an HttpOnly cookie the page cannot read. A handler that takes the id at face value would let
/// anyone holding a link read an event's photographs, or delete them.</para>
///
/// <para>The check is one line at the top of each action and nothing structural enforces it, so it
/// is enforced here instead. If admission ever becomes a filter or a base class, this test should be
/// replaced by that — not deleted.</para>
/// </summary>
public class GuestRouteAdmissionTests
{
    private static string ControllerSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(
                dir.FullName, "InvitesBlog.Api", "Controllers", "GuestController.cs");
            if (File.Exists(candidate)) return File.ReadAllText(candidate);
            dir = dir.Parent;
        }

        // Loud rather than skipped: a test that quietly stops checking is worse than no test.
        throw new FileNotFoundException(
            "GuestController.cs not found walking up from " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Every_render_scoped_route_checks_admission_first()
    {
        var src = ControllerSource();

        // Split at each routing attribute and take everything up to the next one. That captures the
        // whole action whichever body style it uses — an earlier version of this test matched only
        // brace bodies, so an expression-bodied handler (exactly the shape a quick addition takes)
        // slipped through it entirely and the test proved nothing.
        var chunks = Regex.Split(src, @"(?=\[Http(?:Get|Post|Put|Delete)\()")
            .Where(c => c.StartsWith("[Http", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(chunks);

        var renderScoped = chunks
            .Select(c => new
            {
                Route = Regex.Match(c, @"\[Http\w+\(""(?<r>/r/\{renderId\}[^""]*)""\)\]").Groups["r"].Value,
                Body = c,
            })
            .Where(x => x.Route.Length > 0)
            .ToList();

        // If this ever finds nothing, the attribute shape changed and the test has stopped looking.
        Assert.True(renderScoped.Count >= 5, $"Only found {renderScoped.Count} render-scoped routes.");

        var unguarded = renderScoped
            .Where(x => !x.Body.Contains("Admitted(renderId)", StringComparison.Ordinal))
            .Select(x => x.Route)
            .ToList();

        Assert.True(
            unguarded.Count == 0,
            "These render-scoped routes do not check admission: " + string.Join(", ", unguarded));
    }
}
