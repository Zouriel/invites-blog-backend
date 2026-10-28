using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Campaigns;
using InvitesBlog.Application.Dtos.Campaigns;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Application.Exceptions.Campaigns;
using InvitesBlog.Application.Services.Campaigns;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

public class GuestColoursServiceTests
{
    private readonly ICampaignOwnershipService _ownership = Substitute.For<ICampaignOwnershipService>();
    private readonly ICampaignRepository _campaigns = Substitute.For<ICampaignRepository>();
    private readonly ITemplateRepository _templates = Substitute.For<ITemplateRepository>();
    private readonly IStorageService _storage = Substitute.For<IStorageService>();
    private readonly IImageColourReader _reader = Substitute.For<IImageColourReader>();
    private readonly IUnitOfWork _uow = Substitute.For<IUnitOfWork>();

    private static readonly ColourSetDto Pink = new("#d4627f", "#fdf1f4", "#4a2a33");

    private GuestColoursService Sut() => new(_ownership, _campaigns, _templates, _storage, _reader, _uow);

    private Campaign Event(string visibility, string theme = "{}")
    {
        var c = TestData.Campaign();
        c.ThemeOverridesJson = theme;
        _ownership.OwnsAsync(c.Id, Arg.Any<CancellationToken>()).Returns(true);
        _campaigns.GetByIdAsync(c.Id, Arg.Any<CancellationToken>()).Returns(c);
        _templates.GetByIdAsync(c.TemplateId, Arg.Any<CancellationToken>())
            .Returns(new Template { Id = c.TemplateId, Visibility = visibility, PreviewImageUrl = "/assets/designs/x/design.png" });
        return c;
    }

    [Fact]
    public async Task An_uploaded_design_saves_both_sets()
    {
        var c = Event(TemplateVisibility.Imported);

        var res = await Sut().SetAsync(c.Id, new SetGuestColoursRequest(Pink, Pink));

        Assert.Equal(Pink, GuestColours.ReadReply(c.ThemeOverridesJson));
        Assert.Equal(Pink, GuestColours.ReadCard(c.CardThemeJson));
        Assert.Equal(Pink, res.Reply);
        await _uow.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// On a template the same three keys are the host's Theme-step choices. Saving the Colours page
    /// must neither overwrite them nor (with reply null) clear them.
    /// </summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_template_events_theme_is_never_touched(bool sendReply)
    {
        const string theme = """{"accentColor":"#123456","backgroundColor":"#ffffff","textColor":"#000000"}""";
        var c = Event(TemplateVisibility.Public, theme);

        var res = await Sut().SetAsync(c.Id, new SetGuestColoursRequest(sendReply ? Pink : null, Pink));

        Assert.Equal(theme, c.ThemeOverridesJson);
        Assert.Equal(Pink, GuestColours.ReadCard(c.CardThemeJson));
        Assert.Null(res.Reply);
        Assert.False(res.IsImported);
    }

    [Fact]
    public async Task A_colour_that_is_not_hex_is_refused_and_nothing_saved()
    {
        var c = Event(TemplateVisibility.Imported);
        await Assert.ThrowsAsync<BusinessRuleException>(() =>
            Sut().SetAsync(c.Id, new SetGuestColoursRequest(Pink with { Accent = "#fff;}x{" }, null)));
        await _uow.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Someone_elses_event_is_refused()
    {
        var c = Event(TemplateVisibility.Imported);
        _ownership.OwnsAsync(c.Id, Arg.Any<CancellationToken>()).Returns(false);
        await Assert.ThrowsAsync<CampaignAccessDeniedException>(() => Sut().GetAsync(c.Id));
        await Assert.ThrowsAsync<CampaignAccessDeniedException>(() => Sut().SetAsync(c.Id, new SetGuestColoursRequest(null, Pink)));
    }

    [Fact]
    public async Task Suggestions_come_from_the_uploaded_picture()
    {
        var c = Event(TemplateVisibility.Imported);
        _storage.KeyFor("/assets/designs/x/design.png").Returns("designs/x/design.png");
        _storage.GetAsync("designs/x/design.png", Arg.Any<CancellationToken>()).Returns(new byte[] { 1, 2, 3 });
        _reader.Read(Arg.Any<byte[]>()).Returns(new ImageColours("#f9e4ea", [("#f9e4ea", 0.7), ("#d4627f", 0.3)]));

        var res = await Sut().GetAsync(c.Id);

        Assert.NotEmpty(res.Suggested);
        Assert.Equal("/assets/designs/x/design.png", res.DesignImageUrl);
    }

    [Fact]
    public async Task A_video_design_gets_no_suggestions_and_reads_nothing()
    {
        var c = Event(TemplateVisibility.Imported);
        _templates.GetByIdAsync(c.TemplateId, Arg.Any<CancellationToken>())
            .Returns(new Template { Id = c.TemplateId, Visibility = TemplateVisibility.Imported, PreviewImageUrl = "/assets/designs/x/design.mp4" });

        var res = await Sut().GetAsync(c.Id);

        Assert.Empty(res.Suggested);
        await _storage.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
