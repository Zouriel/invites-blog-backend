using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Application.Moderation;
using InvitesBlog.Application.Plans;
using InvitesBlog.Application.Push;
using InvitesBlog.Application.Services.Faces;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using InvitesBlog.Infrastructure.Notifications;
using InvitesBlog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InvitesBlog.Tests.Integration;

/// <summary>
/// The 2026-11-12 notifications audit: what each push carries for grouping and replacing on the phone, the in-app
/// mute switches, the new sweeps (new photos, event reminders, face grouping news) and the single post a comment
/// notification opens. Same real API and Postgres as the rest of the push tests.
/// </summary>
public sealed partial class PushNotificationTests
{
    // ----- What every push carries ------------------------------------------------------------------

    [PostgresFact]
    public async Task A_reply_is_grouped_under_its_event_and_a_changed_answer_replaces_the_last_on_both_platforms()
    {
        var host = await UserAsync();
        var guest = await UserAsync();
        var ev = await EventAsync(host, title: "Dinner");
        var invite = await InviteAsync(ev, await GuestAsync(ev, "Sara", guest.Email));
        var iphone = await RegisterAsync(host, DeviceToken());
        var android = await RegisterAsync(host, DeviceToken(), platform: "android");

        await api.Client(guest.Token).PostAsJsonAsync($"/api/invites/{invite}/rsvp", new { status = "Going" });
        await api.DrainPushesAsync();

        var apple = Assert.Single(api.Apns.To(iphone));
        Assert.Equal((ev.ToString(), $"reply-{invite:N}", "reply"), (apple.ThreadId, apple.CollapseId, apple.Data("kind")));
        Assert.Equal("default", apple.Payload.GetProperty("aps").GetProperty("sound").GetString());
        var google = Assert.Single(api.Fcm.To(android));
        Assert.Equal(("replies", $"reply-{invite:N}", "reply", ev.ToString()), (google.Channel, google.Tag, google.Data("kind"), google.Data("thread")));
    }

    [PostgresFact]
    public async Task Each_kind_goes_to_its_own_Android_channel()
    {
        var host = await UserAsync(name: "Host");
        var guest = await UserAsync(name: "Guest");
        var ev = await EventAsync(host, title: "Channels");
        await GuestAsync(ev, "Guest", guest.Email);
        var guestPhone = await RegisterAsync(guest, DeviceToken(), platform: "android");
        var hostPhone = await RegisterAsync(host, DeviceToken(), platform: "android");

        await api.Client(host.Token).PostAsync($"/api/campaigns/{ev}/finalize", null);
        await api.Client(guest.Token).PostAsJsonAsync($"/api/me/feed/{ev}/comments", new { body = "Hello" });
        await api.DrainPushesAsync();

        Assert.Equal(("events", "invitation"), (Assert.Single(api.Fcm.To(guestPhone)).Channel, api.Fcm.To(guestPhone)[0].Data("kind")));
        Assert.Equal(("comments", "comment"), (Assert.Single(api.Fcm.To(hostPhone)).Channel, api.Fcm.To(hostPhone)[0].Data("kind")));
    }

    // ----- Muting -----------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Muted_kinds_reach_no_phone_but_invitations_still_do_and_unknown_switches_are_ignored()
    {
        var host = await UserAsync();
        var guest = await UserAsync();
        var ev = await EventAsync(host, title: "Muted");
        var guestRow = await GuestAsync(ev, "G", guest.Email);
        var invite = await InviteAsync(ev, guestRow);
        var hostPhone = await RegisterAsync(host, DeviceToken());
        var guestPhone = await RegisterAsync(guest, DeviceToken(), platform: "android");

        var set = await api.Client(host.Token).PutAsJsonAsync("/api/auth/me/notifications", new { muted = new[] { "REPLIES", "invitations", "comments" } });
        Assert.Equal(HttpStatusCode.OK, set.StatusCode);
        var muted = (await DataOf(set)).GetProperty("mutedNotifications").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Equal(["replies", "comments"], muted);
        var me = await DataOf(await api.Client(host.Token).GetAsync("/api/auth/me"));
        Assert.Equal(2, me.GetProperty("mutedNotifications").GetArrayLength());

        // The guest mutes replies too: an invitation can't be muted.
        await api.Client(guest.Token).PutAsJsonAsync("/api/auth/me/notifications", new { muted = new[] { "replies", "reminders" } });

        await api.Client(guest.Token).PostAsJsonAsync($"/api/invites/{invite}/rsvp", new { status = "Going" });
        await api.Client(host.Token).PostAsync($"/api/campaigns/{ev}/finalize", null);
        await api.DrainPushesAsync();
        Assert.Empty(api.Apns.To(hostPhone));
        Assert.Equal("invitation", Assert.Single(api.Fcm.To(guestPhone)).Data("kind"));

        // Switched back on, replies come again.
        await api.Client(host.Token).PutAsJsonAsync("/api/auth/me/notifications", new { muted = Array.Empty<string>() });
        await api.Client(guest.Token).PostAsJsonAsync($"/api/invites/{invite}/rsvp", new { status = "Maybe" });
        await api.DrainPushesAsync();
        Assert.Equal("G might come.", Assert.Single(api.Apns.To(hostPhone)).Body);
    }

