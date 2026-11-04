using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Moderation;
using InvitesBlog.Application.Push;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using InvitesBlog.Infrastructure.Notifications;
using InvitesBlog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InvitesBlog.Tests.Integration;

[CollectionDefinition("favourites")]
public sealed class FavouritesCollection : ICollectionFixture<PostgresApiFixture>;

/// <summary>
/// Hearts, through the real API on real Postgres: who may heart what, what the counts say, what stays
/// in someone's Favourites, and who is told (and how often).
/// </summary>
[Collection("favourites")]
public sealed class FavouritesTests(PostgresApiFixture api)
{
    private sealed record Person(Guid Id, string Email, string Name, string Token);

    [PostgresFact]
    public async Task Hearting_is_idempotent_and_every_album_read_shows_the_count_and_whether_it_is_mine()
    {
        var host = await UserAsync("Host");
        var aisha = await UserAsync("Aisha");
        var ev = await EventAsync(host);
        await InviteAsync(ev, await GuestAsync(ev, "Aisha", aisha.Email), InviteStatus.Sent);
        var photo = await PhotoAsync(ev, null, null);

        var first = await DataAsync(await api.Client(aisha.Token).PostAsync($"/api/me/favourites/{photo}", null));
        Assert.Equal(1, first.GetProperty("favouriteCount").GetInt32());
        Assert.True(first.GetProperty("favourited").GetBoolean());
        var again = await DataAsync(await api.Client(aisha.Token).PostAsync($"/api/me/favourites/{photo}", null));
        Assert.Equal(1, again.GetProperty("favouriteCount").GetInt32());
        Assert.Equal(1, await api.ReadAsync(db => db.Favourites.CountAsync(f => f.PhotoId == photo)));

        var asHost = await TileAsync(host, $"/api/campaigns/{ev}/photos", photo);
        Assert.Equal(1, asHost.GetProperty("favouriteCount").GetInt32());
        Assert.False(asHost.GetProperty("favourited").GetBoolean());
        var asGuest = await TileAsync(aisha, $"/api/me/invitations/{ev}/photos", photo);
        Assert.Equal(1, asGuest.GetProperty("favouriteCount").GetInt32());
        Assert.True(asGuest.GetProperty("favourited").GetBoolean());

        var gone = await DataAsync(await api.Client(aisha.Token).DeleteAsync($"/api/me/favourites/{photo}"));
        Assert.Equal(0, gone.GetProperty("favouriteCount").GetInt32());
        Assert.False(gone.GetProperty("favourited").GetBoolean());
        var goneAgain = await DataAsync(await api.Client(aisha.Token).DeleteAsync($"/api/me/favourites/{photo}"));
        Assert.Equal(0, goneAgain.GetProperty("favouriteCount").GetInt32());
        Assert.Equal(0, (await TileAsync(host, $"/api/campaigns/{ev}/photos", photo)).GetProperty("favouriteCount").GetInt32());
    }

