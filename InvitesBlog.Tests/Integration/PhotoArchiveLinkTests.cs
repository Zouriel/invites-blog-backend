using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InvitesBlog.Tests.Integration;

[CollectionDefinition("archive-links")]
public sealed class ArchiveLinkCollection : ICollectionFixture<PostgresApiFixture>;

/// <summary>
/// Download all on a big album: the photos are named in a POST body (a query string of 1,500 ids is
/// 60 KB, and the server refuses a request line past 8 KB), and the zip is fetched from a link that
/// needs no session, so a browser's own download manager can save it.
/// </summary>
[Collection("archive-links")]
public sealed class PhotoArchiveLinkTests(PostgresApiFixture api)
{
    private sealed record Person(Guid Id, string Email, string Token);

    [PostgresFact]
    public async Task A_host_names_1500_photos_in_the_body_and_the_link_alone_downloads_the_zip()
    {
        // On Premium: more than ten at once is theirs anywhere (pricing 2026-11).
        var host = await UserAsync(SubscriptionTier.Premium);
        var ev = await EventAsync(host);
        var stored = new List<(Guid Id, byte[] Bytes)>();
        for (var i = 0; i < 3; i++) stored.Add(await PhotoAsync(ev, i));
        // The three real ones among 1,500 ids, like an app naming a whole album.
        var ids = stored.Select(s => s.Id).Concat(Enumerable.Range(0, 1497).Select(_ => Guid.NewGuid())).ToArray();

        // The album says how big each original is, so the app can say what saving it all will take.
        var listed = (await JsonDocument.ParseAsync(await (await api.Client(host.Token).GetAsync($"/api/campaigns/{ev}/photos")).Content.ReadAsStreamAsync()))
            .RootElement.GetProperty("data").GetProperty("photos").EnumerateArray()
            .ToDictionary(p => p.GetProperty("id").GetGuid(), p => p.GetProperty("sizeBytes").GetInt64());
        Assert.All(stored, s => Assert.Equal(s.Bytes.Length, listed[s.Id]));

        // (The same ids in a query string are 60 KB; Kestrel answers 414 past 8 KB. The test server
        // here doesn't apply that limit, so it was shown on a real Kestrel instead.)

        var made = await api.Client(host.Token).PostAsJsonAsync($"/api/campaigns/{ev}/photos/download-link", new { ids });
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);
        var link = (await JsonDocument.ParseAsync(await made.Content.ReadAsStreamAsync())).RootElement.GetProperty("data");
        Assert.Equal(3, link.GetProperty("count").GetInt32());
        var url = link.GetProperty("url").GetString()!;
        Assert.StartsWith("/api/photos/archives/", url);

