using System.Text.Json.Nodes;
using InvitesBlog.Api.Rendering;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Campaigns;
using InvitesBlog.Application.Dtos.Campaigns;
using InvitesBlog.Application.Rules;
using InvitesBlog.Application.Services.Campaigns;
using InvitesBlog.Application.Services.SaveTheDate;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using InvitesBlog.Infrastructure.Delivery;
using InvitesBlog.Infrastructure.Rendering;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>
/// A save the date asks nothing and collects nothing: no reply, no camera, no photo box, no album.
/// What it offers is the day, for the guest's calendar — on the page, in the email and as an .ics —
/// and later its guests, emailed marks and pass move into the invitation made from it.
/// </summary>
public class SaveTheDateTests
{
    private static readonly TimeSpan Male = TimeSpan.FromHours(5);

    private static Campaign Std(bool allDay = true) => new()
    {
        Id = Guid.NewGuid(),
        Title = "Aisha & Omar",
        Kind = CampaignKind.SaveTheDate,
        AllDay = allDay,
        TemplateVersion = "1.0.0",
        Status = CampaignStatus.Dispatched,
        EventStartAt = new DateTimeOffset(2027, 3, 14, 12, 0, 0, Male),
        CustomContentJson = """{"venue":{"name":"Sun Island","city":"Ari Atoll"}}""",
        RulesJson = "{\"rules\":[]}",
        TemplateManifestJson = "{}",
    };

    private static JsonObject Render(Campaign c, RsvpStatus rsvp = RsvpStatus.NoResponse)
    {
        var sut = new InviteRenderService(new RuleEngine(), new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Camera:IgnoreDateForCampaigns"] = c.Id.ToString() })
            .Build());
        var invite = new Invite { Id = Guid.NewGuid(), RsvpStatus = rsvp };
        return (JsonObject)sut.Build(c, TestData.Template(), new Guest { Id = Guid.NewGuid(), Name = "Nadia" },
            invite, "https://me.invites.blog/r/xyz", "Aisha", null, null).Data;
    }

    [Fact]
    public void The_page_offers_the_calendar_and_nothing_to_answer_or_upload()
    {
        // Even going, on the day (the camera window is waived for this campaign): still no camera.
        var data = Render(Std(), RsvpStatus.Going);

        Assert.Null(data["rsvp"]?["link"]);
        Assert.Null(data["photos"]?["link"]);
        Assert.Null(data["camera"]?["link"]);
        Assert.Equal("saveTheDate", data["invitation"]?["type"]?.ToString());
        Assert.Contains("dates=20270314/20270315", data["calendar"]?["google"]?.ToString());
        Assert.Equal("https://me.invites.blog/r/xyz/calendar.ics", data["calendar"]?["ics"]?.ToString());
        // A day with no time yet shows no time.
        Assert.Null(data["event"]?["time"]);
        Assert.Equal("Sunday, 14 March 2027", data["event"]?["date"]?.ToString());
    }

    [Fact]
    public void An_invitation_still_asks_for_a_reply()
    {
        var c = Std();
        c.Kind = CampaignKind.Invitation;
        var data = Render(c);
        Assert.NotNull(data["rsvp"]?["link"]);
        Assert.Equal("invitation", data["invitation"]?["type"]?.ToString());
    }

    [Fact]
    public void The_calendar_entry_names_the_place_and_is_all_day_without_a_time()
    {
        var entry = EventCalendar.For(Std(), "https://x");
        Assert.True(entry.AllDay);
        Assert.Equal("Sun Island, Ari Atoll", entry.Location);
        Assert.Equal("Aisha & Omar", entry.Title);
        Assert.False(EventCalendar.For(Std(allDay: false), null).AllDay);
    }

    [Fact]
    public void The_Add_to_calendar_bar_is_only_on_a_save_the_date()
    {
        const string page = "<html><body><h1>Hi</h1></body></html>";
        var withBar = GuestCalendarHtml.Inject(page, Render(Std()));
        Assert.Contains("data-ib-calendar", withBar);
        Assert.Contains("Google Calendar", withBar);
        Assert.Contains("calendar.ics", withBar);

        var invitation = Std();
        invitation.Kind = CampaignKind.Invitation;
        Assert.Equal(page, GuestCalendarHtml.Inject(page, Render(invitation)));
    }

    [Fact]
    public async Task The_email_says_save_the_date_and_carries_the_ics()
    {
        var sender = Substitute.For<IEmailSender>();
        EmailMessage? sent = null;
        sender.SendAsync(Arg.Do<EmailMessage>(m => sent = m), Arg.Any<CancellationToken>())
            .Returns(DeliveryResult.Ok("id"));

        var entry = EventCalendar.For(Std(), "https://me.invites.blog/i/tok");
        await new EmailInviteDeliveryProvider(sender).SendAsync(new InviteDeliveryMessage(
            "email", "nadia@example.com", "Aisha", "https://me.invites.blog/i/tok", "Mark your calendar!",
            SaveTheDate: entry), CancellationToken.None);

        Assert.NotNull(sent);
        Assert.Equal("Save the date: Aisha & Omar", sent!.Subject);
        Assert.Contains("Add it to your calendar", sent.Html);
        Assert.Contains("calendar.google.com", sent.Html);
        Assert.Contains("See the save the date", sent.Html);
        Assert.Contains("Sunday, 14 March 2027", sent.Html);
        var ics = Assert.Single(sent.Attachments!);
        Assert.Equal("save-the-date.ics", ics.Filename);
        Assert.Contains("METHOD:PUBLISH", System.Text.Encoding.UTF8.GetString(ics.Content));
    }