    // ----- Replies to replies -------------------------------------------------------------------------

    [PostgresFact]
    public async Task Answering_a_reply_tells_whoever_wrote_that_reply_and_whoever_started_the_thread()
    {
        var host = await UserAsync(name: "Host");
        var aisha = await UserAsync(name: "Aisha");
        var bilal = await UserAsync(name: "Bilal");
        var ev = await EventAsync(host, title: "Party");
        foreach (var p in new[] { aisha, bilal })
            await InviteAsync(ev, await GuestAsync(ev, p.Name, p.Email), InviteStatus.Sent);
        var hostPhone = await RegisterAsync(host, DeviceToken());
        var aishaPhone = await RegisterAsync(aisha, DeviceToken());
        var bilalPhone = await RegisterAsync(bilal, DeviceToken());

        await api.Client(aisha.Token).PostAsJsonAsync($"/api/me/feed/{ev}/comments", new { body = "First!" });
        var first = await api.ReadAsync(db => db.PostComments.Where(c => c.CampaignId == ev).Select(c => c.Id).SingleAsync());
        await api.Client(bilal.Token).PostAsJsonAsync($"/api/me/feed/{ev}/comments", new { body = "Hi Aisha", parentId = first });
        var reply = await api.ReadAsync(db => db.PostComments.Where(c => c.CampaignId == ev && c.ParentId == first).Select(c => c.Id).SingleAsync());
        await api.DrainPushesAsync();
        var before = (host: api.Apns.To(hostPhone).Count, aisha: api.Apns.To(aishaPhone).Count, bilal: api.Apns.To(bilalPhone).Count);

        // The host answers Bilal's reply: it goes into Aisha's thread, and both of them hear.
        await api.Client(host.Token).PostAsJsonAsync($"/api/me/feed/{ev}/comments", new { body = "Welcome both", parentId = reply });
        await api.DrainPushesAsync();

        Assert.Equal("Host replied: Welcome both", api.Apns.To(bilalPhone).Skip(before.bilal).Single().Body);
        Assert.Equal("Host replied: Welcome both", api.Apns.To(aishaPhone).Skip(before.aisha).Single().Body);
        Assert.Equal(before.host, api.Apns.To(hostPhone).Count);
    }

    // ----- The post a comment notification opens ---------------------------------------------------------

    [PostgresFact]
    public async Task One_post_can_be_read_by_someone_in_it_and_by_nobody_else()
    {
        var host = await UserAsync();
        var guest = await UserAsync();
        var stranger = await UserAsync();
        var ev = await EventAsync(host, title: "One post");
        await InviteAsync(ev, await GuestAsync(ev, "G", guest.Email), InviteStatus.Sent);

        var post = await DataOf(await api.Client(guest.Token).GetAsync($"/api/me/feed/{ev}"));
        Assert.Equal("One post", post.GetProperty("title").GetString());
        Assert.Equal(HttpStatusCode.NotFound, (await api.Client(stranger.Token).GetAsync($"/api/me/feed/{ev}")).StatusCode);
    }

    // ----- New photos -------------------------------------------------------------------------------

