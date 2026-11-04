using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InvitesBlog.Application.Security;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InvitesBlog.Tests.Integration;

[CollectionDefinition("manual-send")]
public sealed class ManualSendCollection : ICollectionFixture<PostgresApiFixture>;

/// <summary>
/// Sending invitations by hand on WhatsApp, Viber and Telegram, the share link that goes with them,
/// phone numbers read in the person's country, and event time zones: through the real API on real
/// Postgres, with its migrations.
/// </summary>
[Collection("manual-send")]
public sealed class ManualSendTests(PostgresApiFixture api)
{
    private sealed record Person(Guid Id, string Email, string Token);

    // Emoji, Dhivehi (Thaana), Arabic, an ampersand and a hash: the server returns them as they are.
    private const string Template = "Hi {{name}} 🎉 ދަޢުވަތު دعوة & #wedding from {{inviter.name}}";

    // ----- The hand send itself -------------------------------------------------------------------------

    [PostgresFact]
    public async Task A_whatsapp_send_returns_the_link_and_message_records_it_and_keeps_the_emailed_link_working()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host, inviterName: "Aisha & Ali");
        var guest = await GuestAsync(ev, "Mariyam", "mariyam@example.com", "+9607771234");
        var emailedToken = TokenService.GenerateToken();
        var invite = await InviteAsync(ev, guest, InviteStatus.Sent, emailedToken, emailed: true);

        var res = await api.Client(host.Token).PostAsJsonAsync(
            $"/api/campaigns/{ev}/guests/{guest}/manual-send", new { channel = "WhatsApp" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var data = await DataAsync(res);
        var link = data.GetProperty("link").GetString()!;
        var shareToken = link[(link.LastIndexOf('/') + 1)..];
        Assert.Equal($"http://localhost/i/{shareToken}", link);
        Assert.Equal(InviteShareLinks.Length, shareToken.Length);
        // The host's words exactly (no {{invite.link}} in them, so the link follows on its own line).
        Assert.Equal($"Hi Mariyam 🎉 ދަޢުވަތު دعوة & #wedding from Aisha & Ali\n\n{link}", data.GetProperty("text").GetString());
        Assert.True(data.GetProperty("sentAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-1));

        var attempt = await api.ReadAsync(db => db.DeliveryAttempts.SingleAsync(a => a.InviteId == invite && a.Channel == "whatsapp-manual"));
        Assert.Equal((DeliveryStatus.Sent, "+9607771234"), (attempt.Status, attempt.RecipientAddress));
        Assert.Equal(TimeSpan.Zero, attempt.AttemptedAt.Offset);
        var stored = await api.ReadAsync(db => db.Invites.SingleAsync(i => i.Id == invite));
        Assert.Equal(TokenService.Hash(emailedToken), stored.TokenHash);   // the emailed token was not replaced

        // Sending again gives the same link: it is made from the invite, not minted.
        var again = await DataAsync(await api.Client(host.Token).PostAsJsonAsync(
            $"/api/campaigns/{ev}/guests/{guest}/manual-send", new { channel = "viber" }));
        Assert.Equal(link, again.GetProperty("link").GetString());

        // Both links open the same invitation (one client, so its address is trusted after the first open).
        var guestPhone = api.Client();
        Assert.Equal(HttpStatusCode.OK, (await guestPhone.PostAsJsonAsync($"/api/invites/by-token/{shareToken}/rsvp", new { status = "Going" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await guestPhone.PostAsJsonAsync($"/api/invites/by-token/{emailedToken}/rsvp", new { status = "Maybe" })).StatusCode);
        Assert.Equal(RsvpStatus.Maybe, (await api.ReadAsync(db => db.Invites.SingleAsync(i => i.Id == invite))).RsvpStatus);

        // An altered share link, or one made with another key, opens nothing.
        var tampered = shareToken[..^1] + (shareToken[^1] == 'A' ? 'B' : 'A');
        Assert.Equal(HttpStatusCode.NotFound, (await api.Client().GetAsync($"/api/invites/by-token/{tampered}")).StatusCode);
        var foreign = Tests.Services.TestData.ShareLinks("some other key").TokenFor(invite);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Client().GetAsync($"/api/invites/by-token/{foreign}")).StatusCode);

        // The removal page takes the share link too.
        Assert.Equal(HttpStatusCode.OK, (await api.Client().GetAsync($"/api/privacy/remove/{shareToken}")).StatusCode);

        // Hand sends aren't counted in the three-a-day resend limit.
        for (var i = 0; i < 3; i++)
            await api.Client(host.Token).PostAsJsonAsync($"/api/campaigns/{ev}/guests/{guest}/manual-send", new { channel = "telegram" });
        Assert.Equal(HttpStatusCode.OK, (await api.Client(host.Token).PostAsync($"/api/campaigns/{ev}/guests/{guest}/resend", null)).StatusCode);
        // …and the resend minted a new emailed token without changing the share link.
        var afterResend = await DataAsync(await api.Client(host.Token).PostAsJsonAsync(
            $"/api/campaigns/{ev}/guests/{guest}/manual-send", new { channel = "whatsapp" }));
        Assert.Equal(link, afterResend.GetProperty("link").GetString());
        Assert.Equal(HttpStatusCode.OK, (await guestPhone.GetAsync($"/api/invites/by-token/{shareToken}")).StatusCode);
    }

    [PostgresFact]
    public async Task The_dashboard_shows_the_email_tick_each_apps_tick_and_the_prepared_message()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host, inviterName: "Aisha", zone: "Europe/London");
        var emailed = await GuestAsync(ev, "Mariyam", "mariyam@example.com", "+9607771234");
        var emailedInvite = await InviteAsync(ev, emailed, InviteStatus.Sent, TokenService.GenerateToken(), emailed: true);
        await AttemptAsync(emailedInvite, "email", "mariyam@example.com", DeliveryStatus.Delivered, DateTimeOffset.UtcNow.AddHours(-2));
        var phoneOnly = await GuestAsync(ev, "Hassan", null, "+447700900123");
        var noInvite = await GuestAsync(ev, "Not yet", null, "+12015550123");
        // An old event's row from the automatic Viber sender that was removed: still readable.
        var old = await GuestAsync(ev, "Old", null, "+9607770000");
        var oldInvite = await InviteAsync(ev, old, InviteStatus.Failed, TokenService.GenerateToken());
        await AttemptAsync(oldInvite, "viber", "+9607770000", DeliveryStatus.Failed, DateTimeOffset.UtcNow.AddDays(-30), "Viber rejected it");

        await api.Client(host.Token).PostAsJsonAsync($"/api/campaigns/{ev}/guests/{emailed}/manual-send", new { channel = "whatsapp" });
        var made = await DataAsync(await api.Client(host.Token).PostAsJsonAsync($"/api/campaigns/{ev}/guests/{phoneOnly}/manual-send", new { channel = "telegram" }));

        var dash = await DataAsync(await api.Client(host.Token).GetAsync($"/api/me/campaigns/{ev}/dashboard"));
        Assert.Equal("Europe/London", dash.GetProperty("campaign").GetProperty("timeZone").GetString());
        var rows = dash.GetProperty("guests").EnumerateArray().ToDictionary(g => g.GetProperty("name").GetString()!);

        var m = rows["Mariyam"];
        Assert.Equal(("Delivered", "email"), (m.GetProperty("emailStatus").GetString(), m.GetProperty("deliveryChannel").GetString()));
        Assert.Equal(JsonValueKind.String, m.GetProperty("emailAt").ValueKind);
        Assert.Equal(new[] { "whatsapp" }, m.GetProperty("manualSends").EnumerateObject().Select(p => p.Name).ToArray());
        Assert.StartsWith("http://localhost/i/w", m.GetProperty("shareLink").GetString());
        Assert.Equal($"Hi Mariyam 🎉 ދަޢުވަތު دعوة & #wedding from Aisha\n\n{m.GetProperty("shareLink").GetString()}", m.GetProperty("shareText").GetString());

        var h = rows["Hassan"];
        Assert.Equal(JsonValueKind.Null, h.GetProperty("emailStatus").ValueKind);
        Assert.Equal(JsonValueKind.Null, h.GetProperty("deliveryChannel").ValueKind);   // a hand send is not "the delivery channel"
        Assert.Equal(new[] { "telegram" }, h.GetProperty("manualSends").EnumerateObject().Select(p => p.Name).ToArray());
        Assert.Equal("Sent", h.GetProperty("inviteStatus").GetString());
        Assert.Equal(made.GetProperty("link").GetString(), h.GetProperty("shareLink").GetString());

        var n = rows["Not yet"];
        Assert.Equal(JsonValueKind.Null, n.GetProperty("shareLink").ValueKind);
        Assert.Empty(n.GetProperty("manualSends").EnumerateObject());

        var o = rows["Old"];
        Assert.Equal(("viber", "Failed"), (o.GetProperty("deliveryChannel").GetString(), o.GetProperty("inviteStatus").GetString()));

        // Mariyam (emailed) and Hassan (by hand) are sent; the old Viber failure isn't.
        var report = dash.GetProperty("report");
        Assert.Equal((2, 1), (report.GetProperty("sent").GetInt32(), report.GetProperty("failed").GetInt32()));

        // A read-only celebrant sees none of it.
        var bride = await UserAsync();
        await api.WriteAsync(async db =>
        {
            db.CampaignCelebrants.Add(new CampaignCelebrant { Id = Guid.NewGuid(), CampaignId = ev, Name = "Bride", Email = bride.Email, CreatedAt = DateTimeOffset.UtcNow });
            await Task.CompletedTask;
        });
        var seen = await DataAsync(await api.Client(bride.Token).GetAsync($"/api/me/campaigns/{ev}/dashboard"));
        foreach (var g in seen.GetProperty("guests").EnumerateArray())
        {
            Assert.Equal(JsonValueKind.Null, g.GetProperty("shareLink").ValueKind);
            Assert.Equal(JsonValueKind.Null, g.GetProperty("manualSends").ValueKind);
            Assert.Equal(JsonValueKind.Null, g.GetProperty("emailStatus").ValueKind);
        }
    }

    [PostgresFact]
    public async Task A_hand_send_is_refused_without_a_phone_a_known_app_or_the_event()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var mailOnly = await GuestAsync(ev, "Mail only", "m@example.com", null);
        var withPhone = await GuestAsync(ev, "Phone", null, "+9607771234");

        var noPhone = await api.Client(host.Token).PostAsJsonAsync($"/api/campaigns/{ev}/guests/{mailOnly}/manual-send", new { channel = "whatsapp" });
        Assert.Equal(HttpStatusCode.BadRequest, noPhone.StatusCode);
        Assert.Contains("no phone number", await noPhone.Content.ReadAsStringAsync());

        var sms = await api.Client(host.Token).PostAsJsonAsync($"/api/campaigns/{ev}/guests/{withPhone}/manual-send", new { channel = "sms" });
        Assert.Equal(HttpStatusCode.BadRequest, sms.StatusCode);

        var stranger = await UserAsync();
        var refused = await api.Client(stranger.Token).PostAsJsonAsync($"/api/campaigns/{ev}/guests/{withPhone}/manual-send", new { channel = "whatsapp" });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.False(await api.ReadAsync(db => db.Invites.AnyAsync(i => i.GuestId == withPhone)));

        // A hand send doesn't use the event's emailed-invitation allowance.
        var before = await AllowanceUsedAsync(host, ev);
        Assert.Equal(HttpStatusCode.OK, (await api.Client(host.Token).PostAsJsonAsync($"/api/campaigns/{ev}/guests/{withPhone}/manual-send", new { channel = "whatsapp" })).StatusCode);
        Assert.Equal(before, await AllowanceUsedAsync(host, ev));
        Assert.Null((await api.ReadAsync(db => db.Invites.SingleAsync(i => i.GuestId == withPhone))).FirstEmailedAt);
    }

    [PostgresFact]
    public async Task A_guest_sent_to_by_hand_still_gets_their_email_when_the_event_emails()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host, settings: """{"channels":["share","email"]}""", status: CampaignStatus.Dispatched);
        var guest = await GuestAsync(ev, "Both", $"both-{Guid.NewGuid():N}@example.com", "+9607771234");
        await api.Client(host.Token).PostAsJsonAsync($"/api/campaigns/{ev}/guests/{guest}/manual-send", new { channel = "whatsapp" });

        // The per-guest send path (resend / add-and-send) and the event's dispatch both still email them.
        using var scope = api.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<InvitesBlog.Infrastructure.Delivery.DispatchService>().DispatchCampaignAsync(ev);
        var email = await api.ReadAsync(db => db.DeliveryAttempts
            .Where(a => a.Channel == "email" && db.Invites.Any(i => i.Id == a.InviteId && i.GuestId == guest)).ToListAsync());
        Assert.Single(email);
    }

    // ----- Phones and countries ---------------------------------------------------------------------

    [PostgresFact]
    public async Task Phone_numbers_are_read_in_the_picked_country_else_the_accounts_else_the_requests_and_never_maldives_by_default()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);

        async Task<HttpResponseMessage> AddAsync(object body, string? cfCountry = null)
        {
            var client = api.Client(host.Token);
            if (cfCountry is not null) client.DefaultRequestHeaders.Add("CF-IPCountry", cfCountry);
            return await client.PostAsJsonAsync($"/api/campaigns/{ev}/guests", body);
        }
        async Task<string?> PhoneOfAsync(string name) =>
            await api.ReadAsync(db => db.Guests.Where(g => g.CampaignId == ev && g.Name == name).Select(g => g.PhoneE164).SingleAsync());

        // No country anywhere: a number without its code is refused, not guessed.
        var bare = await AddAsync(new { name = "Bare", phone = "7771234" });
        Assert.Equal(HttpStatusCode.BadRequest, bare.StatusCode);
        Assert.Contains("Add the country code", await bare.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await AddAsync(new { name = "Unknown", phone = "7771234" }, "XX")).StatusCode);

        // With + or 00 the number carries its own country.
        Assert.Equal(HttpStatusCode.OK, (await AddAsync(new { name = "Plus", phone = "+44 7700 900123" })).StatusCode);
        Assert.Equal("+447700900123", await PhoneOfAsync("Plus"));
        Assert.Equal(HttpStatusCode.OK, (await AddAsync(new { name = "Zeros", phone = "00 960 777 1235" })).StatusCode);
        Assert.Equal("+9607771235", await PhoneOfAsync("Zeros"));

        // The request's country (Cloudflare), then the one picked beside the number, which wins.
        Assert.Equal(HttpStatusCode.OK, (await AddAsync(new { name = "London", phone = "07700 900124" }, "GB")).StatusCode);
        Assert.Equal("+447700900124", await PhoneOfAsync("London"));
        Assert.Equal(HttpStatusCode.OK, (await AddAsync(new { name = "Picked", phone = "(201) 555-0123", phoneCountry = "US" }, "GB")).StatusCode);
        Assert.Equal("+12015550123", await PhoneOfAsync("Picked"));
        Assert.Equal(HttpStatusCode.OK, (await AddAsync(new { name = "Male", phone = "777 1236", defaultCountry = "MV" })).StatusCode);
        Assert.Equal("+9607771236", await PhoneOfAsync("Male"));

        // The account's saved country beats the request's.
        await api.WriteAsync(async db => (await db.Users.SingleAsync(u => u.Id == host.Id)).Country = "US");
        Assert.Equal(HttpStatusCode.OK, (await AddAsync(new { name = "Saved", phone = "201-555-0124" }, "GB")).StatusCode);
        Assert.Equal("+12015550124", await PhoneOfAsync("Saved"));
    }

    [PostgresFact]
    public async Task Signing_in_keeps_the_first_country_seen_and_the_account_can_change_country_and_time_zone()
    {
        var mail = $"p-{Guid.NewGuid():N}@example.com";
        var id = Guid.NewGuid();
        await api.WriteAsync(async db =>
        {
            var role = await db.Roles.Where(r => r.Name == Roles.Customer).Select(r => r.Id).SingleAsync();
            var user = new AppUser { Id = id, Email = mail, DisplayName = "Pass", PasswordHash = PasswordHasher.Hash("correct horse battery"), IsActive = true, CreatedAt = DateTimeOffset.UtcNow };
            user.UserRoles.Add(new UserRole { RoleId = role });
            db.Users.Add(user);
        });

        async Task<JsonElement> LoginAsync(string country)
        {
            var client = api.Client();
            client.DefaultRequestHeaders.Add("CF-IPCountry", country);
            return await DataAsync(await client.PostAsJsonAsync("/api/auth/login", new { email = mail, password = "correct horse battery" }));
        }

        var first = await LoginAsync("XX");   // unknown: nothing saved
        Assert.Equal(JsonValueKind.Null, first.GetProperty("account").GetProperty("country").ValueKind);
        var gb = await LoginAsync("GB");
        Assert.Equal("GB", gb.GetProperty("account").GetProperty("country").GetString());
        var us = await LoginAsync("US");     // already known: kept
        Assert.Equal("GB", us.GetProperty("account").GetProperty("country").GetString());
        var token = us.GetProperty("token").GetString()!;

        var me = await DataAsync(await api.Client(token).GetAsync("/api/auth/me"));
        Assert.Equal(("GB", JsonValueKind.Null), (me.GetProperty("country").GetString(), me.GetProperty("timeZone").ValueKind));

        var set = await DataAsync(await api.Client(token).PutAsJsonAsync("/api/auth/me/settings", new { country = "us", timeZone = "America/New_York" }));
        Assert.Equal(("US", "America/New_York"), (set.GetProperty("country").GetString(), set.GetProperty("timeZone").GetString()));
        foreach (var bad in new object[] { new { timeZone = "GMT Standard Time" }, new { timeZone = "Mars/Olympus" }, new { country = "ZZ" } })
            Assert.Equal(HttpStatusCode.BadRequest, (await api.Client(token).PutAsJsonAsync("/api/auth/me/settings", bad)).StatusCode);
        var cleared = await DataAsync(await api.Client(token).PutAsJsonAsync("/api/auth/me/settings", new { country = "" }));
        Assert.Equal(("America/New_York", JsonValueKind.Null), (cleared.GetProperty("timeZone").GetString(), cleared.GetProperty("country").ValueKind));
    }

    [PostgresTheory]
    [InlineData("GB", "GB")]
    [InlineData("us", "US")]
    [InlineData("XX", null)]
    [InlineData("T1", null)]
    [InlineData(null, null)]
    public async Task The_geo_endpoint_reports_the_requests_country(string? header, string? expected)
    {
        var client = api.Client();
        if (header is not null) client.DefaultRequestHeaders.Add("CF-IPCountry", header);
        var geo = await DataAsync(await client.GetAsync("/api/geo"));
        Assert.Equal(expected, geo.GetProperty("country").GetString());
    }

    // ----- Event time zones -------------------------------------------------------------------------

    [PostgresFact]
    public async Task A_new_event_takes_the_zone_sent_else_the_hosts_else_maldives()
    {
        var host = await UserAsync();
        var when = DateTimeOffset.UtcNow.AddDays(20);

        async Task<string> ZoneOfNewAsync(string? token, object body)
        {
            var res = await api.Client(token).PostAsJsonAsync("/api/campaigns/bare", body);
            Assert.Equal(HttpStatusCode.Created, res.StatusCode);
            var id = (await DataAsync(res)).GetProperty("campaignId").GetGuid();
            return await api.ReadAsync(db => db.Campaigns.Where(c => c.Id == id).Select(c => c.TimeZone).SingleAsync());
        }

        Assert.Equal("Europe/London", await ZoneOfNewAsync(host.Token, new { title = "London", eventDate = when, timeZone = "Europe/London" }));
        Assert.Equal("Indian/Maldives", await ZoneOfNewAsync(host.Token, new { title = "No zone", eventDate = when }));
        await api.WriteAsync(async db => (await db.Users.SingleAsync(u => u.Id == host.Id)).TimeZone = "America/New_York");
        Assert.Equal("America/New_York", await ZoneOfNewAsync(host.Token, new { title = "Host's", eventDate = when }));
        Assert.Equal("Indian/Maldives", await ZoneOfNewAsync(null, new { title = "Anonymous", eventDate = when }));

        var bad = await api.Client(host.Token).PostAsJsonAsync("/api/campaigns/bare", new { title = "Bad", eventDate = when, timeZone = "Nowhere/Land" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    [PostgresFact]
    public async Task Existing_events_are_maldives_and_the_zone_can_be_changed_with_the_date()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        Assert.Equal("Indian/Maldives", await api.ReadAsync(db => db.Campaigns.Where(c => c.Id == ev).Select(c => c.TimeZone).SingleAsync()));

        // 7 pm in London on a winter evening, sent with the London offset; stored as the UTC instant.
        var start = new DateTimeOffset(2027, 1, 16, 19, 0, 0, TimeSpan.Zero);
        var res = await api.Client(host.Token).PutAsJsonAsync($"/api/campaigns/{ev}/content",
            new { eventStartAt = start.ToOffset(TimeSpan.FromHours(0)), timeZone = "Europe/London" });
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        var row = await api.ReadAsync(db => db.Campaigns.SingleAsync(c => c.Id == ev));
        Assert.Equal(("Europe/London", start), (row.TimeZone, row.EventStartAt));

        var bad = await api.Client(host.Token).PutAsJsonAsync($"/api/campaigns/{ev}/content", new { timeZone = "GMT Standard Time" });
        Assert.Equal(HttpStatusCode.BadRequest, bad.StatusCode);
    }

    // ----- The legacy phone backfill ------------------------------------------------------------------

    [PostgresFact]
    public async Task The_phone_backfill_adds_960_only_to_legacy_maldives_shaped_numbers_and_can_run_twice()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var tag = Guid.NewGuid().ToString("N")[..8];
        var rows = new Dictionary<string, (string Before, string? After)>
        {
            [$"seven-{tag}"] = ("7771234", "+9607771234"),
            [$"spaced-{tag}"] = ("960 777 1235", "+9607771235"),
            [$"e164-{tag}"] = ("+447700900123", "+447700900123"),
            [$"british-{tag}"] = ("07700900123", "07700900123"),   // not a Maldives shape: left alone
        };
        foreach (var (name, (before, _)) in rows) await GuestAsync(ev, name, null, before);
        // A user whose legacy number would collide with an account already holding it is left alone.
        var taken = $"+960{Random.Shared.Next(7000000, 7999999)}";
        await api.WriteAsync(async db =>
        {
            db.Users.Add(new AppUser { Id = Guid.NewGuid(), DisplayName = "Has it", PhoneE164 = taken, IsActive = true, CreatedAt = DateTimeOffset.UtcNow });
            db.Users.Add(new AppUser { Id = Guid.NewGuid(), DisplayName = $"Legacy {tag}", PhoneE164 = taken[4..], IsActive = true, CreatedAt = DateTimeOffset.UtcNow });
            await Task.CompletedTask;
        });

        for (var run = 0; run < 2; run++)
        {
            var sql = new InvitesBlog.Infrastructure.Migrations.BackfillLegacyPhoneCountryCode().UpOperations.OfType<SqlOperation>().ToList();
            Assert.NotEmpty(sql);
            await api.WriteAsync(async db =>
            {
                // Raw SQL here is a format string to EF; the migration's regex quantifiers ({7}) are not placeholders.
                foreach (var op in sql) await db.Database.ExecuteSqlRawAsync(op.Sql.Replace("{", "{{").Replace("}", "}}"));
            });
            foreach (var (name, (_, after)) in rows)
                Assert.Equal(after, await api.ReadAsync(db => db.Guests.Where(g => g.Name == name).Select(g => g.PhoneE164).SingleAsync()));
            Assert.Equal(taken[4..], await api.ReadAsync(db => db.Users.Where(u => u.DisplayName == $"Legacy {tag}").Select(u => u.PhoneE164).SingleAsync()));
        }
    }

    // ----- Helpers ------------------------------------------------------------------------------------

    private static async Task<JsonElement> DataAsync(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("data").Clone();
    }

    private async Task<int> AllowanceUsedAsync(Person host, Guid ev) =>
        (await DataAsync(await api.Client(host.Token).GetAsync($"/api/me/campaigns/{ev}/dashboard")))
            .GetProperty("sending").GetProperty("used").GetInt32();

    private async Task<Person> UserAsync()
    {
        var id = Guid.NewGuid();
        var mail = $"p-{id:N}@example.com";
        await api.WriteAsync(async db =>
        {
            var role = await db.Roles.Where(r => r.Name == Roles.Customer).Select(r => r.Id).SingleAsync();
            var user = new AppUser { Id = id, Email = mail, DisplayName = "Host", IsActive = true, CreatedAt = DateTimeOffset.UtcNow };
            user.UserRoles.Add(new UserRole { RoleId = role });
            db.Users.Add(user);
        });
        return new Person(id, mail, api.TokenFor(id, "email", mail));
    }

    private async Task<Guid> EventAsync(
        Person creator, string? inviterName = null, string zone = "Indian/Maldives",
        string? settings = null, CampaignStatus status = CampaignStatus.Dispatched)
    {
        var id = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        await api.WriteAsync(async db =>
        {
            Guid? inviterId = null;
            if (inviterName is not null)
            {
                inviterId = Guid.NewGuid();
                db.Inviters.Add(new Inviter { Id = inviterId.Value, Name = inviterName, Email = creator.Email, PhoneE164 = "", CreatedAt = DateTimeOffset.UtcNow });
            }
            db.Templates.Add(new Template
            {
                Id = templateId, Name = "T", Slug = $"own-{id:N}", Version = "1.0.0", Category = "Imported", Description = "D", PreviewImageUrl = "",
                SceneJson = "{}", ManifestJson = "{}", PackageUrl = "", Visibility = TemplateVisibility.Imported,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.Campaigns.Add(new Campaign
            {
                Id = id, TemplateId = templateId, TemplateVersion = "1.0.0", TemplatePackageUrl = "",
                InviterId = inviterId, CreatedByUserId = creator.Id, AccessTokenHash = Guid.NewGuid().ToString("N"),
                Title = "An event", Slug = $"e-{id:N}", Status = status, TimeZone = zone,
                DeliverySettingsJson = settings ?? $$"""{"channels":["share"],"messageTemplate":{{JsonSerializer.Serialize(Template)}}}""",
                EventStartAt = DateTimeOffset.UtcNow.AddDays(10), CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await Task.CompletedTask;
        });
        return id;
    }

    private async Task<Guid> GuestAsync(Guid ev, string name, string? email, string? phone)
    {
        var id = Guid.NewGuid();
        await api.WriteAsync(async db =>
        {
            db.Guests.Add(new Guest { Id = id, CampaignId = ev, Name = name, Email = email, PhoneE164 = phone, CreatedAt = DateTimeOffset.UtcNow });
            await Task.CompletedTask;
        });
        return id;
    }

    private async Task<Guid> InviteAsync(Guid ev, Guid guest, InviteStatus status, string rawToken, bool emailed = false)
    {
        var id = Guid.NewGuid();
        await api.WriteAsync(async db =>
        {
            db.Invites.Add(new Invite
            {
                Id = id, CampaignId = ev, GuestId = guest, Status = status, TokenHash = TokenService.Hash(rawToken),
                FirstEmailedAt = emailed ? DateTimeOffset.UtcNow.AddHours(-2) : null, CreatedAt = DateTimeOffset.UtcNow,
            });
            await Task.CompletedTask;
        });
        return id;
    }

    private async Task AttemptAsync(Guid invite, string channel, string to, DeliveryStatus status, DateTimeOffset at, string? error = null) =>
        await api.WriteAsync(async db =>
        {
            db.DeliveryAttempts.Add(new DeliveryAttempt
            {
                Id = Guid.NewGuid(), InviteId = invite, Channel = channel, RecipientAddress = to, Status = status,
                ErrorMessage = error, AttemptedAt = at,
            });
            await Task.CompletedTask;
        });
}