    [Fact]
    public void A_save_the_date_resumes_without_a_roles_step()
    {
        var c = Std();
        c.Status = CampaignStatus.Draft;
        c.TemplatePackageUrl = "/assets/x";
        Assert.Equal("guests", CampaignResume.Step(c, isImported: false, guestCount: 0));
        Assert.Equal("inviter", CampaignResume.Step(c, isImported: false, guestCount: 3));
    }

    [Fact]
    public async Task Making_the_invitation_copies_guests_and_marks_and_moves_the_pass()
    {
        var std = Std();
        std.EventPass = EventPassKind.Wedding;
        std.EventPassUntil = DateTimeOffset.UtcNow.AddYears(1);
        std.PaidInviteCapacity = 100;
        var invitation = new Campaign { Id = Guid.NewGuid(), Title = std.Title, Kind = CampaignKind.Invitation, Status = CampaignStatus.Draft };

        var campaignRepo = Substitute.For<ICampaignRepository>();
        campaignRepo.Query(Arg.Any<bool>()).Returns(_ => new[] { std, invitation }.AsAsyncQueryable());
        var campaignService = Substitute.For<ICampaignService>();
        campaignService.CreateBareAsync(std.Title, std.EventStartAt, Arg.Any<CancellationToken>(), CampaignKind.Invitation, true)
            .Returns(new CreateCampaignResponse(invitation.Id, "Draft", "tok"));
        var ownership = Substitute.For<ICampaignOwnershipService>();
        ownership.OwnsAsync(std.Id, Arg.Any<CancellationToken>()).Returns(true);

        var emailed = new Guest { Id = Guid.NewGuid(), CampaignId = std.Id, Name = "Nadia", Email = "n@x.mv", Roles = ["Family"] };
        var notYet = new Guest { Id = Guid.NewGuid(), CampaignId = std.Id, Name = "Hassan", Email = "h@x.mv" };
        var guestRepo = Substitute.For<IGuestRepository>();
        guestRepo.ListByCampaignAsync(std.Id, true, Arg.Any<CancellationToken>()).Returns([emailed, notYet]);
        var added = new List<Guest>();
        await guestRepo.AddAsync(Arg.Do<Guest>(added.Add), Arg.Any<CancellationToken>());

        var inviteRepo = Substitute.For<IInviteRepository>();
        var at = DateTimeOffset.UtcNow.AddDays(-30);
        inviteRepo.ListByCampaignAsync(std.Id, Arg.Any<CancellationToken>()).Returns(
            [new Invite { Id = Guid.NewGuid(), CampaignId = std.Id, GuestId = emailed.Id, FirstEmailedAt = at }]);
        var addedInvites = new List<Invite>();
        await inviteRepo.AddAsync(Arg.Do<Invite>(addedInvites.Add), Arg.Any<CancellationToken>());

        var celebrants = Substitute.For<IRepository<CampaignCelebrant>>();
        celebrants.Query(Arg.Any<bool>()).Returns(Array.Empty<CampaignCelebrant>().AsAsyncQueryable());

        var sut = new SaveTheDateService(campaignService, ownership, campaignRepo, guestRepo, inviteRepo,
            celebrants, TestData.Empty<AuditLog>(), Substitute.For<IUnitOfWork>());
        var made = await sut.MakeInvitationAsync(std.Id);

        Assert.Equal(invitation.Id, made.CampaignId);
        Assert.Equal(2, made.GuestsCopied);
        Assert.Equal(invitation.Id, std.InvitationCampaignId);
        Assert.Equal(new[] { "Nadia", "Hassan" }, added.Select(g => g.Name));
        Assert.All(added, g => Assert.Equal(invitation.Id, g.CampaignId));
        Assert.Equal(["Family"], added[0].Roles);
        // Only Nadia was emailed; her mark comes along so the invitation email doesn't count again.
        var mark = Assert.Single(addedInvites);
        Assert.Equal(added[0].Id, mark.GuestId);
        Assert.Equal(at, mark.FirstEmailedAt);
        // One pool per wedding: the pass and the extra emails move over.
        Assert.Equal(EventPassKind.Wedding, invitation.EventPass);
        Assert.Equal(100, invitation.PaidInviteCapacity);
        Assert.Equal(EventPassKind.None, std.EventPass);
        Assert.Equal(0, std.PaidInviteCapacity);
    }

    [Fact]
    public async Task Asked_twice_it_returns_the_invitation_already_made()
    {
        var std = Std();
        var invitation = new Campaign { Id = Guid.NewGuid(), Status = CampaignStatus.Draft };
        std.InvitationCampaignId = invitation.Id;
        var campaignRepo = Substitute.For<ICampaignRepository>();
        campaignRepo.Query(Arg.Any<bool>()).Returns(_ => new[] { std, invitation }.AsAsyncQueryable());
        var ownership = Substitute.For<ICampaignOwnershipService>();
        ownership.OwnsAsync(std.Id, Arg.Any<CancellationToken>()).Returns(true);
        var campaignService = Substitute.For<ICampaignService>();

        var sut = new SaveTheDateService(campaignService, ownership, campaignRepo, Substitute.For<IGuestRepository>(),
            Substitute.For<IInviteRepository>(), Substitute.For<IRepository<CampaignCelebrant>>(),
            TestData.Empty<AuditLog>(), Substitute.For<IUnitOfWork>());
        var made = await sut.MakeInvitationAsync(std.Id);

        Assert.True(made.AlreadyMade);
        Assert.Equal(invitation.Id, made.CampaignId);
        await campaignService.DidNotReceiveWithAnyArgs().CreateBareAsync(default!);
    }
}
