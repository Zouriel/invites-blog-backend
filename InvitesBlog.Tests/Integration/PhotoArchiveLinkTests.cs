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
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var stored = new List<(Guid Id, byte[] Bytes)>();
        for (var i = 0; i < 3; i++) stored.Add(await PhotoAsync(ev, i));
        // The three real ones among 1,500 ids, like an app naming a whole album.
        var ids = stored.Select(s => s.Id).Concat(Enumerable.Range(0, 1497).Select(_ => Guid.NewGuid())).ToArray();

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
        var ev = await EventAsync(host);
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

    private async Task<Person> UserAsync()
    {
        var id = Guid.NewGuid();
        var mail = $"a-{id:N}@example.com";
        await api.WriteAsync(async db =>
        {
            var roleId = await db.Roles.Where(r => r.Name == Roles.Customer).Select(r => r.Id).SingleAsync();
            var user = new AppUser { Id = id, Email = mail, DisplayName = "Person", IsActive = true, CreatedAt = DateTimeOffset.UtcNow };
            user.UserRoles.Add(new UserRole { RoleId = roleId });
            db.Users.Add(user);
        });
        return new Person(id, mail, api.TokenFor(id, "email", mail, Roles.Customer));
    }

    private async Task<Guid> EventAsync(Person creator)
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
