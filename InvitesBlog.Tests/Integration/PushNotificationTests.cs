using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InvitesBlog.Tests.Integration;

[CollectionDefinition("push")]
public sealed class PushCollection : ICollectionFixture<PostgresApiFixture>;

/// <summary>
/// Push notifications to iPhones, through the real API on real Postgres, with a stand-in for Apple's
/// push service. Devices register through the API, the actions that notify are real requests, and the
/// queued pushes are sent by draining the worker's line, as the running worker would.
/// </summary>
[Collection("push")]
public sealed class PushNotificationTests(PostgresApiFixture api)
{
    private sealed record Person(Guid Id, string Email, string Name, string Token);

    // ----- Registering ------------------------------------------------------------------------------

    [PostgresFact]
    public async Task An_iPhone_registers_with_its_environment_and_moves_to_whoever_signs_in_next()
    {
        var first = await UserAsync();
        var second = await UserAsync();
        var token = DeviceToken();

        await RegisterAsync(first, token, environment: "sandbox");
        var row = await api.ReadAsync(db => db.DeviceTokens.SingleAsync(d => d.Token == token));
        Assert.Equal((first.Id, "ios", "sandbox"), (row.UserId, row.Platform, row.Environment));

        await RegisterAsync(second, token);
        row = await api.ReadAsync(db => db.DeviceTokens.SingleAsync(d => d.Token == token));
        Assert.Equal((second.Id, "production"), (row.UserId, row.Environment));

        var bad = await api.Client(first.Token).PostAsJsonAsync("/api/me/devices", new { token = DeviceToken(), platform = "ios", environment = "staging" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, bad.StatusCode);
    }

    // ----- Invitations ------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Sending_invitations_tells_each_invited_account_once_and_never_the_host()
    {
        var host = await UserAsync(name: "Aisha");
        var cohost = await UserAsync(name: "Ahmed");
        var invited = await UserAsync();
        var noApp = await UserAsync();
        // Started by Aisha, hosted in the name of Ahmed's account: both run it, only Aisha presses send.
        var ev = await EventAsync(host, title: "Aisha's wedding", inviterName: "Aisha", inviterEmail: cohost.Email);
        await GuestAsync(ev, "Invited", invited.Email);
        await GuestAsync(ev, "No app", noApp.Email);
        await GuestAsync(ev, "The host herself", host.Email);
        await GuestAsync(ev, "The co-host", cohost.Email);
        await GuestAsync(ev, "Not on invites.blog", $"nobody-{Guid.NewGuid():N}@example.com");
        var phone = await RegisterAsync(invited, DeviceToken());
        var hostPhone = await RegisterAsync(host, DeviceToken());
        var cohostPhone = await RegisterAsync(cohost, DeviceToken());

        var sent = await api.Client(host.Token).PostAsync($"/api/campaigns/{ev}/finalize", null);
        Assert.Equal(HttpStatusCode.OK, sent.StatusCode);
        await api.DrainPushesAsync();

        var push = Assert.Single(api.Apns.To(phone));
        Assert.Equal(("You're invited", "Aisha invited you to Aisha's wedding.", $"/invitation/{ev}"), (push.Title, push.Body, push.Path));
        Assert.Equal("api.push.apple.com", push.Host);
        Assert.Equal(("blog.invites.app", "alert"), (push.Topic, push.PushType));
        Assert.Equal(HttpVersion.Version20, push.Version);
        AssertSignedByTestKey(push);
        Assert.Empty(api.Apns.To(hostPhone));
        Assert.Empty(api.Apns.To(cohostPhone));

        // Sending again (or a resend) doesn't buzz anyone twice.
        Assert.Equal(HttpStatusCode.OK, (await api.Client(host.Token).PostAsync($"/api/campaigns/{ev}/finalize", null)).StatusCode);
        await api.DrainPushesAsync();
        Assert.Single(api.Apns.To(phone));
        AssertNoTokenLogged(phone, hostPhone);
    }

    // ----- Replies ----------------------------------------------------------------------------------

    [PostgresFact]
    public async Task A_reply_reaches_the_host_and_the_people_it_is_for_but_not_the_guest_who_answered()
    {
        var host = await UserAsync();
        var bride = await UserAsync();
        var guest = await UserAsync();
        var ev = await EventAsync(host, title: "Engagement");
        await CelebrantAsync(ev, bride.Email);
        // Mariyam is on the guest list and also one of the people the event is for, so she'd see the
        // dashboard's news: her own answer still isn't news to her.
        await CelebrantAsync(ev, guest.Email);
        var invite = await InviteAsync(ev, await GuestAsync(ev, "Mariyam", guest.Email));
        var hostPhone = await RegisterAsync(host, DeviceToken());
        var bridePhone = await RegisterAsync(bride, DeviceToken());
        var guestPhone = await RegisterAsync(guest, DeviceToken());

        // Through the emailed link, not signed in: the guest is known only by the guest list.
        var reply = await api.Client().PostAsJsonAsync($"/api/invites/by-token/{invite:N}/rsvp", new { status = "Going", guestCount = 2 });
        Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
        await api.DrainPushesAsync();

        foreach (var p in new[] { hostPhone, bridePhone })
        {
            var push = Assert.Single(api.Apns.To(p));
            Assert.Equal(("Engagement", "Mariyam is coming.", $"/dashboard/{ev}"), (push.Title, push.Body, push.Path));
        }
        Assert.Empty(api.Apns.To(guestPhone));

        // The same answer again is not news; a changed one is.
        await api.Client(guest.Token).PostAsJsonAsync($"/api/invites/{invite}/rsvp", new { status = "Going" });
        await api.DrainPushesAsync();
        Assert.Single(api.Apns.To(hostPhone));
        await api.Client(guest.Token).PostAsJsonAsync($"/api/invites/{invite}/rsvp", new { status = "NotGoing" });
        await api.DrainPushesAsync();
        Assert.Equal("Mariyam can't make it.", api.Apns.To(hostPhone).Last().Body);
    }

    // ----- Comments ---------------------------------------------------------------------------------

    [PostgresFact]
    public async Task A_comment_reaches_the_organiser_and_a_reply_reaches_the_comment_s_author_not_whoever_wrote_it()
    {
        var host = await UserAsync(name: "Host");
        var guest = await UserAsync(name: "Hawwa");
        var ev = await EventAsync(host, title: "Birthday");
        await InviteAsync(ev, await GuestAsync(ev, "Hawwa", guest.Email), InviteStatus.Sent);
        var hostPhone = await RegisterAsync(host, DeviceToken());
        var guestPhone = await RegisterAsync(guest, DeviceToken());

        var long_ = "So happy for you!   " + new string('x', 200);
        var made = await api.Client(guest.Token).PostAsJsonAsync($"/api/me/feed/{ev}/comments", new { body = long_ });
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        await api.DrainPushesAsync();

        var push = Assert.Single(api.Apns.To(hostPhone));
        Assert.Equal("Birthday", push.Title);
        Assert.StartsWith("Hawwa commented: So happy for you! xxx", push.Body);
        Assert.True(push.Body.Length < 110, push.Body);
        Assert.Equal($"/feed?post={ev}", push.Path);
        Assert.Empty(api.Apns.To(guestPhone));

        var commentId = await api.ReadAsync(db => db.PostComments.Where(c => c.CampaignId == ev).Select(c => c.Id).SingleAsync());
        await api.Client(host.Token).PostAsJsonAsync($"/api/me/feed/{ev}/comments", new { body = "Thank you!", parentId = commentId });
        await api.DrainPushesAsync();

        Assert.Equal("Host replied: Thank you!", Assert.Single(api.Apns.To(guestPhone)).Body);
        Assert.Single(api.Apns.To(hostPhone));
    }

    // ----- Celebrants -------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Being_added_to_an_event_tells_that_person_s_phone_and_only_iPhones_go_to_Apple()
    {
        var host = await UserAsync();
        var groom = await UserAsync();
        var ev = await EventAsync(host, title: "Nikah");
        var iphone = await RegisterAsync(groom, DeviceToken());
        var android = await RegisterAsync(groom, DeviceToken(), platform: "android");

        var added = await api.Client(host.Token).PostAsJsonAsync($"/api/campaigns/{ev}/celebrants", new { name = "Groom", email = groom.Email });
        Assert.Equal(HttpStatusCode.OK, added.StatusCode);
        await api.DrainPushesAsync();

        var push = Assert.Single(api.Apns.To(iphone));
        Assert.Equal(("You've been added to Nikah", $"/dashboard/{ev}"), (push.Title, push.Path));
        Assert.Empty(api.Apns.To(android));
    }

    // ----- Dead tokens and failures -----------------------------------------------------------------

    [PostgresFact]
    public async Task Tokens_Apple_reports_dead_are_forgotten_and_the_others_still_get_the_message()
    {
        var host = await UserAsync();
        var person = await UserAsync();
        var ev = await EventAsync(host);
        var good = await RegisterAsync(person, DeviceToken());
        var sandbox = await RegisterAsync(person, DeviceToken(), environment: "sandbox");
        var uninstalled = await RegisterAsync(person, DeviceToken());
        var bad = await RegisterAsync(person, DeviceToken());
        var busy = await RegisterAsync(person, DeviceToken());
        api.Apns.Refuse[uninstalled] = (HttpStatusCode.Gone, "Unregistered");
        api.Apns.Refuse[bad] = (HttpStatusCode.BadRequest, "BadDeviceToken");
        api.Apns.Refuse[busy] = (HttpStatusCode.TooManyRequests, "TooManyRequests");

        await api.Client(host.Token).PostAsJsonAsync($"/api/campaigns/{ev}/celebrants", new { name = "P", email = person.Email });
        await api.DrainPushesAsync();

        Assert.Single(api.Apns.To(good));
        Assert.Equal("api.sandbox.push.apple.com", Assert.Single(api.Apns.To(sandbox)).Host);
        var left = await api.ReadAsync(db => db.DeviceTokens.Where(d => d.UserId == person.Id).Select(d => d.Token).ToListAsync());
        Assert.Equal(new[] { good, sandbox, busy }.Order(), left.Order());
        AssertNoTokenLogged(good, sandbox, uninstalled, bad, busy);
    }

    [PostgresFact]
    public async Task When_Apple_cannot_be_reached_the_action_still_succeeds_and_nothing_is_thrown_or_logged_with_a_token()
    {
        var host = await UserAsync();
        var guest = await UserAsync();
        var ev = await EventAsync(host);
        var invite = await InviteAsync(ev, await GuestAsync(ev, "G", guest.Email));
        var hostPhone = await RegisterAsync(host, DeviceToken());

        api.Apns.Down = true;
        try
        {
            var reply = await api.Client(guest.Token).PostAsJsonAsync($"/api/invites/{invite}/rsvp", new { status = "Maybe" });
            Assert.Equal(HttpStatusCode.OK, reply.StatusCode);
            Assert.Equal(1, await api.DrainPushesAsync());
        }
        finally
        {
            api.Apns.Down = false;
        }

        Assert.Equal(RsvpStatus.Maybe, await api.ReadAsync(db => db.Invites.Where(i => i.Id == invite).Select(i => i.RsvpStatus).SingleAsync()));
        Assert.True(await api.ReadAsync(db => db.DeviceTokens.AnyAsync(d => d.Token == hostPhone)), "a failed send must not forget the device");
        Assert.Contains(api.Logs.Lines, l => l.Contains("Couldn't reach APNs", StringComparison.Ordinal));
        AssertNoTokenLogged(hostPhone);
    }

    // ----- Helpers ----------------------------------------------------------------------------------

    private void AssertNoTokenLogged(params string[] tokens)
    {
        Assert.NotEmpty(api.Logs.Lines);
        foreach (var line in api.Logs.Lines)
        foreach (var t in tokens)
            Assert.False(line.Contains(t, StringComparison.OrdinalIgnoreCase), $"A device token reached the log: {line}");
        foreach (var line in api.Logs.Lines)
            Assert.DoesNotContain("PRIVATE KEY", line);
    }

    private void AssertSignedByTestKey(ApnsRequest push)
    {
        Assert.Equal("bearer", push.AuthScheme);
        var parts = push.Jwt!.Split('.');
        using var header = JsonDocument.Parse(B64(parts[0]));
        using var claims = JsonDocument.Parse(B64(parts[1]));
        Assert.Equal(("ES256", ApnsStub.KeyId), (header.RootElement.GetProperty("alg").GetString(), header.RootElement.GetProperty("kid").GetString()));
        Assert.Equal(ApnsStub.TeamId, claims.RootElement.GetProperty("iss").GetString());
        using var key = api.Apns.PublicKey;
        Assert.True(key.VerifyData(Encoding.ASCII.GetBytes($"{parts[0]}.{parts[1]}"), B64(parts[2]), System.Security.Cryptography.HashAlgorithmName.SHA256));
    }

    private static byte[] B64(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + (4 - s.Length % 4) % 4, '='));
    }

    /// <summary>A token shaped like Apple's: 64 hex characters.</summary>
    private static string DeviceToken() => Convert.ToHexString(Guid.NewGuid().ToByteArray()) + Convert.ToHexString(Guid.NewGuid().ToByteArray());

    private async Task<string> RegisterAsync(Person who, string token, string platform = "ios", string? environment = null)
    {
        var r = await api.Client(who.Token).PostAsJsonAsync("/api/me/devices", new { token, platform, environment });
        Assert.Equal(HttpStatusCode.OK, r.StatusCode);
        return token;
    }

    private async Task<Person> UserAsync(string name = "Test Person")
    {
        var id = Guid.NewGuid();
        var mail = $"p-{id:N}@example.com";
        await api.WriteAsync(async db =>
        {
            var role = await db.Roles.Where(r => r.Name == Roles.Customer).Select(r => r.Id).SingleAsync();
            var user = new AppUser { Id = id, Email = mail, DisplayName = name, IsActive = true, CreatedAt = DateTimeOffset.UtcNow };
            user.UserRoles.Add(new UserRole { RoleId = role });
            db.Users.Add(user);
        });
        return new Person(id, mail, name, api.TokenFor(id, "email", mail));
    }

    private async Task<Guid> EventAsync(Person creator, string title = "An event", string? inviterName = null, string? inviterEmail = null)
    {
        var id = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        await api.WriteAsync(async db =>
        {
            Guid? inviterId = null;
            if (inviterName is not null)
            {
                inviterId = Guid.NewGuid();
                db.Inviters.Add(new Inviter { Id = inviterId.Value, Name = inviterName, Email = inviterEmail ?? creator.Email, PhoneE164 = "", CreatedAt = DateTimeOffset.UtcNow });
            }
            db.Templates.Add(new Template
            {
                Id = templateId, Name = title, Slug = $"own-{id:N}", Version = "1.0.0", Category = "Imported", Description = "D", PreviewImageUrl = "",
                SceneJson = "{}", ManifestJson = "{}", PackageUrl = "", Visibility = TemplateVisibility.Imported,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.Campaigns.Add(new Campaign
            {
                Id = id, TemplateId = templateId, TemplateVersion = "1.0.0", TemplatePackageUrl = "",
                InviterId = inviterId, CreatedByUserId = creator.Id, AccessTokenHash = Guid.NewGuid().ToString("N"),
                Title = title, Slug = $"e-{id:N}", Status = CampaignStatus.Dispatched,
                EventStartAt = DateTimeOffset.UtcNow.AddDays(10), CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await Task.CompletedTask;
        });
        return id;
    }

    private async Task<Guid> GuestAsync(Guid ev, string name, string email)
    {
        var id = Guid.NewGuid();
        await api.WriteAsync(async db =>
        {
            db.Guests.Add(new Guest { Id = id, CampaignId = ev, Name = name, Email = email, CreatedAt = DateTimeOffset.UtcNow });
            await Task.CompletedTask;
        });
        return id;
    }

    private async Task CelebrantAsync(Guid ev, string email) =>
        await api.WriteAsync(async db =>
        {
            db.CampaignCelebrants.Add(new CampaignCelebrant { Id = Guid.NewGuid(), CampaignId = ev, Name = "Celebrant", Email = email, CreatedAt = DateTimeOffset.UtcNow });
            await Task.CompletedTask;
        });

    private async Task<Guid> InviteAsync(Guid ev, Guid guest, InviteStatus status = InviteStatus.Created)
    {
        var id = Guid.NewGuid();
        await api.WriteAsync(async db =>
        {
            db.Invites.Add(new Invite
            {
                Id = id, CampaignId = ev, GuestId = guest, Status = status,
                TokenHash = InvitesBlog.Application.Security.TokenService.Hash(id.ToString("N")), CreatedAt = DateTimeOffset.UtcNow,
            });
            await Task.CompletedTask;
        });
        return id;
    }
}