    [PostgresFact]
    public async Task New_photos_wait_for_a_pause_then_reach_the_album_s_people_once_per_quiet_spell_never_the_uploader()
    {
        var host = await UserAsync(name: "Host");
        var bride = await UserAsync(name: "Bride");
        var hawwa = await UserAsync(name: "Hawwa");
        var ev = await EventAsync(host, title: "Wedding");
        await CelebrantAsync(ev, bride.Email);
        // Hawwa is one of the people the event is for, and the one uploading.
        await CelebrantAsync(ev, hawwa.Email);
        var bucket = await AlbumAsync(ev, host);
        await AlbumPhotoAsync(ev, bucket, hawwa.Id, "Hawwa");
        await AlbumPhotoAsync(ev, bucket, hawwa.Id, "Hawwa", video: true);
        var now = DateTimeOffset.UtcNow;

        Assert.Empty(await PhotoSweepAsync(now.AddMinutes(1), bucket));
        var sent = await PhotoSweepAsync(now.AddMinutes(11), bucket);
        Assert.Equal(new[] { host.Id, bride.Id }.Order(), sent.Select(s => s.User).Order());
        Assert.All(sent, s => Assert.Equal(("Wedding", "Hawwa added 2 photos and videos.", $"/buckets/{bucket}", "photos", $"photos-{bucket:N}"),
            (s.Title, s.Body, s.Data["path"], s.Data["kind"], s.Data["collapse"])));

        // More soon after: quiet for two hours, then one push covering only what's new.
        await AlbumPhotoAsync(ev, bucket, null, "Mohamed");
        Assert.Empty(await PhotoSweepAsync(now.AddMinutes(40), bucket));
        var later = await PhotoSweepAsync(now.AddHours(2).AddMinutes(20), bucket);
        Assert.Contains(later, s => s.User == hawwa.Id && s.Body == "Mohamed added a photo.");
        Assert.Equal(3, later.Count);
    }

    // ----- Event reminders --------------------------------------------------------------------------

    [PostgresFact]
    public async Task Guests_who_are_coming_are_reminded_once_the_day_before_on_the_event_s_clock()
    {
        var host = await UserAsync();
        var going = await UserAsync();
        var maybe = await UserAsync();
        var notGoing = await UserAsync();
        var ev = await EventAsync(host, title: "Reminder party");
        var start = DateTimeOffset.UtcNow.AddHours(20);
        await api.WriteAsync(async db =>
        {
            var c = await db.Campaigns.SingleAsync(x => x.Id == ev);
            c.EventStartAt = start;
            c.TimeZone = "Indian/Maldives";
        });
        foreach (var (p, status) in new[] { (going, RsvpStatus.Going), (maybe, RsvpStatus.Maybe), (notGoing, RsvpStatus.NotGoing), (host, RsvpStatus.Going) })
        {
            var invite = await InviteAsync(ev, await GuestAsync(ev, "G", p.Email), InviteStatus.Sent);
            await api.WriteAsync(async db => (await db.Invites.SingleAsync(i => i.Id == invite)).RsvpStatus = status);
        }

        var sent = await ReminderSweepAsync(DateTimeOffset.UtcNow, ev);
        Assert.Equal(new[] { going.Id, maybe.Id }.Order(), sent.Select(s => s.User).Order());
        var expected = EventReminderService.Body(new Campaign { EventStartAt = start, TimeZone = "Indian/Maldives", Title = "" }, DateTimeOffset.UtcNow);
        Assert.All(sent, s => Assert.Equal(("Reminder party", expected, $"/invitation/{ev}", "reminder"), (s.Title, s.Body, s.Data["path"], s.Data["kind"])));
        Assert.Empty(await ReminderSweepAsync(DateTimeOffset.UtcNow, ev));
    }

    [Fact]
    public void A_reminder_names_the_day_and_time_on_the_event_s_own_clock()
    {
        var c = new Campaign { Title = "x", TimeZone = "Indian/Maldives", EventStartAt = new DateTimeOffset(2026, 11, 13, 14, 0, 0, TimeSpan.Zero) };
        // 20:00 UTC on the 12th is already 01:00 on the 13th in Malé: the event is today there.
        Assert.Equal("Today at 7:00 pm +05.", EventReminderService.Body(c, new DateTimeOffset(2026, 11, 12, 20, 0, 0, TimeSpan.Zero)));
        Assert.Equal("Tomorrow at 7:00 pm +05.", EventReminderService.Body(c, new DateTimeOffset(2026, 11, 12, 15, 0, 0, TimeSpan.Zero)));
        c.AllDay = true;
        Assert.Equal("Tomorrow.", EventReminderService.Body(c, new DateTimeOffset(2026, 11, 12, 15, 0, 0, TimeSpan.Zero)));
    }

