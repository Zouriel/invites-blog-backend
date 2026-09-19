using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Plans;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>
/// The one discount: a design a Studio published FOR this client takes the Studio percentage off a
/// pass, with no code to enter. Nothing else is discounted, and a lapsed Studio's designs aren't.
/// </summary>
public class PassOfferTests
{
    private readonly ICampaignRepository _campaigns = Substitute.For<ICampaignRepository>();
    private readonly ITemplateRepository _templates = Substitute.For<ITemplateRepository>();
    private readonly IPlanService _plans = Substitute.For<IPlanService>();

    private readonly AppUser _host = new() { Id = Guid.NewGuid(), Email = "client@x.mv", DisplayName = "Client" };

    /// <param name="earlier">Other events already made from the same design, before this one.</param>
    private async Task<PassOfferDto> OfferFor(Template template, bool studioActive, AppUser? host = null, int earlier = 0, bool someoneElseFirst = false)
    {
        var campaign = TestData.Campaign();
        campaign.TemplateId = template.Id;
        campaign.CreatedByUserId = (host ?? _host).Id;
        campaign.CreatedAt = DateTimeOffset.UtcNow;
        var rows = new List<Campaign> { campaign };
        for (var i = 0; i < earlier; i++)
        {
            var other = TestData.Campaign();
            other.TemplateId = template.Id;
            other.CreatedByUserId = campaign.CreatedByUserId;
            other.CreatedAt = campaign.CreatedAt.AddDays(-1 - i);
            rows.Add(other);
        }
        if (someoneElseFirst)
        {
            var theirs = TestData.Campaign();
            theirs.TemplateId = template.Id;
            theirs.CreatedByUserId = Guid.NewGuid();
            theirs.CreatedAt = campaign.CreatedAt.AddDays(-3);
            rows.Add(theirs);
        }
        _campaigns.GetByIdAsync(campaign.Id, Arg.Any<CancellationToken>()).Returns(campaign);
        _campaigns.Query(Arg.Any<bool>()).Returns(_ => rows.AsAsyncQueryable());
        _templates.GetByIdAsync(template.Id, Arg.Any<CancellationToken>()).Returns(template);
        var users = Substitute.For<IRepository<AppUser>>();
        users.Query(Arg.Any<bool>()).Returns(_ => new[] { _host, host ?? _host }.Distinct().AsAsyncQueryable());
        if (template.DesignerUserId is { } d) _plans.IsStudioAsync(d, Arg.Any<CancellationToken>()).Returns(studioActive);
        return await new PassOfferService(_campaigns, _templates, users, _plans, TestData.PriceBook()).ForCampaignAsync(campaign.Id);
    }

    private static Template Design(string? assignedTo) => new()
    {
        Id = Guid.NewGuid(), Name = "D", Version = "1", DesignerUserId = Guid.NewGuid(), DesignerName = "Aishath",
        AssignedEmail = assignedTo, Visibility = assignedTo is null ? TemplateVisibility.Public : TemplateVisibility.Dedicated,
    };

    [Fact]
    public async Task A_design_made_for_the_client_by_a_Studio_is_30_percent_off_the_pass()
    {
        var o = await OfferFor(Design("client@x.mv"), studioActive: true);
        Assert.Equal(30, o.DiscountPercent);
        Assert.Equal("Aishath", o.DesignedBy);
        Assert.Equal(139m, o.PartyPass);
        Assert.Equal(489m, o.WeddingPass);
        Assert.Equal(199m, o.FullPartyPass);
        // Extensions are never discounted.
        Assert.Equal(99m, o.PartyExtension);
        Assert.Equal(349m, o.WeddingExtension);
    }

    [Fact]
    public async Task A_public_design_or_a_lapsed_Studio_pays_full_price()
    {
        var gallery = await OfferFor(Design(null), studioActive: true);
        var lapsed = await OfferFor(Design("client@x.mv"), studioActive: false);
        Assert.Equal(0, gallery.DiscountPercent);
        Assert.Equal(199m, gallery.PartyPass);
        Assert.Equal(0, lapsed.DiscountPercent);
        Assert.Equal(699m, lapsed.WeddingPass);
    }

    [Fact]
    public async Task Only_the_person_it_was_made_for_and_only_its_first_event()
    {
        var design = Design("client@x.mv");
        var stranger = new AppUser { Id = Guid.NewGuid(), Email = "someone@else.mv", DisplayName = "S" };

        // Made public later and used by somebody else: full price.
        var other = await OfferFor(design, studioActive: true, host: stranger);
        Assert.Equal(0, other.DiscountPercent);
        Assert.Equal(199m, other.PartyPass);

        // The client again, on a second event from the same design: full price.
        var second = await OfferFor(design, studioActive: true, earlier: 1);
        Assert.Equal(0, second.DiscountPercent);

        // Someone else (a designer trying it, an admin) used it first: the client's first is still theirs.
        Assert.Equal(30, (await OfferFor(design, studioActive: true, someoneElseFirst: true)).DiscountPercent);

        // The client's first event: discounted, whatever the case of their email.
        _host.Email = "Client@X.mv";
        Assert.Equal(30, (await OfferFor(design, studioActive: true)).DiscountPercent);
    }

    [Fact]
    public void Extending_adds_a_year_from_the_end_and_resets_the_reminders()
    {
        var now = DateTimeOffset.UtcNow;
        var c = TestData.Campaign();
        c.EventPass = EventPassKind.Wedding;
        c.EventPassUntil = now.AddDays(10);
        c.PassNoticeStage = 2;

        Assert.True(EventPasses.Extend(c, now));
        Assert.Equal(now.AddDays(10).AddMonths(12), c.EventPassUntil);
        Assert.Equal(0, c.PassNoticeStage);

        var none = TestData.Campaign();
        Assert.False(EventPasses.Extend(none, now));
    }
}
