using System.Net;
using System.Net.Http.Json;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InvitesBlog.Tests.Integration;

[CollectionDefinition("access-control")]
public sealed class AccessControlCollection : ICollectionFixture<PostgresApiFixture>;

/// <summary>
/// Requests that try to reach someone else's things by changing an id or a body field, through the real API on
/// real Postgres: each must be refused, and the thing it aimed at left as it was.
/// </summary>
[Collection("access-control")]
public sealed class AccessControlTests(PostgresApiFixture api)
{
    private sealed record Person(Guid Id, string Email, string? Phone, string Token);

    // ----- Host details -----------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Typing_another_hosts_email_in_your_own_event_never_changes_their_record_or_reaches_their_events()
    {
        var victim = await UserAsync(phone: "+9607770111");
        var (victimInviter, victimEvent) = await HostedEventAsync(victim, inviterPhone: victim.Phone!);
        // The attacker signs in with their own (verified) phone and owns an event of their own.
        var attacker = await UserAsync(phone: "+9607770999");
        var (_, attackerEvent) = await HostedEventAsync(attacker, inviterPhone: attacker.Phone!);
        var client = api.Client(attacker.Token);
        Refused(await client.GetAsync($"/api/me/campaigns/{victimEvent}/dashboard"));

        var res = await client.PutAsJsonAsync($"/api/campaigns/{attackerEvent}/inviter",
            new { name = "Someone else", phone = attacker.Phone, email = victim.Email.ToUpperInvariant() });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());

