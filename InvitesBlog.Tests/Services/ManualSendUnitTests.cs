using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Delivery;
using InvitesBlog.Application.Phones;
using InvitesBlog.Application.Security;
using InvitesBlog.Domain.Entities;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>The share link a host sends by hand, the message that goes with it, and phone regions.</summary>
public class ManualSendUnitTests
{
    // ----- Share links -------------------------------------------------------------------------------

    [Fact]
    public void A_share_link_is_the_same_every_time_and_reads_back_to_its_invite()
    {
        var links = TestData.ShareLinks();
        var invite = Guid.NewGuid();
        var token = links.TokenFor(invite);
        Assert.Equal(token, links.TokenFor(invite));
        Assert.Equal(InviteShareLinks.Length, token.Length);
        Assert.StartsWith("w", token);
        Assert.Matches("^[A-Za-z0-9_-]+$", token);
        Assert.Equal(invite, links.Read(token));
        Assert.NotEqual(token, links.TokenFor(Guid.NewGuid()));
    }

    [Fact]
    public void An_altered_or_foreign_share_link_reads_as_nothing()
    {
        var links = TestData.ShareLinks();
        var invite = Guid.NewGuid();
        var token = links.TokenFor(invite);

        // Every single-character change, in the id half and the signature half.
        for (var i = 1; i < token.Length; i++)
        {
            var altered = token[..i] + (token[i] == 'A' ? 'B' : 'A') + token[(i + 1)..];
            Assert.Null(links.Read(altered));
        }
        // Someone else's invite id with this signature.
        var other = links.TokenFor(Guid.NewGuid());
        Assert.Null(links.Read(other[..23] + token[23..]));
        // Made with another key.
        Assert.Null(links.Read(TestData.ShareLinks("another key").TokenFor(invite)));
        // Not a share link at all: an emailed token (43 chars), junk, wrong prefix, padding.
        Assert.Null(links.Read(TokenService.GenerateToken()));
        Assert.Null(links.Read("x" + token[1..]));
        Assert.Null(links.Read(token + "="));
        Assert.Null(links.Read(""));
        Assert.Null(links.Read(null));
    }

    [Fact]
    public void Without_its_own_key_the_share_link_key_is_derived_from_the_jwt_key_not_equal_to_it()
    {
        static InviteShareLinks With(Dictionary<string, string?> values) =>
            new(Microsoft.Extensions.Configuration.MemoryConfigurationBuilderExtensions
                .AddInMemoryCollection(new Microsoft.Extensions.Configuration.ConfigurationBuilder(), values).Build());
        var invite = Guid.NewGuid();
        var derivedA = With(new() { ["Jwt:SigningKey"] = "jwt-key-a-long-enough-0123456789" }).TokenFor(invite);
        var derivedB = With(new() { ["Jwt:SigningKey"] = "jwt-key-b-long-enough-0123456789" }).TokenFor(invite);
        var direct = With(new() { ["Invites:ShareLinkKey"] = "jwt-key-a-long-enough-0123456789" }).TokenFor(invite);
        Assert.NotEqual(derivedA, derivedB);
        Assert.NotEqual(derivedA, direct);   // the JWT key is never used as the share key itself
    }

    [Fact]
    public async Task The_emailed_token_is_looked_up_first_then_the_share_link()
    {
        var links = TestData.ShareLinks();
        var invites = Substitute.For<IInviteRepository>();
        var emailedInvite = new Invite { Id = Guid.NewGuid() };
        var sharedInvite = new Invite { Id = Guid.NewGuid() };
        var emailed = TokenService.GenerateToken();
        invites.GetByTokenHashAsync(TokenService.Hash(emailed), Arg.Any<CancellationToken>()).Returns(emailedInvite);
        invites.GetByIdAsync(sharedInvite.Id, Arg.Any<CancellationToken>()).Returns(sharedInvite);

        Assert.Same(emailedInvite, await invites.FindByTokenAsync(links, emailed));
        Assert.Same(sharedInvite, await invites.FindByTokenAsync(links, links.TokenFor(sharedInvite.Id)));
        Assert.Null(await invites.FindByTokenAsync(links, TestData.ShareLinks("x").TokenFor(sharedInvite.Id)));
        await invites.DidNotReceive().GetByIdAsync(emailedInvite.Id, Arg.Any<CancellationToken>());
    }

    // ----- The message ---------------------------------------------------------------------------------

    [Theory]
    [InlineData("You're invited 🎉", "You're invited 🎉\n\nhttps://me.invites.blog/i/")]
    [InlineData("ދަޢުވަތު {{name}} & {{inviter.name}} #2027", "ދަޢުވަތު Aisha & Ali & Hawwa #2027\n\nhttps://me.invites.blog/i/")]
    [InlineData("دعوة {{guest.name}}: {{invite.link}} ✨", "دعوة Aisha: https://me.invites.blog/i/")]
    public void The_message_is_the_hosts_words_raw_with_the_link_once(string template, string expectedStart)
    {
        var settings = DeliverySettings.Parse(System.Text.Json.JsonSerializer.Serialize(new { channels = new[] { "share" }, messageTemplate = template }));
        var links = TestData.ShareLinks();
        var inviteId = Guid.NewGuid();
        var (link, text) = ManualSends.Compose(new Guest { Name = " Aisha " }, inviteId, settings, "https://me.invites.blog", links, "Ali & Hawwa");

        Assert.Equal($"https://me.invites.blog/i/{links.TokenFor(inviteId)}", link);
        Assert.StartsWith(expectedStart, text);
        Assert.Equal(1, CountOf(text, link));
        // Nothing encoded or escaped on the server: the client URL-encodes for wa.me / t.me / viber, and
        // that round-trips every character, the emoji, Thaana, Arabic, & and # included.
        Assert.DoesNotContain("%", text);
        Assert.DoesNotContain("&amp;", text);
        Assert.Equal(text, Uri.UnescapeDataString(Uri.EscapeDataString(text)));
        Assert.DoesNotContain("&", Uri.EscapeDataString(text));
        Assert.DoesNotContain("#", Uri.EscapeDataString(text));
    }

