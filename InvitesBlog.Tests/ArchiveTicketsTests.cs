using InvitesBlog.Api.Photos;
using InvitesBlog.Application.Services.Photos;
using Xunit;

namespace InvitesBlog.Tests;

public sealed class ArchiveTicketsTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now = new(2026, 11, 3, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static readonly PhotoArchive Album = new(Guid.NewGuid(), "album.zip", [Guid.NewGuid()], 10);

    [Fact]
    public void A_link_names_its_archive_until_it_expires()
    {
        var clock = new Clock();
        var tickets = new ArchiveTickets(clock);
        var ticket = tickets.Issue(Album);

        Assert.True(ticket.Length >= 40, "a link must be long enough not to be guessed");
        Assert.DoesNotContain('/', ticket);
        Assert.DoesNotContain('+', ticket);
        Assert.Same(Album, tickets.Find(ticket));

        clock.Now += ArchiveTickets.Lifetime - TimeSpan.FromSeconds(1);
        Assert.Same(Album, tickets.Find(ticket));
        clock.Now += TimeSpan.FromSeconds(2);
        Assert.Null(tickets.Find(ticket));
    }

    [Fact]
    public void Links_are_different_every_time_and_an_unknown_one_finds_nothing()
    {
        var tickets = new ArchiveTickets(new Clock());
        Assert.NotEqual(tickets.Issue(Album), tickets.Issue(Album));
        Assert.Null(tickets.Find("nope"));
        Assert.Null(tickets.Find(""));
    }

    [Fact]
    public void Expired_links_make_room_for_new_ones()
    {
        var clock = new Clock();
        var tickets = new ArchiveTickets(clock);
        for (var i = 0; i < ArchiveTickets.MaxHeld; i++) tickets.Issue(Album);
        Assert.Throws<InvitesBlog.Application.Exceptions.BusinessRuleException>(() => tickets.Issue(Album));
        clock.Now += ArchiveTickets.Lifetime + TimeSpan.FromSeconds(1);
        Assert.NotNull(tickets.Find(tickets.Issue(Album)));
    }
}
