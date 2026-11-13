using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using InvitesBlog.Application.Security;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InvitesBlog.Tests.Integration;

[CollectionDefinition("window-preview")]
public sealed class WindowAndPreviewCollection : ICollectionFixture<PostgresApiFixture>;

/// <summary>
/// After an event's photo window has ended no new QR codes or links are made (409 window_closed), while every
/// existing one keeps opening; and "View invite", the host's read-only look at the invitation as a guest sees it.
/// Through the real API on real Postgres.
/// </summary>
[Collection("window-preview")]
public sealed class WindowAndPreviewTests(PostgresApiFixture api)
{
    private sealed record Person(Guid Id, string Email, string Token);

    // ----- No new codes or links after the window ------------------------------------------------------

    [PostgresFact]
    public async Task After_the_window_every_generator_answers_409_window_closed_and_existing_links_still_open()
    {
        var host = await UserAsync();
        var (ev, bucket) = await EventAsync(host, startsInDays: -10);
        var guest = await GuestAsync(ev, "Mariyam", "mariyam@example.com", "+9607771234");
        var token = TokenService.GenerateToken();
        await InviteAsync(ev, guest, token);
        var client = api.Client(host.Token);

        await AssertClosedAsync(await client.PostAsJsonAsync($"/api/media-buckets/{bucket}/qr", new { label = "Tables", allowAnonymous = true }));
        await AssertClosedAsync(await client.PutAsJsonAsync($"/api/campaigns/{ev}/open-link", new { allowAnonymous = false }));
        await AssertClosedAsync(await client.PostAsJsonAsync($"/api/campaigns/{ev}/guests/{guest}/manual-send", new { channel = "whatsapp" }));
        await AssertClosedAsync(await client.PostAsync($"/api/campaigns/{ev}/guests/{guest}/resend", null));
        await AssertClosedAsync(await client.PostAsync($"/api/campaigns/{ev}/finalize", null));

        // Nothing was made by the refusals.
        Assert.Equal(0, await api.ReadAsync(db => db.MediaBucketQrs.CountAsync(q => q.BucketId == bucket)));
        Assert.Equal(0, await api.ReadAsync(db => db.DeliveryAttempts.CountAsync(a => db.Invites.Any(i => i.Id == a.InviteId && i.CampaignId == ev))));

        // The guest's existing link still opens their invitation, never an error.
        Assert.Equal(HttpStatusCode.OK, (await api.Client().GetAsync($"/api/invites/by-token/{token}")).StatusCode);
        // Existing codes can still be listed (and turned off).
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync($"/api/media-buckets/{bucket}/qr")).StatusCode);

        // The dashboard, the album and the Hosting list all say so, with the day it ended.
        var dash = await DataAsync(await client.GetAsync($"/api/me/campaigns/{ev}/dashboard"));
        Assert.True(dash.GetProperty("campaign").GetProperty("photoWindowClosed").GetBoolean());
        Assert.True(dash.GetProperty("campaign").GetProperty("photoWindowClosesAt").GetDateTimeOffset() < DateTimeOffset.UtcNow);
        var album = await DataAsync(await client.GetAsync($"/api/media-buckets/{bucket}"));
        Assert.True(album.GetProperty("windowClosed").GetBoolean());
        var mine = (await DataAsync(await client.GetAsync("/api/me/campaigns"))).EnumerateArray().Single(c => c.GetProperty("id").GetGuid() == ev);
        Assert.True(mine.GetProperty("photoWindowClosed").GetBoolean());
    }

    [PostgresFact]
    public async Task Before_and_during_the_window_the_same_calls_work_and_lengthening_the_window_brings_them_back()
    {
        var host = await UserAsync();
        var (ev, bucket) = await EventAsync(host, startsInDays: 20);
        var guest = await GuestAsync(ev, "Hassan", null, "+9607770001");
        var client = api.Client(host.Token);

        Assert.True((await client.PostAsJsonAsync($"/api/media-buckets/{bucket}/qr", new { label = (string?)null, allowAnonymous = true })).IsSuccessStatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PutAsJsonAsync($"/api/campaigns/{ev}/open-link", new { allowAnonymous = false })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync($"/api/campaigns/{ev}/guests/{guest}/manual-send", new { channel = "viber" })).StatusCode);
        var dash = await DataAsync(await client.GetAsync($"/api/me/campaigns/{ev}/dashboard"));
        Assert.False(dash.GetProperty("campaign").GetProperty("photoWindowClosed").GetBoolean());

        // Two days after a one-day window closed; then the album is stretched to five days (a pass): open again.
        var (late, lateBucket) = await EventAsync(host, startsInDays: -3);
        await AssertClosedAsync(await client.PostAsJsonAsync($"/api/media-buckets/{lateBucket}/qr", new { label = (string?)null, allowAnonymous = true }));
        await api.WriteAsync(async db =>
        {
            var b = await db.MediaBuckets.SingleAsync(x => x.Id == lateBucket);
            b.UploadWindowDays = 5;
        });
        Assert.True((await client.PostAsJsonAsync($"/api/media-buckets/{lateBucket}/qr", new { label = (string?)null, allowAnonymous = true })).IsSuccessStatusCode);
        Assert.False((await DataAsync(await client.GetAsync($"/api/me/campaigns/{late}/dashboard"))).GetProperty("campaign").GetProperty("photoWindowClosed").GetBoolean());
    }

    // ----- View invite ---------------------------------------------------------------------------------

    [PostgresFact]
    public async Task View_invite_shows_the_invitation_as_a_guest_sees_it_and_writes_nothing()
    {
        var host = await UserAsync();
        var (ev, _) = await EventAsync(host, startsInDays: -10, package: "<html><body><h1>Our wedding</h1></body></html>");
        var guest = await GuestAsync(ev, "Mariyam", "mariyam@example.com", null);
        await InviteAsync(ev, guest, TokenService.GenerateToken());

        var link = (await DataAsync(await api.Client(host.Token).GetAsync($"/api/campaigns/{ev}/preview-link")))
            .GetProperty("url").GetString()!;
        Assert.Contains("/h/preview/", link);

        var browser = api.BrowserClient();
        var hop = await browser.GetAsync(new Uri(link).PathAndQuery);
        Assert.Equal(HttpStatusCode.Redirect, hop.StatusCode);
        var page = hop.Headers.Location!.ToString();
        Assert.StartsWith("/r/preview/", page);
        Assert.Contains(hop.Headers.GetValues("Set-Cookie"), c => c.StartsWith("ib_preview=") && c.Contains("httponly", StringComparison.OrdinalIgnoreCase) && c.Contains("path=/r/preview", StringComparison.OrdinalIgnoreCase));

        var res = await browser.GetAsync(page);
        var html = await res.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Contains("Our wedding", html);
        Assert.Contains(">Preview</div>", html);
        Assert.NotNull(res.Headers.GetValues("Content-Security-Policy").SingleOrDefault());
        // The reply button a guest sees is there, and leads to the preview's notice, not to a reply form.
        Assert.Contains($"{page}/rsvp", html);
        var reply = await browser.GetAsync($"{page}/rsvp");
        Assert.Contains("This is a preview", await reply.Content.ReadAsStringAsync());
        Assert.Contains("This is a preview", await (await browser.PostAsync($"{page}/rsvp", new StringContent("status=Going", Encoding.UTF8, "application/x-www-form-urlencoded"))).Content.ReadAsStringAsync());

        // Nothing written: no invite rows, no view, no reply.
        Assert.Equal(1, await api.ReadAsync(db => db.Invites.CountAsync(i => i.CampaignId == ev)));
        Assert.Equal(0, await api.ReadAsync(db => db.Invites.CountAsync(i => i.CampaignId == ev && i.ViewedAt != null)));
        Assert.Equal(0, await api.ReadAsync(db => db.RsvpResponses.CountAsync(r => db.Invites.Any(i => i.Id == r.InviteId && i.CampaignId == ev))));
        Assert.Equal(1, await api.ReadAsync(db => db.Guests.CountAsync(g => g.CampaignId == ev)));

        // The address alone, without the cookie, shows nothing; nor does a stale or forged link.
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.BrowserClient().GetAsync(page)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.BrowserClient().GetAsync("/h/preview/abc.123.def")).StatusCode);
    }

    [PostgresFact]
    public async Task View_invite_is_for_the_host_and_celebrants_only_and_not_for_an_album_alone()
    {
        var host = await UserAsync();
        var (ev, _) = await EventAsync(host, startsInDays: 5, package: "<html><body>x</body></html>");

        var stranger = await UserAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client(stranger.Token).GetAsync($"/api/campaigns/{ev}/preview-link")).StatusCode);

        var bride = await UserAsync();
        await api.WriteAsync(async db =>
        {
            db.CampaignCelebrants.Add(new CampaignCelebrant { Id = Guid.NewGuid(), CampaignId = ev, Name = "Bride", Email = bride.Email, CreatedAt = DateTimeOffset.UtcNow });
            await Task.CompletedTask;
        });
        Assert.Equal(HttpStatusCode.OK, (await api.Client(bride.Token).GetAsync($"/api/campaigns/{ev}/preview-link")).StatusCode);

        var (albumOnly, _) = await EventAsync(host, startsInDays: 5, package: null);
        var refused = await api.Client(host.Token).GetAsync($"/api/campaigns/{albumOnly}/preview-link");
        Assert.Equal(HttpStatusCode.NotFound, refused.StatusCode);
        Assert.Contains("no_invitation", await refused.Content.ReadAsStringAsync());
    }

    // ----- Helpers -------------------------------------------------------------------------------------

    private static async Task AssertClosedAsync(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.StatusCode == HttpStatusCode.Conflict, $"{(int)res.StatusCode}: {body}");
        Assert.Contains("window_closed", body);
        Assert.Contains("Guests can still view the album", body);
    }

    private static async Task<JsonElement> DataAsync(HttpResponseMessage res)
    {
        var body = await res.Content.ReadAsStringAsync();
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("data").Clone();
    }

    private async Task<Person> UserAsync()
    {
        var id = Guid.NewGuid();
        var mail = $"w-{id:N}@example.com";
        await api.WriteAsync(async db =>
        {
            var role = await db.Roles.Where(r => r.Name == Roles.Customer).Select(r => r.Id).SingleAsync();
            var user = new AppUser { Id = id, Email = mail, DisplayName = "Host", IsActive = true, CreatedAt = DateTimeOffset.UtcNow };
            user.UserRoles.Add(new UserRole { RoleId = role });
            db.Users.Add(user);
        });
        return new Person(id, mail, api.TokenFor(id, "email", mail));
    }

    /// <summary>A sent event starting <paramref name="startsInDays"/> from now, with its one-day album and, when given, an invitation page.</summary>
    private async Task<(Guid Event, Guid Bucket)> EventAsync(Person creator, int startsInDays, string? package = "<html><body>Invitation</body></html>")
    {
        var id = Guid.NewGuid();
        var bucketId = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        var start = DateTimeOffset.UtcNow.AddDays(startsInDays);
        var packageUrl = package is null ? "" : $"http://localhost/assets/packages/preview-{id:N}/";
        if (package is not null)
            await api.Storage.PutAsync($"packages/preview-{id:N}/index.html", Encoding.UTF8.GetBytes(package), "text/html");

        await api.WriteAsync(async db =>
        {
            var inviterId = Guid.NewGuid();
            db.Inviters.Add(new Inviter { Id = inviterId, Name = "Aisha", Email = $"inviter-{id:N}@example.com", PhoneE164 = "", CreatedAt = DateTimeOffset.UtcNow });
            db.Templates.Add(new Template
            {
                Id = templateId, Name = "T", Slug = $"own-{id:N}", Version = "1.0.0", Category = "Imported", Description = "D", PreviewImageUrl = "",
                SceneJson = "{}", ManifestJson = "{}", PackageUrl = packageUrl, Visibility = TemplateVisibility.Imported,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.Campaigns.Add(new Campaign
            {
                Id = id, TemplateId = templateId, TemplateVersion = "1.0.0", TemplatePackageUrl = packageUrl,
                InviterId = inviterId, CreatedByUserId = creator.Id, AccessTokenHash = Guid.NewGuid().ToString("N"),
                Title = "An event", Slug = $"e-{id:N}", Status = CampaignStatus.Dispatched, TimeZone = "Indian/Maldives",
                DeliverySettingsJson = """{"channels":["share"]}""",
                EventStartAt = start, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.MediaBuckets.Add(new MediaBucket
            {
                Id = bucketId, OwnerUserId = creator.Id, CampaignId = id, EventDate = start, UploadWindowDays = 1,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await Task.CompletedTask;
        });
        return (id, bucketId);
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

    private async Task<Guid> InviteAsync(Guid ev, Guid guest, string rawToken)
    {
        var id = Guid.NewGuid();
        await api.WriteAsync(async db =>
        {
            db.Invites.Add(new Invite
            {
                Id = id, CampaignId = ev, GuestId = guest, Status = InviteStatus.Sent, TokenHash = TokenService.Hash(rawToken),
                FirstEmailedAt = DateTimeOffset.UtcNow.AddHours(-2), CreatedAt = DateTimeOffset.UtcNow,
            });
            await Task.CompletedTask;
        });
        return id;
    }
}