    private static int CountOf(string text, string part)
    {
        var n = 0;
        for (var i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    [Theory]
    [InlineData("whatsapp", "whatsapp")]
    [InlineData(" WhatsApp ", "whatsapp")]
    [InlineData("VIBER", "viber")]
    [InlineData("telegram", "telegram")]
    [InlineData("sms", null)]
    [InlineData("email", null)]
    [InlineData(null, null)]
    public void Only_the_three_apps_can_be_sent_from_by_hand(string? asked, string? app) => Assert.Equal(app, ManualSends.ParseApp(asked));

    [Fact]
    public void Hand_send_channels_are_told_apart_from_automatic_ones()
    {
        Assert.Equal("whatsapp-manual", ManualSends.ChannelFor("whatsapp"));
        Assert.Equal("telegram", ManualSends.AppOf("telegram-manual"));
        Assert.True(ManualSends.IsManual("viber-manual"));
        Assert.False(ManualSends.IsManual("viber"));   // the old automatic sender's rows
        Assert.Null(ManualSends.AppOf("email"));
    }

    // ----- Phone regions -------------------------------------------------------------------------------

    [Fact]
    public void A_number_without_a_code_and_no_country_is_refused_not_read_as_maldivian()
    {
        var r = new PhoneNormalizer().Normalize("7771234", null);
        Assert.Equal(PhoneNormalizationOutcome.Impossible, r.Outcome);
        Assert.Equal(PhoneNormalizer.NeedsCountryCode, r.Warning);
    }

    [Theory]
    [InlineData("+44 7700 900123", null, "+447700900123")]
    [InlineData("0044 7700 900123", null, "+447700900123")]
    [InlineData("00 960 777 1234", null, "+9607771234")]
    [InlineData("07700 900123", "GB", "+447700900123")]
    [InlineData("07700 900123", "gb", "+447700900123")]
    [InlineData("(201) 555-0123", "US", "+12015550123")]
    [InlineData("777 1234", "MV", "+9607771234")]
    [InlineData("+1 201 555 0123", "GB", "+12015550123")]   // a code wins over the region
    public void Numbers_are_read_in_the_region_given_or_by_their_own_code(string raw, string? region, string e164) =>
        Assert.Equal(e164, new PhoneNormalizer().Normalize(raw, region).E164);

    [Fact]
    public async Task The_region_is_the_one_picked_else_the_accounts_else_the_requests()
    {
        var source = Substitute.For<IPhoneRegionSource>();
        source.DefaultRegionAsync(Arg.Any<CancellationToken>()).Returns("GB");
        var n = new PhoneNormalizer(source);

        Assert.Equal("+447700900123", (await n.NormalizeAsync("07700 900123", null)).E164);
        Assert.Equal("+12015550123", (await n.NormalizeAsync("(201) 555-0123", "US")).E164);
        // A number with its own code never asks.
        source.ClearReceivedCalls();
        Assert.Equal("+9607771234", (await n.NormalizeAsync("+960 777 1234", null)).E164);
        await source.DidNotReceive().DefaultRegionAsync(Arg.Any<CancellationToken>());

        source.DefaultRegionAsync(Arg.Any<CancellationToken>()).Returns((string?)null);
        Assert.Equal(PhoneNormalizer.NeedsCountryCode, (await n.NormalizeAsync("07700 900123", null)).Warning);
    }

    [Fact]
    public async Task The_account_country_beats_the_requests_country()
    {
        var me = new AppUser { Id = Guid.NewGuid(), DisplayName = "Me", Country = "US" };
        var users = Substitute.For<IRepository<AppUser>>();
        users.Query(Arg.Any<bool>()).Returns(_ => new[] { me }.AsAsyncQueryable());
        var current = Substitute.For<ICurrentUser>();
        current.UserId.Returns(me.Id);
        current.RequestCountry.Returns("GB");

        Assert.Equal("US", await new PhoneRegionSource(current, users).DefaultRegionAsync());
        me.Country = null;
        Assert.Equal("GB", await new PhoneRegionSource(current, users).DefaultRegionAsync());
        current.RequestCountry.Returns("XX");
        Assert.Null(await new PhoneRegionSource(current, users).DefaultRegionAsync());
    }

    [Theory]
    [InlineData("GB", "GB")]
    [InlineData(" us ", "US")]
    [InlineData("XX", null)]
    [InlineData("T1", null)]
    [InlineData("ZZ", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void The_cloudflare_country_header_is_read_carefully(string? header, string? country) =>
        Assert.Equal(country, RequestCountry.Read(header));
}