        var record = await api.ReadAsync(db => db.Inviters.SingleAsync(i => i.Id == victimInviter));
        Assert.Equal("Victim", record.Name);
        Assert.Equal(victim.Phone, record.PhoneE164);
        // Still nothing of theirs.
        Refused(await client.GetAsync($"/api/me/campaigns/{victimEvent}/dashboard"));
        Assert.Equal(CampaignStatus.Dispatched, await api.ReadAsync(db => db.Campaigns.Where(c => c.Id == victimEvent).Select(c => c.Status).SingleAsync()));
    }

    [PostgresFact]
    public async Task A_host_still_edits_their_own_record()
    {
        var host = await UserAsync(phone: null);
        var (inviter, ev) = await HostedEventAsync(host, inviterPhone: "");

        var res = await api.Client(host.Token).PutAsJsonAsync($"/api/campaigns/{ev}/inviter",
            new { name = "New name", phone = "+9607771111", email = host.Email });
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());

        var record = await api.ReadAsync(db => db.Inviters.SingleAsync(i => i.Id == inviter));
        Assert.Equal("New name", record.Name);
        Assert.Equal("+9607771111", record.PhoneE164);
    }

    [PostgresFact]
    public async Task A_celebrant_with_full_access_cannot_make_themselves_the_organiser()
    {
        var host = await UserAsync(phone: null);
        var (inviter, ev) = await HostedEventAsync(host, inviterPhone: "");
        var manager = await UserAsync(phone: null);
        await CelebrantAsync(ev, manager, canManage: true);
        var client = api.Client(manager.Token);
        // They do run the event...
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/me/campaigns/{ev}/dashboard")).StatusCode);

        // ...but who hosts it is the organiser's to say.
        Refused(await client.PutAsJsonAsync($"/api/campaigns/{ev}/inviter",
            new { name = "Me now", phone = (string?)null, email = manager.Email }));
        Assert.Equal(inviter, await api.ReadAsync(db => db.Campaigns.Where(c => c.Id == ev).Select(c => c.InviterId).SingleAsync()));
    }

    // ----- Albums -----------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task A_guest_who_is_first_to_upload_never_owns_the_events_album()
    {
        var host = await UserAsync(phone: null);
        // An event booked through a link, with no account behind it and no album yet.
        var (_, ev) = await HostedEventAsync(host, inviterPhone: "", createdByAccount: false, album: false);
        var guest = await UserAsync(phone: null);
        await GuestRowAsync(ev, guest);

        await api.Client(guest.Token).PostAsJsonAsync($"/api/me/invitations/{ev}/photos/uploads",
            new { fileName = "a.jpg", contentType = "image/jpeg", size = 2048 });

        var bucket = await api.ReadAsync(db => db.MediaBuckets.SingleAsync(b => b.CampaignId == ev));
        Assert.NotEqual(guest.Id, bucket.OwnerUserId);
        var client = api.Client(guest.Token);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/media-buckets/{bucket.Id}/access")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/media-buckets/{bucket.Id}/qr")).StatusCode);
    }

    [PostgresFact]
    public async Task An_album_a_celebrant_made_stops_being_theirs_when_they_go_back_to_read_only()
    {
        var host = await UserAsync(phone: null);
        var (_, ev) = await HostedEventAsync(host, inviterPhone: "", album: false);
        var celebrant = await UserAsync(phone: null);
        var row = await CelebrantAsync(ev, celebrant, canManage: true);

        var made = await api.Client(celebrant.Token).PostAsJsonAsync("/api/media-buckets",
            new { title = "After party", campaignId = ev });
        Assert.True(made.IsSuccessStatusCode, await made.Content.ReadAsStringAsync());
        var bucket = await api.ReadAsync(db => db.MediaBuckets.SingleAsync(b => b.CampaignId == ev));
        Assert.Equal(host.Id, bucket.OwnerUserId);

        // Even an album that does name them (made before this rule) is only theirs while they have full access.
        await api.WriteAsync(async db =>
        {
            (await db.MediaBuckets.SingleAsync(b => b.Id == bucket.Id)).OwnerUserId = celebrant.Id;
            (await db.CampaignCelebrants.SingleAsync(c => c.Id == row)).CanManage = false;
        });
        var client = api.Client(celebrant.Token);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync($"/api/media-buckets/{bucket.Id}/access")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.PostAsJsonAsync($"/api/media-buckets/{bucket.Id}/qr",
            new { label = "Mine", allowAnonymous = true })).StatusCode);
        // The host still manages it.
        Assert.Equal(HttpStatusCode.OK, (await api.Client(host.Token).GetAsync($"/api/media-buckets/{bucket.Id}/access")).StatusCode);
    }

    // ----- One-time codes ----------------------------------------------------------------------------------

    [PostgresFact]
    public async Task A_code_sent_to_someone_elses_address_never_opens_a_guests_link_from_a_new_network()
    {
        var host = await UserAsync(phone: null);
        var (_, ev) = await HostedEventAsync(host, inviterPhone: "");
        var guestEmail = $"g-{Guid.NewGuid():N}@example.com";
        var token = InvitesBlog.Application.Security.TokenService.GenerateToken();
        var invite = await InviteAsync(ev, guestEmail, token);

        // The guest opens it first (that network is trusted); the link then leaks to someone elsewhere.
        Assert.Equal(HttpStatusCode.Redirect, (await api.BrowserClient().GetAsync($"/i/{token}")).StatusCode);
        Assert.Equal(1, await TrustedAsync(invite));
        var stranger = api.BrowserClient();
        Assert.Equal(HttpStatusCode.OK, (await stranger.GetAsync($"/i/{token}")).StatusCode);   // asks for a code

        // They ask for a code to their OWN address and present it as the guest's.
        var own = $"x-{Guid.NewGuid():N}@example.com";
        var sent = await stranger.PostAsJsonAsync("/api/otp/request", new { channel = "email", email = own });
        Assert.True(sent.IsSuccessStatusCode, await sent.Content.ReadAsStringAsync());
        var challenge = (await sent.Content.ReadFromJsonAsync<Envelope<Challenge>>())!.Data.ChallengeId;
        var verify = new HttpRequestMessage(HttpMethod.Post, $"/i/{token}/verify")
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = api.Codes.CodeFor(own) }),
        };
        verify.Headers.Add("Cookie", $"ib_challenge={challenge}");
        var res = await stranger.SendAsync(verify);
        Assert.NotEqual(HttpStatusCode.Redirect, res.StatusCode);
        Assert.Equal(1, await TrustedAsync(invite));

        // The real way in still works: the code goes to the guest's own address.
        var guest = api.BrowserClient();
        Assert.Equal(HttpStatusCode.OK, (await guest.PostAsync($"/i/{token}/reauth", null)).StatusCode);
        var ok = await guest.PostAsync($"/i/{token}/verify",
            new FormUrlEncodedContent(new Dictionary<string, string> { ["code"] = api.Codes.CodeFor(guestEmail) }));
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
        Assert.Equal(2, await TrustedAsync(invite));
    }

    [PostgresFact]
    public async Task Guesses_sent_side_by_side_still_end_a_code_after_five()
    {
        var email = $"r-{Guid.NewGuid():N}@example.com";
        var sent = await api.Client().PostAsJsonAsync("/api/otp/request", new { channel = "email", email });
        Assert.True(sent.IsSuccessStatusCode, await sent.Content.ReadAsStringAsync());
        var challenge = (await sent.Content.ReadFromJsonAsync<Envelope<Challenge>>())!.Data.ChallengeId;
        var right = api.Codes.CodeFor(email);
        var wrong = Enumerable.Range(0, 12).Select(i => ((int.Parse(right) + 1 + i) % 1_000_000).ToString("D6")).ToList();

        // Each guess from its own address, all at once.
        var answers = await Task.WhenAll(wrong.Select(code =>
            api.Client().PostAsJsonAsync("/api/otp/verify", new { challengeId = challenge, code })));
        Assert.All(answers, a => Assert.False(a.IsSuccessStatusCode));

        var last = await api.Client().PostAsJsonAsync("/api/otp/verify", new { challengeId = challenge, code = right });
        Assert.False(last.IsSuccessStatusCode);
        Assert.Equal(5, await api.ReadAsync(db => db.OtpChallenges.Where(c => c.Id == challenge).Select(c => c.Attempts).SingleAsync()));
    }

    // ----- Helpers ----------------------------------------------------------------------------------------

    private sealed record Envelope<T>(T Data);
    private sealed record Challenge(Guid ChallengeId);

    private async Task<int> TrustedAsync(Guid invite) =>
        await api.ReadAsync(db => db.InviteTrustedIps.CountAsync(t => t.InviteId == invite));

    private async Task<Guid> InviteAsync(Guid ev, string guestEmail, string rawToken)
    {
        var id = Guid.NewGuid();
        await api.WriteAsync(async db =>
        {
            var guest = new Guest { Id = Guid.NewGuid(), CampaignId = ev, Name = "Guest", Email = guestEmail, CreatedAt = DateTimeOffset.UtcNow };
            db.Guests.Add(guest);
            db.Invites.Add(new Invite
            {
                Id = id, CampaignId = ev, GuestId = guest.Id, Status = InviteStatus.Sent,
                TokenHash = InvitesBlog.Application.Security.TokenService.Hash(rawToken),
                FirstEmailedAt = DateTimeOffset.UtcNow.AddHours(-2), CreatedAt = DateTimeOffset.UtcNow,
            });
            await Task.CompletedTask;
        });
        return id;
    }


    /// <summary>Refused, however the endpoint words it (401 for a campaign the caller has no key to, 403, or 404).</summary>
    private static void Refused(HttpResponseMessage res) =>
        Assert.True(res.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.NotFound,
            $"expected a refusal, got {(int)res.StatusCode}");

    private async Task<Person> UserAsync(string? phone)
    {
        var id = Guid.NewGuid();
        var mail = $"ac-{id:N}@example.com";
        await api.WriteAsync(async db =>
        {
            var role = await db.Roles.Where(r => r.Name == Roles.Customer).Select(r => r.Id).SingleAsync();
            var user = new AppUser { Id = id, Email = mail, PhoneE164 = phone, DisplayName = "Person", IsActive = true, CreatedAt = DateTimeOffset.UtcNow };
            user.UserRoles.Add(new UserRole { RoleId = role });
            db.Users.Add(user);
        });
        return new Person(id, mail, phone, api.TokenFor(id, "email", mail));
    }

    /// <summary>A sent event hosted under <paramref name="host"/>'s email (their host record), with an album unless told not to.</summary>
    private async Task<(Guid Inviter, Guid Event)> HostedEventAsync(Person host, string inviterPhone, bool createdByAccount = true, bool album = true)
    {
        var id = Guid.NewGuid();
        var inviterId = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow.AddDays(5);
        await api.WriteAsync(async db =>
        {
            var templateId = Guid.NewGuid();
            db.Templates.Add(new Template
            {
                Id = templateId, Name = "T", Slug = $"ac-{id:N}", Version = "1.0.0", Category = "Imported", Description = "D", PreviewImageUrl = "",
                SceneJson = "{}", ManifestJson = "{}", PackageUrl = "", Visibility = TemplateVisibility.Imported,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.Inviters.Add(new Inviter { Id = inviterId, Name = "Victim", Email = host.Email, PhoneE164 = inviterPhone, CreatedAt = DateTimeOffset.UtcNow });
            db.Campaigns.Add(new Campaign
            {
                Id = id, TemplateId = templateId, TemplateVersion = "1.0.0", TemplatePackageUrl = "",
                InviterId = inviterId, CreatedByUserId = createdByAccount ? host.Id : null, AccessTokenHash = Guid.NewGuid().ToString("N"),
                Title = "An event", Slug = $"ac-{id:N}", Status = CampaignStatus.Dispatched, TimeZone = "Indian/Maldives",
                DeliverySettingsJson = """{"channels":["share"]}""",
                EventStartAt = start, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            if (album)
                db.MediaBuckets.Add(new MediaBucket
                {
                    Id = Guid.NewGuid(), OwnerUserId = host.Id, CampaignId = id, EventDate = start, UploadWindowDays = 1,
                    CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                });
            await Task.CompletedTask;
        });
        return (inviterId, id);
    }

    private async Task<Guid> CelebrantAsync(Guid ev, Person who, bool canManage)
    {
        var id = Guid.NewGuid();
        await api.WriteAsync(async db =>
        {
            db.CampaignCelebrants.Add(new CampaignCelebrant
            {
                Id = id, CampaignId = ev, Name = "Celebrant", Email = who.Email, CanManage = canManage, CreatedAt = DateTimeOffset.UtcNow,
            });
            await Task.CompletedTask;
        });
        return id;
    }

    private async Task GuestRowAsync(Guid ev, Person who)
    {
        await api.WriteAsync(async db =>
        {
            db.Guests.Add(new Guest { Id = Guid.NewGuid(), CampaignId = ev, Name = "Guest", Email = who.Email, CreatedAt = DateTimeOffset.UtcNow });
            await Task.CompletedTask;
        });
    }
}