        // No session at all: the link is the whole authorization, and it can be fetched again (a retry).
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var got = await api.Client().GetAsync(url);
            Assert.Equal(HttpStatusCode.OK, got.StatusCode);
            Assert.Equal("application/zip", got.Content.Headers.ContentType?.MediaType);
            Assert.True(got.Headers.CacheControl?.NoStore, "a download must never be cached by a proxy");
            using var zip = new ZipArchive(new MemoryStream(await got.Content.ReadAsByteArrayAsync()), ZipArchiveMode.Read);
            Assert.Equal(3, zip.Entries.Count);
            var contents = new List<byte[]>();
            foreach (var entry in zip.Entries.OrderBy(e => e.FullName))
            {
                await using var read = await entry.OpenAsync();
                using var copy = new MemoryStream();
                await read.CopyToAsync(copy);
                contents.Add(copy.ToArray());
            }
            Assert.Equal(stored.Select(s => s.Bytes), contents);
        }
    }

    [PostgresFact]
    public async Task A_guest_gets_a_link_to_what_they_can_see_and_a_stranger_gets_none()
    {
        var host = await UserAsync();
        var guest = await UserAsync();
        var stranger = await UserAsync();
        // A Gold pass: everyone who sees the photos may download them all.
        var ev = await EventAsync(host, EventPassKind.Gold);
        await GuestAsync(ev, guest.Email);
        await PhotoAsync(ev, 0);

        var made = await api.Client(guest.Token).PostAsJsonAsync($"/api/me/invitations/{ev}/photos/download-link", new { });
        Assert.Equal(HttpStatusCode.OK, made.StatusCode);

        var refused = await api.Client(stranger.Token).PostAsJsonAsync($"/api/me/invitations/{ev}/photos/download-link", new { });
        Assert.False(refused.IsSuccessStatusCode);
        var asHost = await api.Client(stranger.Token).PostAsJsonAsync($"/api/campaigns/{ev}/photos/download-link", new { });
        Assert.False(asHost.IsSuccessStatusCode);
        var signedOut = await api.Client().PostAsJsonAsync($"/api/campaigns/{ev}/photos/download-link", new { });
        Assert.False(signedOut.IsSuccessStatusCode);
    }

    [PostgresFact]
    public async Task A_link_nobody_was_given_is_not_found()
    {
        var got = await api.Client().GetAsync("/api/photos/archives/not-a-real-link-0123456789abcdefghijklmnop");
        Assert.Equal(HttpStatusCode.NotFound, got.StatusCode);
        Assert.Contains("expired", await got.Content.ReadAsStringAsync());
    }

    // ----- Ten at a time (pricing 2026-11) ------------------------------------------------------

    private static async Task<(HttpStatusCode Status, string? Code)> OutcomeAsync(HttpResponseMessage r)
    {
        if (r.IsSuccessStatusCode) return (r.StatusCode, null);
        var body = await r.Content.ReadFromJsonAsync<JsonElement>();
        var code = body.TryGetProperty("errors", out var e) && e.ValueKind == JsonValueKind.Array && e.GetArrayLength() > 0
            ? e[0].GetProperty("code").GetString() : null;
        return (r.StatusCode, code);
    }

    private static readonly (HttpStatusCode, string?) Refused = (HttpStatusCode.Forbidden, "download_limit");

    [PostgresFact]
    public async Task On_a_free_event_anyone_takes_up_to_ten_at_a_time_and_never_all_of_them()
    {
        var host = await UserAsync();
        var guest = await UserAsync();
        var ev = await EventAsync(host);
        await GuestAsync(ev, guest.Email);
        var photos = new List<Guid>();
        for (var i = 0; i < 11; i++) photos.Add((await PhotoAsync(ev, i)).Id);
        var ten = photos.Take(10).ToArray();
        var eleven = photos.ToArray();

        foreach (var (token, route) in new[] { (host.Token, $"/api/campaigns/{ev}/photos"), (guest.Token, $"/api/me/invitations/{ev}/photos") })
        {
            Assert.Equal(HttpStatusCode.OK, (await api.Client(token).PostAsJsonAsync($"{route}/download-link", new { ids = ten })).StatusCode);
            Assert.Equal(Refused, await OutcomeAsync(await api.Client(token).PostAsJsonAsync($"{route}/download-link", new { ids = eleven })));
            // No ids is "Download all".
            Assert.Equal(Refused, await OutcomeAsync(await api.Client(token).PostAsJsonAsync($"{route}/download-link", new { })));
            // The streamed zip too, before a byte of it is written.
            var streamed = await api.Client(token).GetAsync($"{route}/download");
            Assert.Equal(HttpStatusCode.Forbidden, streamed.StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await api.Client(token).GetAsync($"{route}/download?{string.Join('&', ten.Select(id => $"ids={id}"))}")).StatusCode);

            // The album says so, so the apps can stop the eleventh tap.
            var box = (await api.Client(token).GetFromJsonAsync<JsonElement>(route)).GetProperty("data");
            Assert.False(box.GetProperty("downloadAll").GetBoolean());
            Assert.Equal(10, box.GetProperty("downloadLimit").GetInt32());
        }
    }

    [PostgresFact]
    public async Task The_viewers_own_premium_or_studio_lifts_the_limit_anywhere_they_can_see_photos()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        await PhotoAsync(ev, 0);
        await PhotoAsync(ev, 1);

        foreach (var tier in new[] { SubscriptionTier.Premium, SubscriptionTier.Venue })
        {
            var guest = await UserAsync(tier);
            await GuestAsync(ev, guest.Email);
            Assert.Equal(HttpStatusCode.OK, (await api.Client(guest.Token).PostAsJsonAsync($"/api/me/invitations/{ev}/photos/download-link", new { })).StatusCode);
            Assert.True((await api.Client(guest.Token).GetFromJsonAsync<JsonElement>($"/api/me/invitations/{ev}/photos")).GetProperty("data").GetProperty("downloadAll").GetBoolean());
            var me = (await api.Client(guest.Token).GetFromJsonAsync<JsonElement>("/api/auth/me")).GetProperty("data");
            Assert.True(me.GetProperty("canDownloadAll").GetBoolean());
        }

        // A Premium that has ended is Free again.
        var lapsed = await UserAsync(SubscriptionTier.Premium, DateTimeOffset.UtcNow.AddMinutes(-5));
        await GuestAsync(ev, lapsed.Email);
        Assert.Equal(Refused, await OutcomeAsync(await api.Client(lapsed.Token).PostAsJsonAsync($"/api/me/invitations/{ev}/photos/download-link", new { })));
        Assert.False((await api.Client(lapsed.Token).GetFromJsonAsync<JsonElement>("/api/auth/me")).GetProperty("data").GetProperty("canDownloadAll").GetBoolean());
    }

    [PostgresFact]
    public async Task A_pass_in_force_lifts_it_for_everyone_in_that_event_and_an_ended_pass_does_not()
    {
        var host = await UserAsync();
        var guest = await UserAsync();
        var gold = await EventAsync(host, EventPassKind.Platinum);
        var ended = await EventAsync(host, EventPassKind.Gold, DateTimeOffset.UtcNow.AddMinutes(-5));
        foreach (var ev in new[] { gold, ended })
        {
            await GuestAsync(ev, guest.Email);
            await PhotoAsync(ev, 0);
        }

        Assert.Equal(HttpStatusCode.OK, (await api.Client(guest.Token).PostAsJsonAsync($"/api/me/invitations/{gold}/photos/download-link", new { })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.Client(host.Token).PostAsJsonAsync($"/api/campaigns/{gold}/photos/download-link", new { })).StatusCode);
        Assert.True((await api.Client(guest.Token).GetFromJsonAsync<JsonElement>($"/api/me/invitations/{gold}/photos")).GetProperty("data").GetProperty("downloadAll").GetBoolean());
        Assert.Equal(Refused, await OutcomeAsync(await api.Client(guest.Token).PostAsJsonAsync($"/api/me/invitations/{ended}/photos/download-link", new { })));
    }

    [PostgresFact]
    public async Task Favourites_are_ten_at_a_time_unless_the_account_or_every_chosen_event_allows_more()
    {
        var host = await UserAsync();
        var free = await EventAsync(host);
        var platinum = await EventAsync(host, EventPassKind.Platinum);
        var inFree = new List<Guid>();
        var inPlatinum = new List<Guid>();
        for (var i = 0; i < 11; i++)
        {
            inFree.Add((await PhotoAsync(free, i)).Id);
            inPlatinum.Add((await PhotoAsync(platinum, i)).Id);
        }
        await api.WriteAsync(async db =>
        {
            foreach (var id in inFree.Concat(inPlatinum))
                db.Favourites.Add(new Favourite { Id = Guid.NewGuid(), UserId = host.Id, PhotoId = id, CreatedAt = DateTimeOffset.UtcNow });
            await Task.CompletedTask;
        });
        var client = api.Client(host.Token);

        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/me/favourites/download-link", new { ids = inFree.Take(10) })).StatusCode);
        Assert.Equal(Refused, await OutcomeAsync(await client.PostAsJsonAsync("/api/me/favourites/download-link", new { ids = inFree })));
        Assert.Equal(Refused, await OutcomeAsync(await client.PostAsJsonAsync("/api/me/favourites/download-link", new { })));
        // Eleven, all from the Platinum event: that event may be downloaded whole, so they may too.
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/me/favourites/download-link", new { ids = inPlatinum })).StatusCode);
        // One from the free event among them is enough to need the account's own Premium.
        Assert.Equal(Refused, await OutcomeAsync(await client.PostAsJsonAsync("/api/me/favourites/download-link", new { ids = inPlatinum.Append(inFree[0]) })));

        await api.WriteAsync(async db =>
        {
            var u = await db.Users.SingleAsync(x => x.Id == host.Id);
            u.SubscriptionTier = SubscriptionTier.Premium;
            u.SubscriptionEndsAt = DateTimeOffset.UtcNow.AddDays(10);
        });
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/me/favourites/download-link", new { })).StatusCode);
    }

    private async Task<(Guid Id, byte[] Bytes)> PhotoAsync(Guid ev, int n)
    {
        var id = Guid.NewGuid();
        var bytes = Enumerable.Range(0, 1000 + n).Select(i => (byte)(i * 7 + n)).ToArray();
        var key = $"campaigns/{ev:N}/photos/{id:N}_o.jpg";
        var url = await api.Storage.PutAsync(key, bytes, "image/jpeg");
        await api.WriteAsync(async db =>
        {
            db.EventPhotos.Add(new EventPhoto
            {
                Id = id, CampaignId = ev, UploaderName = "Guest", OriginalUrl = url, Url = url, ThumbUrl = url,
                ContentType = "image/jpeg", SizeBytes = bytes.Length, Width = 10, Height = 10,
                CreatedAt = DateTimeOffset.UtcNow.AddSeconds(n),
            });
            await Task.CompletedTask;
        });
        return (id, bytes);
    }

    private async Task<Person> UserAsync(SubscriptionTier tier = SubscriptionTier.None, DateTimeOffset? endsAt = null)
    {
        var id = Guid.NewGuid();
        var mail = $"a-{id:N}@example.com";
        await api.WriteAsync(async db =>
        {
            var roleId = await db.Roles.Where(r => r.Name == Roles.Customer).Select(r => r.Id).SingleAsync();
            var user = new AppUser
            {
                Id = id, Email = mail, DisplayName = "Person", IsActive = true, CreatedAt = DateTimeOffset.UtcNow,
                SubscriptionTier = tier, SubscriptionEndsAt = tier == SubscriptionTier.None ? null : endsAt ?? DateTimeOffset.UtcNow.AddDays(20),
            };
            user.UserRoles.Add(new UserRole { RoleId = roleId });
            db.Users.Add(user);
        });
        return new Person(id, mail, api.TokenFor(id, "email", mail, Roles.Customer));
    }

    private async Task<Guid> EventAsync(Person creator, EventPassKind pass = EventPassKind.None, DateTimeOffset? passUntil = null)
    {
        var id = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        await api.WriteAsync(async db =>
        {
            db.Templates.Add(new Template
            {
                Id = templateId, Name = "Album", Slug = $"own-{id:N}", Version = "1.0.0", Category = "Imported", Description = "D", PreviewImageUrl = "",
                SceneJson = "{}", ManifestJson = "{}", PackageUrl = "", Visibility = TemplateVisibility.Imported,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.Campaigns.Add(new Campaign
            {
                Id = id, TemplateId = templateId, TemplateVersion = "1.0.0", TemplatePackageUrl = "",
                CreatedByUserId = creator.Id, AccessTokenHash = Guid.NewGuid().ToString("N"),
                Title = "Big album", Slug = $"e-{id:N}", Status = CampaignStatus.Dispatched,
                EventStartAt = DateTimeOffset.UtcNow.AddDays(-1), CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                EventPass = pass, EventPassUntil = pass == EventPassKind.None ? null : passUntil ?? DateTimeOffset.UtcNow.AddMonths(11),
            });
            await Task.CompletedTask;
        });
        return id;
    }

    private async Task GuestAsync(Guid ev, string email) =>
        await api.WriteAsync(async db =>
        {
            db.Guests.Add(new Guest { Id = Guid.NewGuid(), CampaignId = ev, Name = "Guest", Email = email, CreatedAt = DateTimeOffset.UtcNow });
            await Task.CompletedTask;
        });
}
