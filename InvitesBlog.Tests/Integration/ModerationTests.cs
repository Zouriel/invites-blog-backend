using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InvitesBlog.Tests.Integration;

[CollectionDefinition("moderation")]
public sealed class ModerationCollection : ICollectionFixture<PostgresApiFixture>;

/// <summary>
/// Reporting, blocking and the word filter (App Store guideline 1.2), through the real API on real
/// Postgres: who stops seeing what, who is told, and what an admin can do about it.
/// </summary>
[Collection("moderation")]
public sealed class ModerationTests(PostgresApiFixture api)
{
    private sealed record Person(Guid Id, string Email, string Name, string Token);

    // ----- Reporting --------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Reporting_a_comment_hides_it_for_the_reporter_only_and_emails_the_owner_straight_away()
    {
        var host = await UserAsync("Host");
        var rude = await UserAsync("Rude guest");
        var kind = await UserAsync("Kind guest");
        var ev = await EventAsync(host, "Garden party");
        await InviteAsync(ev, await GuestAsync(ev, "Rude", rude.Email), InviteStatus.Sent);
        await InviteAsync(ev, await GuestAsync(ev, "Kind", kind.Email), InviteStatus.Sent);
        var comment = await CommentAsync(rude, ev, "What an ugly cake");

        var mailsBefore = api.Mail.Sent.Count(m => m.To == PostgresApiFixture.ReportInbox);
        var report = await api.Client(kind.Token).PostAsJsonAsync("/api/me/reports",
            new { kind = "comment", targetId = comment, reason = "abusive", note = "Mean to the host" });
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        Assert.True((await DataAsync(report)).GetProperty("hidden").GetBoolean());

        Assert.DoesNotContain(comment, await CommentIdsAsync(kind, ev));
        Assert.Contains(comment, await CommentIdsAsync(host, ev));
        Assert.Contains(comment, await CommentIdsAsync(rude, ev));

        var mail = Assert.Single(api.Mail.Sent.Where(m => m.To == PostgresApiFixture.ReportInbox).Skip(mailsBefore)
            .Where(m => m.Html.Contains("What an ugly cake")));
        Assert.Contains("abusive", mail.Subject);
        Assert.Contains("Mean to the host", mail.Html);
        Assert.Contains("/admin/reports", mail.Html);

        var row = await api.ReadAsync(db => db.ContentReports.SingleAsync(r => r.TargetId == comment));
        Assert.Equal((kind.Id, rude.Id, ev, "What an ugly cake"), (row.ReporterUserId, row.SubjectUserId!.Value, row.CampaignId!.Value, row.Snapshot));
    }

    [PostgresFact]
    public async Task Nobody_reports_their_own_comment_or_something_they_cannot_see()
    {
        var host = await UserAsync();
        var guest = await UserAsync();
        var stranger = await UserAsync();
        var ev = await EventAsync(host);
        await InviteAsync(ev, await GuestAsync(ev, "G", guest.Email), InviteStatus.Sent);
        var comment = await CommentAsync(guest, ev, "Lovely");

        var own = await api.Client(guest.Token).PostAsJsonAsync("/api/me/reports", new { kind = "comment", targetId = comment, reason = "spam" });
        Assert.Equal(HttpStatusCode.BadRequest, own.StatusCode);
        var unseen = await api.Client(stranger.Token).PostAsJsonAsync("/api/me/reports", new { kind = "comment", targetId = comment, reason = "spam" });
        Assert.Equal(HttpStatusCode.NotFound, unseen.StatusCode);
        var noReason = await api.Client(host.Token).PostAsJsonAsync("/api/me/reports", new { kind = "comment", targetId = comment });
        Assert.Equal(HttpStatusCode.BadRequest, noReason.StatusCode);
        var anonymous = await api.Client().PostAsJsonAsync("/api/me/reports", new { kind = "comment", targetId = comment, reason = "spam" });
        // The API's answer to anyone not signed in: "Sign in to continue."
        Assert.Equal(HttpStatusCode.Forbidden, anonymous.StatusCode);
        Assert.False(await api.ReadAsync(db => db.ContentReports.AnyAsync(r => r.TargetId == comment)));
    }

