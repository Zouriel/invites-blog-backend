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
            $"/api/media-buckets/{bucket}/face-grouping", new { on = true, guestsTold = true, outsideRestrictedRegions = true })).StatusCode);

        // Not for events in Illinois, Texas or Washington: the host must say this one isn't.
        var region = await api.Client(host.Token).PutAsJsonAsync($"/api/media-buckets/{bucket}/face-grouping", new { on = true, guestsTold = true });
        Assert.Equal(HttpStatusCode.BadRequest, region.StatusCode);
        Assert.Contains("Illinois", await region.Content.ReadAsStringAsync());

        var on = await DataAsync(await api.Client(host.Token).PutAsJsonAsync($"/api/media-buckets/{bucket}/face-grouping", new { on = true, guestsTold = true, outsideRestrictedRegions = true }));
        // Recorded, with no face data.
        var record = (await api.ReadAsync(db => db.AuditLogs.Where(a => a.Action == "faces.on").ToListAsync())).Single(a => a.DataJson.Contains(bucket.ToString()));
        Assert.Equal(host.Id.ToString(), record.Actor);
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
        // Both groups are listed (the People tab names them); the one in two photos first.
        Assert.Equal(2, people.Count);
        var one = people[0];
        Assert.Equal(2, one.GetProperty("photoCount").GetInt32());
        Assert.Equal(1, people[1].GetProperty("photoCount").GetInt32());
        var personId = one.GetProperty("id").GetGuid();
        foreach (var id in new[] { p1, p2 })
            Assert.Contains(personId, Tile(asHost, id).GetProperty("people").EnumerateArray().Select(x => x.GetGuid()));

        var asGuest = await DataAsync(await api.Client(guest.Token).GetAsync($"/api/me/invitations/{ev}/photos"));
        Assert.False(asGuest.GetProperty("faces").GetProperty("canName").GetBoolean());
        Assert.Equal(2, asGuest.GetProperty("faces").GetProperty("people").GetArrayLength());

        // A QR code onto the album says so too (the app's camera shows the notice from this).
        var qr = await DataAsync(await api.Client(host.Token).PostAsJsonAsync($"/api/media-buckets/{bucket}/qr", new { label = "Table", allowAnonymous = true }));
        var token = qr.GetProperty("url").GetString()!.Split("/q/")[1];
        Assert.True((await DataAsync(await api.Client().GetAsync($"/api/q/{token}"))).GetProperty("faces").GetBoolean());

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
        var optOut = (await api.ReadAsync(db => db.AuditLogs.Where(a => a.Action == "faces.opt_out").ToListAsync())).Single(a => a.DataJson.Contains(bucket.ToString()));
        Assert.Equal(guest.Id.ToString(), optOut.Actor);
        Assert.DoesNotContain("embedding", optOut.DataJson, StringComparison.OrdinalIgnoreCase);
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
    public async Task The_host_says_two_groups_are_one_person_by_picking_or_by_typing_the_same_name()
    {
        var (host, guest, ev, bucket) = await SetUpAsync();
        for (var i = 0; i < 3; i++) await PhotoAsync(ev, bucket);
        await SwitchOnAsync(host, bucket);
        // The same person, seen three times but too differently to be grouped at first (three groups).
        api.Faces.Next = [FakeFaceAnalyzer.Face(10)];
        await RunWorkerAsync();
        api.Faces.Next = [FakeFaceAnalyzer.Face(20)];
        await RunWorkerAsync();
        api.Faces.Next = [FakeFaceAnalyzer.Face(30)];
        await RunWorkerAsync();
        var groups = await api.ReadAsync(db => db.FacePeople.Where(p => p.BucketId == bucket).OrderBy(p => p.CreatedAt).Select(p => p.Id).ToListAsync());
        Assert.Equal(3, groups.Count);

        await DataAsync(await api.Client(host.Token).PutAsJsonAsync($"/api/media-buckets/{bucket}/people/{groups[0]}", new { name = "Shanna" }));
        // A guest can't merge.
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client(guest.Token).PostAsJsonAsync(
            $"/api/campaigns/{ev}/photos/people/{groups[1]}/merge", new { into = groups[0] })).StatusCode);
        // Picking "Shanna" for the second group.
        var merged = await DataAsync(await api.Client(host.Token).PostAsJsonAsync(
            $"/api/media-buckets/{bucket}/people/{groups[1]}/merge", new { into = groups[0] }));
        Assert.Equal(groups[0], merged.GetProperty("id").GetGuid());
        Assert.Equal(2, merged.GetProperty("photoCount").GetInt32());
        // Typing "shanna" for the third merges it too.
        var typed = await DataAsync(await api.Client(host.Token).PutAsJsonAsync($"/api/campaigns/{ev}/photos/people/{groups[2]}", new { name = "shanna" }));
        Assert.Equal(groups[0], typed.GetProperty("id").GetGuid());
        Assert.Equal("Shanna", typed.GetProperty("name").GetString());
        Assert.Equal(3, typed.GetProperty("photoCount").GetInt32());

        var left = await api.ReadAsync(db => db.FacePeople.Where(p => p.BucketId == bucket).ToListAsync());
        var shanna = Assert.Single(left);
        Assert.Equal(3, shanna.FaceCount);
        // The average now sits between the three, so a new photo like any of them joins Shanna.
        Assert.All(new[] { 10, 20, 30 }, a => Assert.True(
            InvitesBlog.Application.Services.Faces.FaceClustering.Cosine(FakeFaceAnalyzer.Person(a), shanna.Centroid)
            >= InvitesBlog.Application.Services.Faces.FaceClustering.DefaultThreshold));
        var box = await DataAsync(await api.Client(host.Token).GetAsync($"/api/media-buckets/{bucket}/media"));
        Assert.All(box.GetProperty("photos").EnumerateArray(), p => Assert.Equal(groups[0], p.GetProperty("people")[0].GetGuid()));
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
    public async Task Albums_idle_for_the_retention_limit_lose_their_face_data_and_busy_ones_keep_it()
    {
        var (host, _, ev, idle) = await SetUpAsync();
        await PhotoAsync(ev, idle);
        await SwitchOnAsync(host, idle);
        api.Faces.Next = [FakeFaceAnalyzer.Face(7)];
        await RunWorkerAsync();
        var (host2, _, ev2, busy) = await SetUpAsync();
        await PhotoAsync(ev2, busy);
        await SwitchOnAsync(host2, busy);
        api.Faces.Next = [FakeFaceAnalyzer.Face(8)];
        await RunWorkerAsync();

        // The first album: nothing new for 25 months. The second: a photo last week.
        var old = DateTimeOffset.UtcNow.AddDays(-760);
        await api.WriteAsync(async db =>
        {
            await db.MediaBuckets.Where(b => b.Id == idle || b.Id == busy).ExecuteUpdateAsync(x => x.SetProperty(b => b.UpdatedAt, old));
            await db.EventPhotos.Where(p => p.BucketId == idle).ExecuteUpdateAsync(x => x.SetProperty(p => p.CreatedAt, old));
            await db.EventPhotos.Where(p => p.BucketId == busy).ExecuteUpdateAsync(x => x.SetProperty(p => p.CreatedAt, DateTimeOffset.UtcNow.AddDays(-7)));
        });

        var faces = api.Services.CreateScope().ServiceProvider.GetRequiredService<InvitesBlog.Application.Services.Faces.IFaceGroupingService>();
        Assert.True(await faces.ExpireIdleAsync(TimeSpan.FromDays(730), CancellationToken.None) >= 1);

        Assert.False(await api.ReadAsync(db => db.MediaBuckets.Where(b => b.Id == idle).Select(b => b.FaceGroupingOn).SingleAsync()));
        Assert.False(await api.ReadAsync(db => db.PhotoFaces.AnyAsync(f => f.BucketId == idle)));
        Assert.False(await api.ReadAsync(db => db.FacePeople.AnyAsync(f => f.BucketId == idle)));
        Assert.Contains(await api.ReadAsync(db => db.AuditLogs.Where(a => a.Action == "faces.expired").ToListAsync()), a => a.DataJson.Contains(idle.ToString()));
        Assert.True(await api.ReadAsync(db => db.MediaBuckets.Where(b => b.Id == busy).Select(b => b.FaceGroupingOn).SingleAsync()));
        Assert.True(await api.ReadAsync(db => db.PhotoFaces.AnyAsync(f => f.BucketId == busy)));
    }

    [PostgresFact]
    public async Task Free_events_cannot_switch_it_on_and_an_event_whose_pass_ends_loses_it_and_its_face_data()
    {
        var (freeHost, _, _, freeBucket) = await SetUpAsync(paid: false);
        var refused = await api.Client(freeHost.Token).PutAsJsonAsync($"/api/media-buckets/{freeBucket}/face-grouping", new { on = true, guestsTold = true, outsideRestrictedRegions = true });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("any pass or Premium", await refused.Content.ReadAsStringAsync());
        var free = await DataAsync(await api.Client(freeHost.Token).GetAsync($"/api/media-buckets/{freeBucket}"));
        Assert.False(free.GetProperty("faceGroupingIncluded").GetBoolean());

        var (host, _, ev, bucket) = await SetUpAsync();
        Assert.True((await DataAsync(await api.Client(host.Token).GetAsync($"/api/media-buckets/{bucket}"))).GetProperty("faceGroupingIncluded").GetBoolean());
        await PhotoAsync(ev, bucket);
        await SwitchOnAsync(host, bucket);
        api.Faces.Next = [FakeFaceAnalyzer.Face(11)];
        await RunWorkerAsync();
        Assert.True(await api.ReadAsync(db => db.PhotoFaces.AnyAsync(f => f.BucketId == bucket)));

        // The pass ends: the next sweep switches grouping off and deletes the faces.
        await api.WriteAsync(async db =>
        {
            await db.Campaigns.Where(c => c.Id == ev).ExecuteUpdateAsync(x => x.SetProperty(c => c.EventPassUntil, DateTimeOffset.UtcNow.AddDays(-1)));
        });
        var faces = api.Services.CreateScope().ServiceProvider.GetRequiredService<InvitesBlog.Application.Services.Faces.IFaceGroupingService>();
        Assert.True(await faces.ExpireUnpaidAsync(CancellationToken.None) >= 1);
        Assert.False(await api.ReadAsync(db => db.MediaBuckets.Where(b => b.Id == bucket).Select(b => b.FaceGroupingOn).SingleAsync()));
        Assert.False(await api.ReadAsync(db => db.PhotoFaces.AnyAsync(f => f.BucketId == bucket)));
        Assert.False(await api.ReadAsync(db => db.FacePeople.AnyAsync(f => f.BucketId == bucket)));
        Assert.Contains(await api.ReadAsync(db => db.AuditLogs.Where(a => a.Action == "faces.plan_ended").ToListAsync()), a => a.DataJson.Contains(bucket.ToString()));
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
        await DataAsync(await api.Client(host.Token).PutAsJsonAsync($"/api/media-buckets/{bucket}/face-grouping", new { on = true, guestsTold = true, outsideRestrictedRegions = true }));

    private async Task<(Person Host, Person Guest, Guid Event, Guid Bucket)> SetUpAsync(bool paid = true)
    {
        api.Faces.Next = [];
        var host = await UserAsync("Host");
        var guest = await UserAsync("Guest");
        var ev = await EventAsync(host, paid);
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

    private async Task<Guid> EventAsync(Person creator, bool paid = true)
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
                // Face grouping comes with any pass or Premium: these events have a Gold pass unless a test says otherwise.
                EventPass = paid ? EventPassKind.Gold : EventPassKind.None,
                EventPassUntil = paid ? DateTimeOffset.UtcNow.AddMonths(12) : null,
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
