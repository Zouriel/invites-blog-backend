using System.Net;
using System.Text;
using System.Text.Json;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Delivery;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using InvitesBlog.Infrastructure.Delivery;
using InvitesBlog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>
/// Guest invitations go by Viber when the guest has a phone number, by email when they don't or Viber
/// can't reach them, and are "not sent" with neither. Email is the last resort, so OTP codes stay the
/// main thing invites.blog emails.
/// </summary>
public class ViberInvitationTests
{
    private const string Phone = "+9607771234";

    // ---------- the route ----------

    private static Guest G(string? email, string? phone) =>
        new() { Id = Guid.NewGuid(), CampaignId = Guid.NewGuid(), Name = "Amira", Email = email, PhoneE164 = phone };

    private static DeliverySettings Sending => DeliverySettings.Parse("{\"channels\":[\"email\"]}");

    private static IInviteDeliveryProvider[] Both => [new FakeProvider("email", true), new FakeProvider("viber", true)];

    [Fact]
    public void A_guest_with_a_phone_and_an_email_is_tried_on_Viber_first_then_email()
    {
        var route = GuestRoute.For(G("a@x.mv", Phone), Sending, Both);
        Assert.Equal(["viber", "email"], route.Select(r => r.Channel));
        Assert.Equal(Phone, route[0].Address);
    }

    [Fact]
    public void A_guest_with_only_an_email_is_emailed()
    {
        Assert.Equal(["email"], GuestRoute.For(G("a@x.mv", null), Sending, Both).Select(r => r.Channel));
    }

    [Fact]
    public void A_guest_with_only_a_phone_is_sent_Viber()
    {
        Assert.Equal(["viber"], GuestRoute.For(G(null, Phone), Sending, Both).Select(r => r.Channel));
    }

    [Fact]
    public void Without_Viber_set_up_a_phone_only_guest_has_no_route_and_an_emailable_one_is_emailed()
    {
        IInviteDeliveryProvider[] emailOnly = [new FakeProvider("email", true)];
        Assert.Empty(GuestRoute.For(G(null, Phone), Sending, emailOnly));
        Assert.Equal(["email"], GuestRoute.For(G("a@x.mv", Phone), Sending, emailOnly).Select(r => r.Channel));
    }

    [Fact]
    public void Share_only_events_send_nobody_anything()
    {
        Assert.Empty(GuestRoute.For(G("a@x.mv", Phone), DeliverySettings.Parse("{\"channels\":[\"share\"]}"), Both));
    }

    [Fact]
    public void In_test_numbers_mode_only_listed_numbers_go_by_Viber()
    {
        var viber = Viber(new Capture(), onlyTo: "+960 777 1234");
        IInviteDeliveryProvider[] providers = [new FakeProvider("email", true), viber];
        Assert.Equal(["viber", "email"], GuestRoute.For(G("a@x.mv", Phone), Sending, providers).Select(r => r.Channel));
        Assert.Equal(["email"], GuestRoute.For(G("a@x.mv", "+9607779999"), Sending, providers).Select(r => r.Channel));
    }