    // ----- Face grouping ------------------------------------------------------------------------------

    [PostgresFact]
    public async Task The_host_alone_hears_once_when_face_grouping_finishes_and_never_a_group_s_name()
    {
        var host = await UserAsync();
        var celebrant = await UserAsync();
        var ev = await EventAsync(host, title: "Faces");
        await CelebrantAsync(ev, celebrant.Email);
        var bucket = await AlbumAsync(ev, host, faces: true);
        var person = await FaceGroupAsync(bucket, "Grandma");
        var photo = await AlbumPhotoAsync(ev, bucket, null, "G");
        await api.WriteAsync(async db =>
        {
            db.FaceJobs.Add(new FaceJob { Id = photo, BucketId = bucket, Status = FaceJobStatus.Waiting, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, NextAttemptAt = DateTimeOffset.UtcNow });
            await Task.CompletedTask;
        });

        Assert.DoesNotContain(await FaceSweepAsync(FaceNoticeService.ReadyAsync), s => s.Data["path"].StartsWith($"/buckets/{bucket}"));
        await api.WriteAsync(async db => db.FaceJobs.Remove(await db.FaceJobs.SingleAsync(j => j.Id == photo)));

        var sent = (await FaceSweepAsync(FaceNoticeService.ReadyAsync)).Where(s => s.Data["path"].StartsWith($"/buckets/{bucket}")).ToList();
        var note = Assert.Single(sent);
        Assert.Equal((host.Id, $"/buckets/{bucket}?tab=people", "faces"), (note.User, note.Data["path"], note.Data["kind"]));
        Assert.DoesNotContain("Grandma", note.Title + note.Body);
        Assert.DoesNotContain(await FaceSweepAsync(FaceNoticeService.ReadyAsync), s => s.Data["path"].StartsWith($"/buckets/{bucket}"));
        _ = person;
    }

