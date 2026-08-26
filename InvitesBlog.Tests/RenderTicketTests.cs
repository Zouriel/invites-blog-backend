using InvitesBlog.Api.Rendering;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>
/// The cookie and the URL of the server-rendered guest path. These are the only thing standing
/// between "holds a valid ticket" and "can read someone's invitation", so the failure cases matter
/// more than the happy one.
/// </summary>
public class RenderTicketTests
{
    private static RenderTickets Sut(string key = "a-long-enough-test-signing-key-0123456789") =>
        new(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Jwt:SigningKey"] = key })
            .Build());

    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void A_ticket_reads_back_as_the_invitation_it_was_issued_for()
    {
        var invite = Guid.NewGuid();
        var sut = Sut();

        Assert.Equal(invite, sut.ReadTicket(sut.IssueTicket(invite, Now), Now));
    }

    [Fact]
    public void A_render_id_is_stable_so_refresh_and_back_still_work()
    {
        var invite = Guid.NewGuid();
        var sut = Sut();

        Assert.Equal(sut.RenderId(invite), sut.RenderId(invite));
    }

    [Fact]
    public void Different_invitations_get_different_render_ids()
    {
        var sut = Sut();
        Assert.NotEqual(sut.RenderId(Guid.NewGuid()), sut.RenderId(Guid.NewGuid()));
    }

    [Fact]
    public void A_render_id_does_not_leak_the_invite_id_it_was_derived_from()
    {
        // It appears in the address bar of a document that may run a designer's JavaScript, so it has
        // to be opaque rather than merely inconvenient to read.
        var invite = Guid.NewGuid();
        var renderId = Sut().RenderId(invite);

        Assert.DoesNotContain(invite.ToString("N"), renderId, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(invite.ToString(), renderId, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_expired_ticket_is_refused()
    {
        var sut = Sut();
        var ticket = sut.IssueTicket(Guid.NewGuid(), Now);

        Assert.Null(sut.ReadTicket(ticket, Now.Add(RenderTickets.TicketLifetime).AddMinutes(1)));
    }

    [Fact]
    public void A_ticket_signed_with_another_key_is_refused()
    {
        var ticket = Sut("one-signing-key-that-is-long-enough-xxxxx").IssueTicket(Guid.NewGuid(), Now);

        Assert.Null(Sut("a-different-signing-key-also-long-enough").ReadTicket(ticket, Now));
    }

    [Fact]
    public void Editing_the_invite_id_inside_a_ticket_invalidates_it()
    {
        // The whole point: the payload is readable, so it must not be *changeable*.
        var sut = Sut();
        var ticket = sut.IssueTicket(Guid.NewGuid(), Now);
        var parts = ticket.Split('.');
        var forged = $"{Guid.NewGuid():N}.{parts[1]}.{parts[2]}";

        Assert.Null(sut.ReadTicket(forged, Now));
    }

    [Fact]
    public void Extending_the_expiry_inside_a_ticket_invalidates_it()
    {
        var sut = Sut();
        var parts = sut.IssueTicket(Guid.NewGuid(), Now).Split('.');
        var forged = $"{parts[0]}.{long.Parse(parts[1]) + 86_400}.{parts[2]}";

        Assert.Null(sut.ReadTicket(forged, Now));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("nonsense")]
    [InlineData("a.b")]
    [InlineData("a.b.c.d")]
    [InlineData("not-a-guid.123.abc")]
    [InlineData("00000000000000000000000000000000.notanumber.abc")]
    [InlineData("00000000000000000000000000000000.123.!!!not-base64!!!")]
    public void Rubbish_in_the_cookie_is_refused_rather_than_thrown_on(string? ticket)
    {
        // This parses a cookie. Cookies arrive from anywhere, including from people trying things.
        Assert.Null(Sut().ReadTicket(ticket, Now));
    }
}
