using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using InvitesBlog.Infrastructure.Faces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InvitesBlog.Tests.Integration;

[CollectionDefinition("faces")]
public sealed class FacesCollection : ICollectionFixture<PostgresApiFixture>;

/// <summary>
/// Face grouping through the real API, worker and migrations on real Postgres (a stand-in analyzer gives the
/// faces): switching on needs the host's "guests are told", the People filter, naming, "that's me" opt-out,
/// and every way face data must disappear.
/// </summary>
[Collection("faces")]
public sealed class FaceGroupingTests(PostgresApiFixture api)
{
    private sealed record Person(Guid Id, string Email, string Name, string Token);

    [PostgresFact]
    public async Task Switching_on_needs_guests_told_then_the_worker_groups_faces_and_both_host_and_guest_see_people()
    {
        var (host, guest, ev, bucket) = await SetUpAsync();
        var p1 = await PhotoAsync(ev, bucket);
        var p2 = await PhotoAsync(ev, bucket);
        var video = await PhotoAsync(ev, bucket, "video/mp4");

        var refused = await api.Client(host.Token).PutAsJsonAsync($"/api/media-buckets/{bucket}/face-grouping", new { on = true, guestsTold = false });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("Tick the box", await refused.Content.ReadAsStringAsync());
        // Only the host switches it.
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client(guest.Token).PutAsJsonAsync(
            $"/api/media-buckets/{bucket}/face-grouping", new { on = true, guestsTold = true })).StatusCode);

        var on = await DataAsync(await api.Client(host.Token).PutAsJsonAsync($"/api/media-buckets/{bucket}/face-grouping", new { on = true, guestsTold = true }));
        Assert.True(on.GetProperty("faceGrouping").GetBoolean());
        Assert.Equal(2, await api.ReadAsync(db => db.FaceJobs.CountAsync(j => j.BucketId == bucket)));
        Assert.False(await api.ReadAsync(db => db.FaceJobs.AnyAsync(j => j.Id == video)));

        // Two photos of person 1; person 2 only in the second.
        api.Faces.Next = [FakeFaceAnalyzer.Face(1)];
        await RunWorkerAsync();
        api.Faces.Next = [FakeFaceAnalyzer.Face(1, 0.2f, 0.1f), FakeFaceAnalyzer.Face(2, 0.6f)];
        await RunWorkerAsync();
        Assert.Equal(0, await api.ReadAsync(db => db.FaceJobs.CountAsync(j => j.BucketId == bucket)));

        var asHost = await DataAsync(await api.Client(host.Token).GetAsync($"/api/media-buckets/{bucket}/media"));
        var faces = asHost.GetProperty("faces");
        Assert.True(faces.GetProperty("canName").GetBoolean());
        var people = faces.GetProperty("people").EnumerateArray().ToList();
        // Person 2 is in one photo and unnamed: not offered as a filter yet.
        var one = Assert.Single(people);
        Assert.Equal(2, one.GetProperty("photoCount").GetInt32());
        var personId = one.GetProperty("id").GetGuid();
        foreach (var id in new[] { p1, p2 })
            Assert.Contains(personId, Tile(asHost, id).GetProperty("people").EnumerateArray().Select(x => x.GetGuid()));

        var asGuest = await DataAsync(await api.Client(guest.Token).GetAsync($"/api/me/invitations/{ev}/photos"));
        Assert.False(asGuest.GetProperty("faces").GetProperty("canName").GetBoolean());
        Assert.Single(asGuest.GetProperty("faces").GetProperty("people").EnumerateArray());

        // The camera says so before the first photo.
        var camera = await api.ReadAsync(db => db.MediaBuckets.SingleAsync(b => b.Id == bucket));
        Assert.True(camera.FaceGroupingOn);
        Assert.Equal(host.Id, camera.FaceGroupingConfirmedBy);
    }

    [PostgresFact]
    public async Task Only_the_host_names_and_a_guest_can_opt_out_which_deletes_their_faces_and_keeps_new_ones_out()
    {
        var (host, guest, ev, bucket) = await SetUpAsync();
        await PhotoAsync(ev, bucket);
        await PhotoAsync(ev, bucket);
        await SwitchOnAsync(host, bucket);
        api.Faces.Next = [FakeFaceAnalyzer.Face(5)];
        await RunWorkerAsync();
        await RunWorkerAsync();
        var personId = await api.ReadAsync(db => db.FacePeople.Where(p => p.BucketId == bucket).Select(p => p.Id).SingleAsync());

        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client(guest.Token).PutAsJsonAsync(
            $"/api/media-buckets/{bucket}/people/{personId}", new { name = "Me" })).StatusCode);
        var named = await DataAsync(await api.Client(host.Token).PutAsJsonAsync(
            $"/api/campaigns/{ev}/photos/people/{personId}", new { name = "  Aisha  " }));
        Assert.Equal("Aisha", named.GetProperty("name").GetString());

        // A stranger can't reach it at all.
        var stranger = await UserAsync("Stranger");
        Assert.NotEqual(HttpStatusCode.NoContent, (await api.Client(stranger.Token).PostAsync(
            $"/api/me/invitations/{ev}/photos/people/{personId}/ignore", null)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await api.Client(guest.Token).PostAsync(
            $"/api/me/invitations/{ev}/photos/people/{personId}/ignore", null)).StatusCode);
        var ignored = await api.ReadAsync(db => db.FacePeople.SingleAsync(p => p.Id == personId));
        Assert.True(ignored.Ignored);
        Assert.Null(ignored.Name);
        Assert.Equal(0, await api.ReadAsync(db => db.PhotoFaces.CountAsync(f => f.PersonId == personId)));
        Assert.Empty((await DataAsync(await api.Client(host.Token).GetAsync($"/api/media-buckets/{bucket}/media")))
            .GetProperty("faces").GetProperty("people").EnumerateArray());

        // A new photo of them is not grouped again; someone else in it still is.
        var later = await PhotoAsync(ev, bucket);
        api.Faces.Next = [FakeFaceAnalyzer.Face(5, wobble: 0.05f), FakeFaceAnalyzer.Face(9, 0.7f)];
        await RunWorkerAsync();
        var kept = await api.ReadAsync(db => db.PhotoFaces.Where(f => f.PhotoId == later).ToListAsync());
        Assert.Single(kept);
        Assert.NotEqual(personId, kept[0].PersonId);
    }

    [PostgresFact]
    public async Task Face_data_goes_with_the_photo_the_switch_and_the_album()
    {
        var (host, _, ev, bucket) = await SetUpAsync();
        var p1 = await PhotoAsync(ev, bucket);
        var p2 = await PhotoAsync(ev, bucket);
        await SwitchOnAsync(host, bucket);
        api.Faces.Next = [FakeFaceAnalyzer.Face(3)];
        await RunWorkerAsync();
        await RunWorkerAsync();
        Assert.Equal(2, await api.ReadAsync(db => db.PhotoFaces.CountAsync(f => f.BucketId == bucket)));

        // Removing a photo removes its faces.
        Assert.True((await api.Client(host.Token).DeleteAsync($"/api/media-buckets/{bucket}/media/{p1}")).IsSuccessStatusCode);
        Assert.Equal(0, await api.ReadAsync(db => db.PhotoFaces.CountAsync(f => f.PhotoId == p1)));
        Assert.Equal(1, await api.ReadAsync(db => db.PhotoFaces.CountAsync(f => f.PhotoId == p2)));

        // Switching off removes everything; the box no longer mentions faces.
        var off = await DataAsync(await api.Client(host.Token).PutAsJsonAsync($"/api/media-buckets/{bucket}/face-grouping", new { on = false, guestsTold = false }));
        Assert.False(off.GetProperty("faceGrouping").GetBoolean());
        Assert.Equal(0, await api.ReadAsync(db => db.PhotoFaces.CountAsync(f => f.BucketId == bucket)));
        Assert.Equal(0, await api.ReadAsync(db => db.FacePeople.CountAsync(f => f.BucketId == bucket)));
        var box = await DataAsync(await api.Client(host.Token).GetAsync($"/api/media-buckets/{bucket}/media"));
        Assert.Equal(JsonValueKind.Null, box.GetProperty("faces").ValueKind);

        // On again, then the album removed outright (account deletion does that): the database cascades.
        await SwitchOnAsync(host, bucket);
        await RunWorkerAsync();
        Assert.True(await api.ReadAsync(db => db.PhotoFaces.AnyAsync(f => f.BucketId == bucket)));
        await api.WriteAsync(async db =>
        {
            await db.EventPhotos.Where(p => p.BucketId == bucket).ExecuteDeleteAsync();
            await db.MediaBuckets.Where(b => b.Id == bucket).ExecuteDeleteAsync();
        });
        Assert.False(await api.ReadAsync(db => db.PhotoFaces.AnyAsync(f => f.BucketId == bucket)));
        Assert.False(await api.ReadAsync(db => db.FacePeople.AnyAsync(f => f.BucketId == bucket)));
        Assert.False(await api.ReadAsync(db => db.FaceJobs.AnyAsync(f => f.BucketId == bucket)));
    }

    [PostgresFact]
    public async Task A_photo_removed_while_it_is_being_read_leaves_no_faces()
    {
        var (host, _, ev, bucket) = await SetUpAsync();
        var p1 = await PhotoAsync(ev, bucket);
        await SwitchOnAsync(host, bucket);
        await api.WriteAsync(async db =>
        {
            await db.EventPhotos.Where(p => p.Id == p1).ExecuteUpdateAsync(s => s.SetProperty(p => p.DeletedAt, DateTimeOffset.UtcNow));
        });
        api.Faces.Next = [FakeFaceAnalyzer.Face(4)];
        await RunWorkerAsync();
        Assert.False(await api.ReadAsync(db => db.PhotoFaces.AnyAsync(f => f.PhotoId == p1)));
        Assert.False(await api.ReadAsync(db => db.FaceJobs.AnyAsync(j => j.Id == p1)));
    }

    // ---------- helpers ----------

    private async Task RunWorkerAsync() =>
        await api.Services.GetRequiredService<FaceIndexService>().RunOnceAsync(CancellationToken.None);

    private async Task SwitchOnAsync(Person host, Guid bucket) =>
        await DataAsync(await api.Client(host.Token).PutAsJsonAsync($"/api/media-buckets/{bucket}/face-grouping", new { on = true, guestsTold = true }));

    private async Task<(Person Host, Person Guest, Guid Event, Guid Bucket)> SetUpAsync()
    {
        api.Faces.Next = [];
        var host = await UserAsync("Host");
        var guest = await UserAsync("Guest");
        var ev = await EventAsync(host);
        await InviteAsync(ev, await GuestAsync(ev, "Guest", guest.Email));
        var bucket = Guid.NewGuid();
        await api.WriteAsync(async db =>
        {
            db.MediaBuckets.Add(new MediaBucket
            {
                Id = bucket, OwnerUserId = host.Id, CampaignId = ev, EventDate = DateTimeOffset.UtcNow.AddDays(10),
                CapacityBytes = 1_000_000, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await Task.CompletedTask;
        });
        return (host, guest, ev, bucket);
    }

    private static JsonElement Tile(JsonElement box, Guid photo) =>
        box.GetProperty("photos").EnumerateArray().Single(p => p.GetProperty("id").GetGuid() == photo);

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("data");
    }

    private async Task<Guid> PhotoAsync(Guid ev, Guid bucket, string type = "image/jpeg")
    {
        var id = Guid.NewGuid();
        var url = $"http://localhost/assets/buckets/{bucket:N}/media/{id:N}_v.jpg";
        var storage = api.Services.GetRequiredService<IStorageService>();
        await storage.PutAsync(storage.KeyFor(url)!, [0xFF, 0xD8, 0xFF], "image/jpeg");
        await api.WriteAsync(async db =>
        {
            db.EventPhotos.Add(new EventPhoto
            {
                Id = id, CampaignId = ev, BucketId = bucket, UploaderName = "Guest",
                OriginalUrl = url, Url = url, ThumbUrl = url.Replace("_v.jpg", "_t.jpg"), ContentType = type,
                SizeBytes = 10, Width = 10, Height = 10, CreatedAt = DateTimeOffset.UtcNow,
            });
            if (type.StartsWith("image/"))
            {
                var on = await db.MediaBuckets.AnyAsync(b => b.Id == bucket && b.FaceGroupingOn);
                if (on) db.FaceJobs.Add(new FaceJob { Id = id, BucketId = bucket, NextAttemptAt = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            }
        });
        return id;
    }

    private async Task<Person> UserAsync(string name, string role = Roles.Customer)
    {
        var id = Guid.NewGuid();
        var mail = $"f-{id:N}@example.com";
        await api.WriteAsync(async db =>
        {
            var roleId = await db.Roles.Where(r => r.Name == role).Select(r => r.Id).SingleAsync();
            var user = new AppUser { Id = id, Email = mail, DisplayName = name, IsActive = true, CreatedAt = DateTimeOffset.UtcNow };
            user.UserRoles.Add(new UserRole { RoleId = roleId });
            db.Users.Add(user);
        });
        return new Person(id, mail, name, api.TokenFor(id, "email", mail, role));
    }

    private async Task<Guid> EventAsync(Person creator)
    {
        var id = Guid.NewGuid();
        var templateId = Guid.NewGuid();
        await api.WriteAsync(async db =>
        {
            db.Templates.Add(new Template
            {
                Id = templateId, Name = "Faces", Slug = $"own-{id:N}", Version = "1.0.0", Category = "Imported", Description = "D", PreviewImageUrl = "",
                SceneJson = "{}", ManifestJson = "{}", PackageUrl = "", Visibility = TemplateVisibility.Imported,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.Campaigns.Add(new Campaign
            {
                Id = id, TemplateId = templateId, TemplateVersion = "1.0.0", TemplatePackageUrl = "",
                CreatedByUserId = creator.Id, AccessTokenHash = Guid.NewGuid().ToString("N"),
                Title = "Faces", Slug = $"e-{id:N}", Status = CampaignStatus.Dispatched,
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

    private async Task InviteAsync(Guid ev, Guid guest) =>
        await api.WriteAsync(async db =>
        {
            var id = Guid.NewGuid();
            db.Invites.Add(new Invite
            {
                Id = id, CampaignId = ev, GuestId = guest, Status = InviteStatus.Sent,
                TokenHash = InvitesBlog.Application.Security.TokenService.Hash(id.ToString("N")), CreatedAt = DateTimeOffset.UtcNow,
            });
            await Task.CompletedTask;
        });
}