    // ---------- sending (DispatchService) ----------

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase($"viber-{Guid.NewGuid()}").Options);

    private static IConfiguration Config(Dictionary<string, string?>? extra = null)
    {
        var values = new Dictionary<string, string?> { ["Urls:InviteeBase"] = "https://me.invites.blog" };
        foreach (var (k, v) in extra ?? []) values[k] = v;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static async Task<(AppDbContext Db, Campaign Campaign, Guest Guest)> SeedAsync(string? email, string? phone)
    {
        var db = NewDb();
        var campaign = new Campaign
        {
            Id = Guid.NewGuid(), TemplateId = Guid.NewGuid(), TemplateVersion = "1.0.0", AccessTokenHash = "h",
            Title = "T", Slug = "t", Status = CampaignStatus.Dispatched,
            DeliverySettingsJson = "{\"channels\":[\"email\"]}",
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
        };
        var guest = new Guest
        {
            Id = Guid.NewGuid(), CampaignId = campaign.Id, Name = "Amira", Email = email, PhoneE164 = phone,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        db.Campaigns.Add(campaign);
        db.Guests.Add(guest);
        await db.SaveChangesAsync();
        return (db, campaign, guest);
    }

    private static DispatchService Dispatch(AppDbContext db, int left = 100, params IInviteDeliveryProvider[] providers) =>
        new(db, providers, Config(), NullLogger<DispatchService>.Instance, TestData.Allowance(left: left));

    [Fact]
    public async Task A_guest_with_a_phone_gets_Viber_and_no_email()
    {
        var (db, campaign, _) = await SeedAsync("a@x.mv", Phone);
        var email = new FakeProvider("email", true);
        var viber = new FakeProvider("viber", true);

        await Dispatch(db, 100, email, viber).DispatchCampaignAsync(campaign.Id);

        Assert.Single(viber.Calls);
        Assert.Empty(email.Calls);
        Assert.StartsWith("https://me.invites.blog/i/", viber.Calls[0].InviteLink);
        var invite = await db.Invites.SingleAsync();
        Assert.Equal(InviteStatus.Sent, invite.Status);
        Assert.NotNull(invite.FirstEmailedAt); // counted against the event's sent invitations, like an email
        Assert.Equal("viber", (await db.DeliveryAttempts.SingleAsync()).Channel);
    }

    [Fact]
    public async Task When_Infobip_refuses_the_message_the_guest_is_emailed_instead()
    {
        var (db, campaign, _) = await SeedAsync("a@x.mv", Phone);
        var email = new FakeProvider("email", true);
        var viber = new FakeProvider("viber", false);

        await Dispatch(db, 100, email, viber).DispatchCampaignAsync(campaign.Id);

        Assert.Single(email.Calls);
        var attempts = await db.DeliveryAttempts.OrderBy(a => a.AttemptedAt).ToListAsync();
        Assert.Equal([("viber", DeliveryStatus.Failed), ("email", DeliveryStatus.Sent)],
            attempts.Select(a => (a.Channel, a.Status)));
        Assert.Equal(InviteStatus.Sent, (await db.Invites.SingleAsync()).Status);
    }

    [Fact]
    public async Task A_Viber_send_uses_up_one_of_the_events_sent_invitations()
    {
        var (db, campaign, _) = await SeedAsync(null, Phone);
        var viber = new FakeProvider("viber", true);

        await Dispatch(db, 0, new FakeProvider("email", true), viber).DispatchCampaignAsync(campaign.Id);

        Assert.Empty(viber.Calls);
        var attempt = await db.DeliveryAttempts.SingleAsync();
        Assert.Equal(DeliveryStatus.Skipped, attempt.Status);
        Assert.Equal(DispatchService.OverLimitMessage, attempt.ErrorMessage);
    }

    // ---------- Viber's delivery report ----------

    private static async Task<(AppDbContext Db, Guest Guest, FakeProvider Email, InfobipReportHandler Sut, DeliveryAttempt Viber)>
        SentByViberAsync(string? email = "a@x.mv")
    {
        var (db, campaign, guest) = await SeedAsync(email, Phone);
        var emailP = new FakeProvider("email", true);
        var dispatch = Dispatch(db, 100, emailP, new FakeProvider("viber", true));
        await dispatch.DispatchCampaignAsync(campaign.Id);
        var viber = await db.DeliveryAttempts.SingleAsync();
        return (db, guest, emailP, new InfobipReportHandler(db, dispatch, NullLogger<InfobipReportHandler>.Instance), viber);
    }

    private static string Report(string messageId, string group, string? error = null) =>
        JsonSerializer.Serialize(new
        {
            results = new[]
            {
                new
                {
                    messageId, to = "9607771234",
                    status = new { groupName = group, name = group + "_X" },
                    error = error is null ? null : new { name = error, permanent = true }
                }
            }
        });

    [Fact]
    public async Task A_delivered_report_marks_the_Viber_message_delivered()
    {
        var (db, _, email, sut, viber) = await SentByViberAsync();

        await sut.HandleReportAsync(Report(viber.ProviderMessageId!, "DELIVERED"));

        Assert.Equal(DeliveryStatus.Delivered, (await db.DeliveryAttempts.SingleAsync()).Status);
        Assert.Empty(email.Calls);
    }

    [Fact]
    public async Task A_guest_not_on_Viber_is_emailed_once_even_when_the_report_repeats()
    {
        var (db, _, email, sut, viber) = await SentByViberAsync();

        await sut.HandleReportAsync(Report(viber.ProviderMessageId!, "UNDELIVERABLE", "EC_NOT_VIBER_USER"));
        await sut.HandleReportAsync(Report(viber.ProviderMessageId!, "UNDELIVERABLE", "EC_NOT_VIBER_USER"));

        Assert.Single(email.Calls);
        Assert.Equal("a@x.mv", email.Calls[0].RecipientAddress);
        var failed = await db.DeliveryAttempts.SingleAsync(a => a.Channel == "viber");
        Assert.Equal(DeliveryStatus.Failed, failed.Status);
        Assert.Contains("EC_NOT_VIBER_USER", failed.ErrorMessage);
        Assert.Equal(InviteStatus.Sent, (await db.Invites.SingleAsync()).Status);
    }

    [Fact]
    public async Task A_report_still_on_its_way_changes_nothing()
    {
        var (db, _, email, sut, viber) = await SentByViberAsync();

        await sut.HandleReportAsync(Report(viber.ProviderMessageId!, "PENDING"));

        Assert.Equal(DeliveryStatus.Sent, (await db.DeliveryAttempts.SingleAsync()).Status);
        Assert.Empty(email.Calls);
    }

    [Fact]
    public async Task An_old_Viber_failure_does_not_email_after_a_newer_send_went_out()
    {
        var (db, guest, email, sut, viber) = await SentByViberAsync();
        // The host resent since; the first message's failure arrives late.
        db.DeliveryAttempts.Add(new DeliveryAttempt
        {
            Id = Guid.NewGuid(), InviteId = viber.InviteId, Channel = "viber", RecipientAddress = Phone,
            Status = DeliveryStatus.Sent, ProviderMessageId = "newer", AttemptedAt = viber.AttemptedAt.AddMinutes(5)
        });
        await db.SaveChangesAsync();

        await sut.HandleReportAsync(Report(viber.ProviderMessageId!, "EXPIRED"));

        Assert.Empty(email.Calls);
    }

    [Fact]
    public async Task A_guest_with_no_email_to_fall_back_to_shows_as_failed()
    {
        var (db, _, email, sut, viber) = await SentByViberAsync(email: null);

        await sut.HandleReportAsync(Report(viber.ProviderMessageId!, "UNDELIVERABLE"));

        Assert.Empty(email.Calls);
        Assert.Equal(InviteStatus.Failed, (await db.Invites.SingleAsync()).Status);
    }

    [Fact]
    public async Task Rubbish_and_unknown_reports_are_ignored()
    {
        var (db, _, email, sut, _) = await SentByViberAsync();

        await sut.HandleReportAsync("not json");
        await sut.HandleReportAsync("[]");
        await sut.HandleReportAsync(Report("someone-elses", "UNDELIVERABLE"));

        Assert.Equal(DeliveryStatus.Sent, (await db.DeliveryAttempts.SingleAsync()).Status);
        Assert.Empty(email.Calls);
    }

    // ---------- the Infobip request ----------

    private sealed class Capture : HttpMessageHandler
    {
        public HttpRequestMessage? Request;
        public string? Body;
        public HttpStatusCode Status = HttpStatusCode.OK;
        public string Response =
            "{\"bulkId\":\"b1\",\"messages\":[{\"messageId\":\"vb-1\",\"status\":{\"groupName\":\"PENDING\",\"name\":\"PENDING_ENROUTE\"},\"destination\":\"9607771234\"}]}";

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Request = request;
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(Status) { Content = new StringContent(Response, Encoding.UTF8, "application/json") };
        }
    }

    private static InfobipViberSender Viber(Capture handler, string? onlyTo = null) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("https://abc.api.infobip.com/proxy/") },
            Config(new()
            {
                ["Infobip:ViberSender"] = "invitesblog",
                ["Infobip:WebhookToken"] = "tok&en",
                ["Infobip:OnlyTo"] = onlyTo,
                ["Urls:ApiBase"] = "https://invites.blog",
            }),
            NullLogger<InfobipViberSender>.Instance);

    private static InviteDeliveryMessage Message(string text = "You're warmly invited!") =>
        new("viber", Phone, "Mariyam", "https://me.invites.blog/i/raw", text,
            CampaignId: Guid.NewGuid(), InviteId: Guid.NewGuid(), RemovalLink: "https://me.invites.blog/privacy/remove/raw");

    [Fact]
    public async Task The_request_is_a_text_with_a_button_to_the_guests_link_and_a_delivery_webhook()
    {
        var http = new Capture();
        var m = Message();

        var result = await Viber(http).SendAsync(m, default);

        Assert.True(result.Success);
        Assert.Equal("vb-1", result.ProviderMessageId);
        // The base address keeps its path: the request is relative to it.
        Assert.Equal("https://abc.api.infobip.com/proxy/viber/2/messages", http.Request!.RequestUri!.ToString());
        var msg = JsonDocument.Parse(http.Body!).RootElement.GetProperty("messages")[0];
        Assert.Equal("invitesblog", msg.GetProperty("sender").GetString());
        Assert.Equal("9607771234", msg.GetProperty("destinations")[0].GetProperty("to").GetString());
        var content = msg.GetProperty("content");
        Assert.Equal("TEXT", content.GetProperty("type").GetString());
        Assert.Equal("https://me.invites.blog/i/raw", content.GetProperty("button").GetProperty("action").GetString());
        Assert.True(content.GetProperty("button").GetProperty("title").GetString()!.Length <= 30);
        var text = content.GetProperty("text").GetString()!;
        Assert.StartsWith("You're warmly invited!", text);
        Assert.Contains("on behalf of Mariyam", text);
        Assert.Contains("https://me.invites.blog/privacy/remove/raw", text);
        Assert.Equal("https://invites.blog/api/delivery/infobip/webhook?t=tok%26en",
            msg.GetProperty("webhooks").GetProperty("delivery").GetProperty("url").GetString());
        Assert.Equal(m.InviteId.ToString(), msg.GetProperty("webhooks").GetProperty("callbackData").GetString());
    }

    [Fact]
    public void A_long_message_is_cut_to_Vibers_limit_and_keeps_its_footer()
    {
        var text = InfobipViberSender.Text(Message(new string('x', 3000)));
        Assert.Equal(InfobipViberSender.MaxText, text.Length);
        Assert.EndsWith("https://me.invites.blog/privacy/remove/raw", text);
    }

    [Fact]
    public async Task A_refused_request_fails_so_the_guest_is_emailed()
    {
        var http = new Capture { Status = HttpStatusCode.BadRequest, Response = "{\"requestError\":{}}" };
        Assert.False((await Viber(http).SendAsync(Message(), default)).Success);

        var rejected = new Capture
        {
            Response = "{\"messages\":[{\"messageId\":\"vb-2\",\"status\":{\"groupName\":\"REJECTED\",\"name\":\"REJECTED_DESTINATION_NOT_REGISTERED\"}}]}"
        };
        var result = await Viber(rejected).SendAsync(Message(), default);
        Assert.False(result.Success);
        Assert.Contains("REJECTED_DESTINATION_NOT_REGISTERED", result.Error);
    }
}

/// <summary>A delivery provider test double that records calls and returns a preset result.</summary>
internal sealed class FakeProvider(string channel, bool succeeds) : IInviteDeliveryProvider
{
    public string Channel => channel;
    public List<InviteDeliveryMessage> Calls { get; } = new();

    public Task<DeliveryResult> SendAsync(InviteDeliveryMessage m, CancellationToken ct)
    {
        Calls.Add(m);
        return Task.FromResult(succeeds ? DeliveryResult.Ok($"{channel}-{Guid.NewGuid():N}") : DeliveryResult.Fail("boom"));
    }
}