    [PostgresFact]
    public async Task Opt_outs_are_counted_for_the_host_never_named_and_at_most_daily()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host, title: "Opt outs");
        var bucket = await AlbumAsync(ev, host, faces: true);
        var now = DateTimeOffset.UtcNow;
        await OptOutAsync(await FaceGroupAsync(bucket, "Uncle"), now.AddMinutes(-5), null);
        // The host opting a group out themselves isn't news to them.
        await OptOutAsync(await FaceGroupAsync(bucket, "Me"), now.AddMinutes(-4), host.Id);

        var sent = Assert.Single(await FaceSweepAsync(FaceNoticeService.OptOutsAsync, now), s => s.Data["path"].StartsWith($"/buckets/{bucket}"));
        Assert.Equal((host.Id, "Opt outs", "Someone opted out of face grouping. Their face is no longer grouped or kept."), (sent.User, sent.Title, sent.Body));
        Assert.DoesNotContain("Uncle", sent.Body);

        await OptOutAsync(await FaceGroupAsync(bucket, "Aunt"), now.AddMinutes(10), null);
        await OptOutAsync(await FaceGroupAsync(bucket, "Cousin"), now.AddMinutes(11), null);
        Assert.DoesNotContain(await FaceSweepAsync(FaceNoticeService.OptOutsAsync, now.AddHours(2)), s => s.Data["path"].StartsWith($"/buckets/{bucket}"));
        var next = Assert.Single(await FaceSweepAsync(FaceNoticeService.OptOutsAsync, now.AddDays(1).AddMinutes(1)), s => s.Data["path"].StartsWith($"/buckets/{bucket}"));
        Assert.StartsWith("2 people opted out", next.Body);
    }

    [PostgresFact]
    public async Task The_host_is_warned_a_month_and_a_week_before_a_pass_ending_switches_face_grouping_off()
    {
        var host = await UserAsync();
        var guest = await UserAsync();
        var ev = await EventAsync(host, title: "Pass event");
        await GuestAsync(ev, "G", guest.Email);
        await SetPassAsync(ev, DateTimeOffset.UtcNow.AddDays(20));
        var bucket = await AlbumAsync(ev, host, faces: true);

        var first = Assert.Single(await EndingSweepAsync(bucket));
        Assert.Equal(host.Id, first.User);
        Assert.StartsWith("Face grouping in Pass event ends on ", first.Title);
        Assert.Equal("When the event's pass or Premium ends, face grouping is switched off and its face data deleted.", first.Body);
        Assert.Empty(await EndingSweepAsync(bucket));

        await SetPassAsync(ev, DateTimeOffset.UtcNow.AddDays(5));
        Assert.Single(await EndingSweepAsync(bucket));
        Assert.Empty(await EndingSweepAsync(bucket));
    }

    [PostgresFact]
    public async Task A_Premium_that_ends_warns_the_host_but_one_that_renews_by_itself_does_not()
    {
        var host = await UserAsync();
        await api.WriteAsync(async db =>
        {
            var u = await db.Users.SingleAsync(x => x.Id == host.Id);
            u.SubscriptionTier = SubscriptionTier.Premium;
            u.SubscriptionEndsAt = DateTimeOffset.UtcNow.AddDays(6);
            u.AutoRenewKind = PaymentKind.PremiumMonthly;
        });
        var ev = await EventAsync(host, title: "Premium event");
        var bucket = await AlbumAsync(ev, host, faces: true);
        Assert.Empty(await EndingSweepAsync(bucket));

        await api.WriteAsync(async db => (await db.Users.SingleAsync(x => x.Id == host.Id)).AutoRenewKind = null);
        Assert.Equal("When the event's pass or Premium ends, face grouping is switched off and its face data deleted.",
            Assert.Single(await EndingSweepAsync(bucket)).Body);
    }

    [PostgresFact]
    public async Task An_idle_album_s_host_is_warned_before_face_data_expires_and_told_when_it_has()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host, title: "Old album");
        await SetPassAsync(ev, DateTimeOffset.UtcNow.AddYears(2));
        var bucket = await AlbumAsync(ev, host, faces: true, updated: DateTimeOffset.UtcNow.AddDays(-25));
        var phone = await RegisterAsync(host, DeviceToken());

        var warned = Assert.Single(await EndingSweepAsync(bucket, idle: TimeSpan.FromDays(30)));
        Assert.Contains("no new photos for a long time", warned.Body);

        using (var scope = api.Services.CreateScope())
            Assert.True(await scope.ServiceProvider.GetRequiredService<IFaceGroupingService>().ExpireIdleAsync(TimeSpan.FromDays(10), CancellationToken.None) >= 1);
        await api.DrainPushesAsync();
        var off = Assert.Single(api.Apns.To(phone));
        Assert.Equal(("Face grouping is off in Old album", $"/buckets/{bucket}", "album"), (off.Title, off.Path, off.Data("kind")));
        Assert.Contains("You can switch it on again", off.Body);
    }

    [Fact]
    public void The_photos_ending_push_sells_nothing()
    {
        var body = MediaRetentionService.PushBody(new DateTimeOffset(2026, 12, 3, 0, 0, 0, TimeSpan.Zero), 700L * 1024 * 1024);
        Assert.Equal("On 3 December about 700 MB above Free's 300 MB will be removed. Download what you want to keep before then.", body);
        Assert.DoesNotContain("extend", body, StringComparison.OrdinalIgnoreCase);
    }

    // ----- helpers ------------------------------------------------------------------------------------

    private sealed record Sent(Guid User, string Title, string Body, IReadOnlyDictionary<string, string> Data);

    private sealed class Recorder : IPushSender
    {
        public List<Sent> Sent { get; } = [];
        public Task SendAsync(Guid userId, string title, string body, IReadOnlyDictionary<string, string>? data, CancellationToken ct)
        {
            Sent.Add(new Sent(userId, title, body, data ?? new Dictionary<string, string>()));
            return Task.CompletedTask;
        }
    }

    private static async Task<JsonElement> DataOf(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("data").Clone();
    }

    private async Task<List<Sent>> PhotoSweepAsync(DateTimeOffset now, Guid bucket)
    {
        using var scope = api.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var rec = new Recorder();
        await AlbumPhotoNoticeService.SweepAsync(sp.GetRequiredService<AppDbContext>(), sp.GetRequiredService<PushNotificationService>(),
            rec, sp.GetService<IModerationViewService>(), now, CancellationToken.None);
        return rec.Sent.Where(s => s.Data["path"] == $"/buckets/{bucket}").ToList();
    }

    private async Task<List<Sent>> ReminderSweepAsync(DateTimeOffset now, Guid ev)
    {
        using var scope = api.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var rec = new Recorder();
        await EventReminderService.SweepAsync(sp.GetRequiredService<AppDbContext>(), sp.GetRequiredService<PushNotificationService>(), rec, now, CancellationToken.None);
        return rec.Sent.Where(s => s.Data["path"] == $"/invitation/{ev}").ToList();
    }

    private async Task<List<Sent>> FaceSweepAsync(
        Func<AppDbContext, PushNotificationService, IPushSender, DateTimeOffset, CancellationToken, Task<int>> sweep, DateTimeOffset? now = null)
    {
        using var scope = api.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var rec = new Recorder();
        await sweep(sp.GetRequiredService<AppDbContext>(), sp.GetRequiredService<PushNotificationService>(), rec, now ?? DateTimeOffset.UtcNow, CancellationToken.None);
        return rec.Sent;
    }

    private async Task<List<Sent>> EndingSweepAsync(Guid bucket, TimeSpan? idle = null)
    {
        using var scope = api.Services.CreateScope();
        var sp = scope.ServiceProvider;
        var rec = new Recorder();
        await FaceNoticeService.EndingAsync(sp.GetRequiredService<AppDbContext>(), sp.GetRequiredService<PushNotificationService>(), rec,
            sp.GetRequiredService<IPlanService>(), idle ?? TimeSpan.FromDays(730), DateTimeOffset.UtcNow, CancellationToken.None);
        return rec.Sent.Where(s => s.Data["path"].StartsWith($"/buckets/{bucket}")).ToList();
    }

    private async Task SetPassAsync(Guid ev, DateTimeOffset until) =>
        await api.WriteAsync(async db =>
        {
            var c = await db.Campaigns.SingleAsync(x => x.Id == ev);
            c.EventPass = EventPassKind.Gold;
            c.EventPassUntil = until;
        });

    private async Task<Guid> AlbumAsync(Guid ev, Person owner, bool faces = false, DateTimeOffset? updated = null)
    {
        var id = Guid.NewGuid();
        await api.WriteAsync(async db =>
        {
            db.MediaBuckets.Add(new MediaBucket
            {
                Id = id, OwnerUserId = owner.Id, CampaignId = ev, EventDate = DateTimeOffset.UtcNow.AddDays(10), CapacityBytes = 1_000_000,
                FaceGroupingOn = faces, FaceGroupingConfirmedAt = faces ? DateTimeOffset.UtcNow : null,
                CreatedAt = (updated ?? DateTimeOffset.UtcNow).AddMinutes(-1), UpdatedAt = updated ?? DateTimeOffset.UtcNow,
            });
            await Task.CompletedTask;
        });
        return id;
    }

    private async Task<Guid> AlbumPhotoAsync(Guid ev, Guid bucket, Guid? uploader, string name, bool video = false)
    {
        var id = Guid.NewGuid();
        await api.WriteAsync(async db =>
        {
            db.EventPhotos.Add(new EventPhoto
            {
                Id = id, CampaignId = ev, BucketId = bucket, UploaderUserId = uploader, UploaderName = name,
                OriginalUrl = $"http://localhost/assets/campaigns/{id:N}.jpg", Url = $"http://localhost/assets/campaigns/{id:N}_v.jpg",
                ThumbUrl = $"http://localhost/assets/campaigns/{id:N}_t.jpg", ContentType = video ? "video/mp4" : "image/jpeg",
                SizeBytes = 10, Width = 10, Height = 10, CreatedAt = DateTimeOffset.UtcNow,
            });
            await Task.CompletedTask;
        });
        return id;
    }

    private async Task<Guid> FaceGroupAsync(Guid bucket, string name)
    {
        var id = Guid.NewGuid();
        await api.WriteAsync(async db =>
        {
            db.FacePeople.Add(new FacePerson { Id = id, BucketId = bucket, Name = name, Centroid = new float[128], FaceCount = 1, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            await Task.CompletedTask;
        });
        return id;
    }

    private async Task OptOutAsync(Guid person, DateTimeOffset at, Guid? by) =>
        await api.WriteAsync(async db =>
        {
            var p = await db.FacePeople.SingleAsync(x => x.Id == person);
            p.Ignored = true;
            p.Name = null;
            p.IgnoredAt = at;
            p.IgnoredByUserId = by;
        });
}