    [PostgresFact]
    public async Task Nobody_hearts_what_they_cannot_see()
    {
        var host = await UserAsync("Host");
        var stranger = await UserAsync("Stranger");
        var guest = await UserAsync("Guest");
        var ev = await EventAsync(host);
        await InviteAsync(ev, await GuestAsync(ev, "Guest", guest.Email), InviteStatus.Sent);
        var photo = await PhotoAsync(ev, null, null);

        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client(stranger.Token).PostAsync($"/api/me/favourites/{photo}", null)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Client(stranger.Token).PostAsync($"/api/me/favourites/{Guid.NewGuid()}", null)).StatusCode);
        Assert.Equal(0, (await ListAsync(stranger)).GetProperty("totalCount").GetInt32());

        // The guest of the event may (photos with no album of their own sit in the event's default one).
        Assert.Equal(HttpStatusCode.OK, (await api.Client(guest.Token).PostAsync($"/api/me/favourites/{photo}", null)).StatusCode);

        // An album the organiser restricted, with this guest left off it.
        var bucket = await BucketAsync(ev, host, restricted: true);
        var shut = await PhotoAsync(ev, null, null, bucket);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client(guest.Token).PostAsync($"/api/me/favourites/{shut}", null)).StatusCode);
    }

    [PostgresFact]
    public async Task Favourites_list_what_is_still_visible_with_its_album_and_pages()
    {
        var host = await UserAsync("Host");
        var aisha = await UserAsync("Aisha");
        var uploader = await UserAsync("Uploader");
        var ev = await EventAsync(host, "Maya's birthday");
        await InviteAsync(ev, await GuestAsync(ev, "Aisha", aisha.Email), InviteStatus.Sent);
        var a = await PhotoAsync(ev, null, null);
        var b = await PhotoAsync(ev, null, null);
        var c = await PhotoAsync(ev, null, uploader.Id);
        var d = await PhotoAsync(ev, null, null);
        foreach (var id in new[] { a, b, c, d })
            Assert.Equal(HttpStatusCode.OK, (await api.Client(aisha.Token).PostAsync($"/api/me/favourites/{id}", null)).StatusCode);

        var page = await DataAsync(await api.Client(aisha.Token).GetAsync("/api/me/favourites?page=1&pageSize=2"));
        Assert.Equal(4, page.GetProperty("totalCount").GetInt32());
        var items = page.GetProperty("items").EnumerateArray().ToList();
        Assert.Equal(2, items.Count);
        Assert.Equal(d, items[0].GetProperty("id").GetGuid());   // newest heart first
        Assert.Equal("Maya's birthday", items[0].GetProperty("albumTitle").GetString());
        Assert.Equal(ev, items[0].GetProperty("campaignId").GetGuid());
        Assert.True(items[0].GetProperty("favourited").GetBoolean());
        Assert.False(items[0].GetProperty("canDelete").GetBoolean());

        // Deleted media leaves the list and the count; so does everything from someone she blocked.
        await api.WriteAsync(async db =>
            await db.EventPhotos.Where(p => p.Id == a).ExecuteUpdateAsync(s => s.SetProperty(p => p.DeletedAt, DateTimeOffset.UtcNow)));
        await api.Client(aisha.Token).PutAsync($"/api/me/blocks/{uploader.Id}", null);
        var left = await IdsAsync(aisha);
        Assert.Equal([d, b], left);

        // Taking the heart back from something she can no longer see still works.
        Assert.Equal(HttpStatusCode.OK, (await api.Client(aisha.Token).DeleteAsync($"/api/me/favourites/{c}")).StatusCode);
    }

    [PostgresFact]
    public async Task Removing_an_account_takes_its_hearts_out_of_the_counts()
    {
        var host = await UserAsync("Host");
        var aisha = await UserAsync("Aisha");
        var ev = await EventAsync(host);
        await InviteAsync(ev, await GuestAsync(ev, "Aisha", aisha.Email), InviteStatus.Sent);
        var photo = await PhotoAsync(ev, null, null);
        await api.Client(aisha.Token).PostAsync($"/api/me/favourites/{photo}", null);
        await api.Client(host.Token).PostAsync($"/api/me/favourites/{photo}", null);
        Assert.Equal(2, (await TileAsync(host, $"/api/campaigns/{ev}/photos", photo)).GetProperty("favouriteCount").GetInt32());

        await api.WriteAsync(async db =>
            await db.Users.Where(u => u.Id == aisha.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.IsActive, false)));
        Assert.Equal(1, (await TileAsync(host, $"/api/campaigns/{ev}/photos", photo)).GetProperty("favouriteCount").GetInt32());

        await api.WriteAsync(async db => await db.Users.Where(u => u.Id == aisha.Id).ExecuteDeleteAsync());
        Assert.Equal(0, await api.ReadAsync(db => db.Favourites.CountAsync(f => f.UserId == aisha.Id)));
    }

    [PostgresFact]
    public async Task A_download_link_carries_only_her_own_visible_favourites()
    {
        var host = await UserAsync("Host");
        var aisha = await UserAsync("Aisha");
        var ev = await EventAsync(host);
        await InviteAsync(ev, await GuestAsync(ev, "Aisha", aisha.Email), InviteStatus.Sent);
        var kept = await PhotoAsync(ev, null, null);
        var notKept = await PhotoAsync(ev, null, null);
        await api.Client(aisha.Token).PostAsync($"/api/me/favourites/{kept}", null);

        var link = await DataAsync(await api.Client(aisha.Token).PostAsJsonAsync("/api/me/favourites/download-link",
            new { ids = new[] { kept, notKept, Guid.NewGuid() } }));
        Assert.Equal(1, link.GetProperty("count").GetInt32());
        Assert.StartsWith("/api/photos/archives/", link.GetProperty("url").GetString());

        var none = await api.Client(aisha.Token).PostAsJsonAsync("/api/me/favourites/download-link", new { ids = new[] { notKept } });
        Assert.Equal(HttpStatusCode.BadRequest, none.StatusCode);
    }

    [PostgresFact]
    public async Task The_album_s_people_hear_once_per_quiet_period_never_about_their_own_or_blocked_hearts()
    {
        var host = await UserAsync("Host");
        var celebrant = await UserAsync("Maya");
        var aisha = await UserAsync("Aisha");
        var pest = await UserAsync("Pest");
        var ev = await EventAsync(host, "Maya's birthday");
        await CelebrantAsync(ev, celebrant.Email);
        await InviteAsync(ev, await GuestAsync(ev, "Aisha", aisha.Email), InviteStatus.Sent);
        await InviteAsync(ev, await GuestAsync(ev, "Pest", pest.Email), InviteStatus.Sent);
        var bucket = await BucketAsync(ev, host, restricted: false);
        var p1 = await PhotoAsync(ev, null, null, bucket);
        var p2 = await PhotoAsync(ev, null, null, bucket);
        await api.Client(host.Token).PutAsync($"/api/me/blocks/{pest.Id}", null);

        foreach (var (who, photo) in new[] { (pest, p1), (aisha, p1), (aisha, p2), (host, p2) })
            Assert.Equal(HttpStatusCode.OK, (await api.Client(who.Token).PostAsync($"/api/me/favourites/{photo}", null)).StatusCode);

        var now = DateTimeOffset.UtcNow.AddSeconds(1);
        var sent = await SweepAsync(now);
        // The host: Aisha only (Pest is blocked, their own heart doesn't count), two photos.
        var toHost = Assert.Single(sent, s => s.User == host.Id);
        Assert.Equal("Aisha loved 2 photos.", toHost.Body);
        Assert.Equal("Maya's birthday", toHost.Title);
        Assert.Equal($"/buckets/{bucket}", toHost.Path);
        // Maya hears about everyone, newest first.
        var toMaya = Assert.Single(sent, s => s.User == celebrant.Id);
        Assert.Equal("Host and 2 others loved 2 photos.", toMaya.Body);

        // Nothing new: nothing sent. Something new inside the quiet period waits for it to pass.
        Assert.DoesNotContain(await SweepAsync(now.AddMinutes(1)), s => s.Path == $"/buckets/{bucket}");
        var p3 = await PhotoAsync(ev, null, null, bucket);
        await api.Client(aisha.Token).PostAsync($"/api/me/favourites/{p3}", null);
        Assert.DoesNotContain(await SweepAsync(now.AddMinutes(5)), s => s.Path == $"/buckets/{bucket}");
        var later = await SweepAsync(DateTimeOffset.UtcNow.AddMinutes(16));
        Assert.Equal("Aisha loved a photo.", Assert.Single(later, s => s.User == host.Id).Body);
        Assert.Equal(2, later.Count(s => s.Path == $"/buckets/{bucket}"));
    }

    // ----- helpers ------------------------------------------------------------------------------------

    private sealed record Sent(Guid User, string Title, string Body, string Path);

    private sealed class RecordingSender : IPushSender
    {
        public List<Sent> Sent { get; } = [];
        public Task SendAsync(Guid userId, string title, string body, IReadOnlyDictionary<string, string>? data, CancellationToken ct)
        {
            Sent.Add(new Sent(userId, title, body, data?["path"] ?? ""));
            return Task.CompletedTask;
        }
    }

    private async Task<List<Sent>> SweepAsync(DateTimeOffset now)
    {
        using var scope = api.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var sender = new RecordingSender();
        await FavouriteNoticeService.SweepAsync(sp.GetRequiredService<AppDbContext>(),
            sp.GetRequiredService<PushNotificationService>(), sender,
            sp.GetService<IModerationViewService>(), now, CancellationToken.None);
        return sender.Sent;
    }

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("data");
    }

    private async Task<JsonElement> TileAsync(Person who, string route, Guid photo)
    {
        var box = await DataAsync(await api.Client(who.Token).GetAsync(route));
        return box.GetProperty("photos").EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == photo);
    }

    private async Task<JsonElement> ListAsync(Person who) =>
        await DataAsync(await api.Client(who.Token).GetAsync("/api/me/favourites"));

    private async Task<List<Guid>> IdsAsync(Person who) =>
        (await ListAsync(who)).GetProperty("items").EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ToList();

    private async Task<Guid> BucketAsync(Guid ev, Person owner, bool restricted)
    {
        var id = Guid.NewGuid();
        await api.WriteAsync(async db =>
        {
            db.MediaBuckets.Add(new MediaBucket
            {
                Id = id, OwnerUserId = owner.Id, CampaignId = ev, EventDate = DateTimeOffset.UtcNow.AddDays(10),
                IsRestricted = restricted, CapacityBytes = 1_000_000, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await Task.CompletedTask;
        });
        return id;
    }

    private async Task<Guid> PhotoAsync(Guid ev, Guid? guest, Guid? uploader, Guid? bucket = null)
    {
        var id = Guid.NewGuid();
        await api.WriteAsync(async db =>
        {
            db.EventPhotos.Add(new EventPhoto
            {
                Id = id, CampaignId = ev, BucketId = bucket, GuestId = guest, UploaderUserId = uploader, UploaderName = "Guest",
                OriginalUrl = $"http://localhost/assets/campaigns/{id:N}.jpg", Url = $"http://localhost/assets/campaigns/{id:N}_v.jpg",
                ThumbUrl = $"http://localhost/assets/campaigns/{id:N}_t.jpg", ContentType = "image/jpeg",
                SizeBytes = 10, Width = 10, Height = 10, CreatedAt = DateTimeOffset.UtcNow,
            });
            await Task.CompletedTask;
        });
        return id;
    }

    private async Task<Person> UserAsync(string name, string role = Roles.Customer)
    {
        var id = Guid.NewGuid();
        var mail = $"m-{id:N}@example.com";
        await api.WriteAsync(async db =>
        {
            var roleId = await db.Roles.Where(r => r.Name == role).Select(r => r.Id).SingleAsync();
            var user = new AppUser { Id = id, Email = mail, DisplayName = name, IsActive = true, CreatedAt = DateTimeOffset.UtcNow };
            user.UserRoles.Add(new UserRole { RoleId = roleId });
            db.Users.Add(user);
        });
        return new Person(id, mail, name, api.TokenFor(id, "email", mail, role));
    }

    private async Task<Guid> EventAsync(Person creator, string title = "An event")
    {
        var id = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        await api.WriteAsync(async db =>
        {
            db.Templates.Add(new Template
            {
                Id = templateId, Name = title, Slug = $"own-{id:N}", Version = "1.0.0", Category = "Imported", Description = "D", PreviewImageUrl = "",
                SceneJson = "{}", ManifestJson = "{}", PackageUrl = "", Visibility = TemplateVisibility.Imported,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.Campaigns.Add(new Campaign
            {
                Id = id, TemplateId = templateId, TemplateVersion = "1.0.0", TemplatePackageUrl = "",
                CreatedByUserId = creator.Id, AccessTokenHash = Guid.NewGuid().ToString("N"),
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

    private async Task InviteAsync(Guid ev, Guid guest, InviteStatus status) =>
        await api.WriteAsync(async db =>
        {
            var id = Guid.NewGuid();
            db.Invites.Add(new Invite
            {
                Id = id, CampaignId = ev, GuestId = guest, Status = status,
                TokenHash = InvitesBlog.Application.Security.TokenService.Hash(id.ToString("N")), CreatedAt = DateTimeOffset.UtcNow,
            });
            await Task.CompletedTask;
        });
}
