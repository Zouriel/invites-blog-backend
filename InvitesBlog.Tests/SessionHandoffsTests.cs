using InvitesBlog.Api.Controllers;
using InvitesBlog.Application.Dtos.Accounts;
using Xunit;

namespace InvitesBlog.Tests;

/// <summary>The one-time codes that carry a sign-in from invites.blog to app.invites.blog.</summary>
public sealed class SessionHandoffsTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 13, 10, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static AuthResultDto Session(string token = "t") => new(token, DateTimeOffset.UtcNow.AddDays(14), null!);

    [Fact]
    public void A_code_gives_its_session_once()
    {
        var store = new SessionHandoffs(new Clock());
        var session = Session();
        var code = store.Add(session)!;

        Assert.Same(session, store.Take(code));
        Assert.Null(store.Take(code));
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void A_code_works_for_sixty_seconds_and_not_after()
    {
        var clock = new Clock();
        var store = new SessionHandoffs(clock);
        var onTime = store.Add(Session("a"))!;
        var late = store.Add(Session("b"))!;

        clock.Now = clock.Now.AddSeconds(59);
        Assert.NotNull(store.Take(onTime));
        clock.Now = clock.Now.AddSeconds(2);   // 61 s after issue
        Assert.Null(store.Take(late));
        Assert.Equal(TimeSpan.FromSeconds(60), SessionHandoffs.Lifetime);
    }

    [Fact]
    public void Unknown_empty_and_oversized_codes_give_nothing()
    {
        var store = new SessionHandoffs(new Clock());
        store.Add(Session());

        Assert.Null(store.Take(null));
        Assert.Null(store.Take(""));
        Assert.Null(store.Take("not-a-code"));
        Assert.Null(store.Take(new string('a', 500)));
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void Codes_are_random_and_expired_ones_are_swept()
    {
        var clock = new Clock();
        var store = new SessionHandoffs(clock);
        var a = store.Add(Session())!;
        var b = store.Add(Session())!;
        Assert.NotEqual(a, b);
        Assert.Matches("^[A-Za-z0-9_-]{43}$", a);

        clock.Now = clock.Now.AddMinutes(5);
        store.Add(Session());
        Assert.Equal(1, store.Count);
    }
}