    [PostgresFact]
    public async Task A_reported_photo_leaves_the_reporter_s_album_and_an_admin_can_remove_it_for_everyone()
    {
        var host = await UserAsync("Host");
        var guest = await UserAsync("Guest");
        var celebrant = await UserAsync("Bride");
        var ev = await EventAsync(host);
        await CelebrantAsync(ev, celebrant.Email);
        var guestRow = await GuestAsync(ev, "Guest", guest.Email);
        var photo = await PhotoAsync(ev, guestRow, uploader: null);

        Assert.Contains(photo, await AlbumAsync(celebrant, ev));
        var report = await api.Client(celebrant.Token).PostAsJsonAsync("/api/me/reports", new { kind = "photo", targetId = photo, reason = "sexual" });
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        Assert.DoesNotContain(photo, await AlbumAsync(celebrant, ev));
        Assert.Contains(photo, await AlbumAsync(host, ev));

        // The guest's older photo didn't record an account; the report still found whose it is.
        var row = await api.ReadAsync(db => db.ContentReports.SingleAsync(r => r.TargetId == photo));
        Assert.Equal(guest.Id, row.SubjectUserId);

        var admin = await UserAsync("Admin", Roles.Admin);
        var queue = await DataAsync(await api.Client(admin.Token).GetAsync("/api/admin/reports"));
        var item = queue.EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == row.Id);
        Assert.Equal(("photo", "Guest", false), (item.GetProperty("kind").GetString(), item.GetProperty("subjectName").GetString(), item.GetProperty("itemGone").GetBoolean()));

        var removed = await api.Client(admin.Token).PostAsJsonAsync($"/api/admin/reports/{row.Id}/resolve", new { action = "remove" });
        Assert.Equal(HttpStatusCode.OK, removed.StatusCode);
        Assert.DoesNotContain(photo, await AlbumAsync(host, ev));
        Assert.NotNull(await api.ReadAsync(db => db.EventPhotos.Where(p => p.Id == photo).Select(p => p.DeletedAt).SingleAsync()));
        Assert.Equal(TemplateReportStatus.Resolved, await api.ReadAsync(db => db.ContentReports.Where(r => r.Id == row.Id).Select(r => r.Status).SingleAsync()));
    }

    [PostgresFact]
    public async Task An_admin_takes_a_post_out_of_the_feed_and_its_organiser_still_sees_it()
    {
        var host = await UserAsync();
        var guest = await UserAsync();
        var ev = await EventAsync(host, "Loud party");
        await InviteAsync(ev, await GuestAsync(ev, "G", guest.Email), InviteStatus.Sent);
        Assert.Contains(ev, await FeedAsync(guest));

        var report = await api.Client(guest.Token).PostAsJsonAsync("/api/me/reports", new { kind = "post", targetId = ev, reason = "hateful" });
        Assert.Equal(HttpStatusCode.OK, report.StatusCode);
        Assert.DoesNotContain(ev, await FeedAsync(guest));

        var admin = await UserAsync("Admin", Roles.Admin);
        var id = await api.ReadAsync(db => db.ContentReports.Where(r => r.TargetId == ev).Select(r => r.Id).SingleAsync());
        Assert.Equal(HttpStatusCode.OK, (await api.Client(admin.Token).PostAsJsonAsync($"/api/admin/reports/{id}/resolve", new { action = "remove" })).StatusCode);

        var other = await UserAsync();
        await InviteAsync(ev, await GuestAsync(ev, "Other", other.Email), InviteStatus.Sent);
        Assert.DoesNotContain(ev, await FeedAsync(other));
        Assert.Contains(ev, await FeedAsync(host));
    }

    [PostgresFact]
    public async Task Only_admins_open_the_queue()
    {
        var someone = await UserAsync();
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client(someone.Token).GetAsync("/api/admin/reports")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client(someone.Token).PutAsJsonAsync($"/api/admin/users/{someone.Id}/suspended", new { suspended = true })).StatusCode);
    }

    // ----- Blocking ---------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Blocking_hides_their_comments_photos_and_posts_and_leaves_the_guest_list_alone()
    {
        var host = await UserAsync("Host");
        var pest = await UserAsync("Pest");
        var friend = await UserAsync("Friend");
        var ev = await EventAsync(host, "Wedding");
        var pestRow = await GuestAsync(ev, "Pest", pest.Email);
        var pestInvite = await InviteAsync(ev, pestRow, InviteStatus.Sent);
        await InviteAsync(ev, await GuestAsync(ev, "Friend", friend.Email), InviteStatus.Sent);
        await api.WriteAsync(async db =>
        {
            (await db.Invites.SingleAsync(i => i.Id == pestInvite)).RsvpStatus = RsvpStatus.Going;
        });
        var comment = await CommentAsync(pest, ev, "Hello");
        var oldPhoto = await PhotoAsync(ev, pestRow, uploader: null);
        var newPhoto = await PhotoAsync(ev, pestRow, uploader: pest.Id);
        var pestsOwnEvent = await EventAsync(pest, "Pest's party");
        await InviteAsync(pestsOwnEvent, await GuestAsync(pestsOwnEvent, "Host", host.Email), InviteStatus.Sent);
        Assert.Contains(pestsOwnEvent, await FeedAsync(host));

        var block = await api.Client(host.Token).PutAsync($"/api/me/blocks/{pest.Id}", null);
        Assert.Equal(HttpStatusCode.OK, block.StatusCode);
        // Twice is fine.
        Assert.Equal(HttpStatusCode.OK, (await api.Client(host.Token).PutAsync($"/api/me/blocks/{pest.Id}", null)).StatusCode);

        Assert.DoesNotContain(comment, await CommentIdsAsync(host, ev));
        var album = await AlbumAsync(host, ev);
        Assert.DoesNotContain(oldPhoto, album);
        Assert.DoesNotContain(newPhoto, album);
        Assert.DoesNotContain(pestsOwnEvent, await FeedAsync(host));
        // Only for the one who blocked.
        Assert.Contains(comment, await CommentIdsAsync(friend, ev));

        // The event still works: the host still sees the guest and their answer.
        var dashboard = await api.Client(host.Token).GetAsync($"/api/me/campaigns/{ev}/dashboard");
        var text = await dashboard.Content.ReadAsStringAsync();
        Assert.True(dashboard.IsSuccessStatusCode, text);
        Assert.Contains("\"Pest\"", text);
        Assert.Contains("Going", text);

        var list = await DataAsync(await api.Client(host.Token).GetAsync("/api/me/blocks"));
        var entry = Assert.Single(list.EnumerateArray());
        Assert.Equal((pest.Id, "Pest"), (entry.GetProperty("userId").GetGuid(), entry.GetProperty("displayName").GetString()));

        Assert.Equal(HttpStatusCode.OK, (await api.Client(host.Token).DeleteAsync($"/api/me/blocks/{pest.Id}")).StatusCode);
        Assert.Contains(comment, await CommentIdsAsync(host, ev));
        Assert.Contains(oldPhoto, await AlbumAsync(host, ev));
        Assert.Empty((await DataAsync(await api.Client(host.Token).GetAsync("/api/me/blocks"))).EnumerateArray());

        Assert.Equal(HttpStatusCode.BadRequest, (await api.Client(host.Token).PutAsync($"/api/me/blocks/{host.Id}", null)).StatusCode);
    }

    [PostgresFact]
    public async Task Someone_who_blocked_a_person_gets_no_push_about_what_they_do()
    {
        var host = await UserAsync();
        var pest = await UserAsync();
        var ev = await EventAsync(host);
        await InviteAsync(ev, await GuestAsync(ev, "Pest", pest.Email), InviteStatus.Sent);
        var phone = Convert.ToHexString(Guid.NewGuid().ToByteArray()) + Convert.ToHexString(Guid.NewGuid().ToByteArray());
        Assert.Equal(HttpStatusCode.OK, (await api.Client(host.Token).PostAsJsonAsync("/api/me/devices", new { token = phone, platform = "ios" })).StatusCode);

        await CommentAsync(pest, ev, "First");
        await api.DrainPushesAsync();
        Assert.Single(api.Apns.To(phone));

        await api.Client(host.Token).PutAsync($"/api/me/blocks/{pest.Id}", null);
        await CommentAsync(pest, ev, "Second");
        await api.DrainPushesAsync();
        Assert.Single(api.Apns.To(phone));
    }

    // ----- The word filter --------------------------------------------------------------------------

    [PostgresFact]
    public async Task Comments_and_captions_with_slurs_are_refused_with_a_plain_message()
    {
        var host = await UserAsync();
        var guest = await UserAsync();
        var ev = await EventAsync(host);
        await InviteAsync(ev, await GuestAsync(ev, "G", guest.Email), InviteStatus.Sent);

        var refused = await api.Client(guest.Token).PostAsJsonAsync($"/api/me/feed/{ev}/comments", new { body = "go kill yourself" });
        Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
        Assert.Contains("words we don't allow", await refused.Content.ReadAsStringAsync());
        Assert.False(await api.ReadAsync(db => db.PostComments.AnyAsync(c => c.CampaignId == ev)));

        var caption = await api.Client(host.Token).PutAsJsonAsync($"/api/me/feed/{ev}/caption", new { caption = "No f4gs allowed" });
        Assert.Equal(HttpStatusCode.BadRequest, caption.StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await api.Client(guest.Token).PostAsJsonAsync($"/api/me/feed/{ev}/comments", new { body = "Off to Scunthorpe after, classy night!" })).StatusCode);
    }

    // ----- Suspending -------------------------------------------------------------------------------

    [PostgresFact]
    public async Task A_suspended_account_is_signed_out_at_once_and_reinstating_lets_it_back_in()
    {
        var admin = await UserAsync("Admin", Roles.Admin);
        var bad = await UserAsync();
        Assert.Equal(HttpStatusCode.OK, (await api.Client(bad.Token).GetAsync("/api/me/feed")).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await api.Client(admin.Token).PutAsJsonAsync($"/api/admin/users/{bad.Id}/suspended", new { suspended = true })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await api.Client(bad.Token).GetAsync("/api/me/feed")).StatusCode);
        Assert.False(await api.ReadAsync(db => db.Users.Where(u => u.Id == bad.Id).Select(u => u.IsActive).SingleAsync()));

        Assert.Equal(HttpStatusCode.OK, (await api.Client(admin.Token).PutAsJsonAsync($"/api/admin/users/{bad.Id}/suspended", new { suspended = false })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await api.Client(bad.Token).GetAsync("/api/me/feed")).StatusCode);
    }

    // ----- helpers ----------------------------------------------------------------------------------

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("data");
    }

    private async Task<List<Guid>> FeedAsync(Person who)
    {
        var page = await DataAsync(await api.Client(who.Token).GetAsync("/api/me/feed?take=20"));
        return page.GetProperty("items").EnumerateArray().Select(p => p.GetProperty("campaignId").GetGuid()).ToList();
    }

    private async Task<List<Guid>> CommentIdsAsync(Person who, Guid ev)
    {
        var list = await DataAsync(await api.Client(who.Token).GetAsync($"/api/me/feed/{ev}/comments"));
        return list.EnumerateArray()
            .SelectMany(c => new[] { c.GetProperty("id").GetGuid() }
                .Concat(c.GetProperty("replies").EnumerateArray().Select(r => r.GetProperty("id").GetGuid())))
            .ToList();
    }

    private async Task<List<Guid>> AlbumAsync(Person who, Guid ev)
    {
        var box = await DataAsync(await api.Client(who.Token).GetAsync($"/api/campaigns/{ev}/photos"));
        return box.GetProperty("photos").EnumerateArray().Select(p => p.GetProperty("id").GetGuid()).ToList();
    }

    private async Task<Guid> CommentAsync(Person who, Guid ev, string body)
    {
        var made = await DataAsync(await api.Client(who.Token).PostAsJsonAsync($"/api/me/feed/{ev}/comments", new { body }));
        return made.GetProperty("id").GetGuid();
    }

    private async Task<Guid> PhotoAsync(Guid ev, Guid? guest, Guid? uploader)
    {
        var id = Guid.NewGuid();
        await api.WriteAsync(async db =>
        {
            db.EventPhotos.Add(new EventPhoto
            {
                Id = id, CampaignId = ev, GuestId = guest, UploaderUserId = uploader, UploaderName = "Guest",
                OriginalUrl = $"http://localhost/assets/campaigns/{id:N}.jpg", Url = $"http://localhost/assets/campaigns/{id:N}_v.jpg",
                ThumbUrl = $"http://localhost/assets/campaigns/{id:N}_t.jpg", ContentType = "image/jpeg",
                SizeBytes = 10, Width = 10, Height = 10, CreatedAt = DateTimeOffset.UtcNow,
            });
            await Task.CompletedTask;
        });
        return id;
    }

    private async Task<Person> UserAsync(string name = "Test Person", string role = Roles.Customer)
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

    private async Task<Guid> InviteAsync(Guid ev, Guid guest, InviteStatus status)
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
