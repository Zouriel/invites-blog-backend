using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InvitesBlog.Api.Authorization;
using InvitesBlog.Application.Services.Billing;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InvitesBlog.Tests.Integration;

[CollectionDefinition("account-deletion")]
public sealed class AccountDeletionCollection : ICollectionFixture<PostgresApiFixture>;

/// <summary>
/// Deleting an account, through the real API on real Postgres. Everything is read back in a context of
/// its own after the request has finished, and stored files are checked on disk.
/// </summary>
[Collection("account-deletion")]
public sealed class AccountDeletionTests(PostgresApiFixture api)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    // ----- What's deleted ----------------------------------------------------------------------

    [PostgresFact]
    public async Task A_host_goes_with_their_events_guests_photos_and_files_and_the_summary_said_so()
    {
        var host = await UserAsync(subscription: SubscriptionTier.Premium, customer: $"cus_{Guid.NewGuid():N}", autoRenew: true);
        var other = await UserAsync();

        var ev = await EventAsync(host);
        var guest = await GuestAsync(ev, "A guest", "guest-" + Guid.NewGuid().ToString("N") + "@example.com");
        var invite = await InviteAsync(ev, guest);
        var album = await AlbumAsync(ev, host.Id);
        await FeedActivityOnAsync(ev, other.Id);
        var photo = await PhotoAsync(ev, album, guestId: guest, name: "A guest");
        var video = await PhotoAsync(ev, album, contentType: "video/mp4");
        var qr = await QrAsync(album);
        var asset = await AssetAsync(ev);
        var removedEarlier = await PhotoAsync(ev, album, deleted: true);
        await WritePassAsync(ev, EventPassKind.Platinum);
        await api.WriteAsync(async db =>
        {
            db.DeliveryAttempts.Add(new DeliveryAttempt { Id = Guid.NewGuid(), InviteId = invite, Channel = "email", RecipientAddress = "guest@example.com", AttemptedAt = DateTimeOffset.UtcNow });
            db.RsvpResponses.Add(new RsvpResponse { Id = Guid.NewGuid(), InviteId = invite, CreatedAt = DateTimeOffset.UtcNow });
            db.InviteTrustedIps.Add(new InviteTrustedIp { Id = Guid.NewGuid(), InviteId = invite, IpAddress = "10.0.0.1", FirstSeenAt = DateTimeOffset.UtcNow, LastSeenAt = DateTimeOffset.UtcNow });
            db.UploadedGuestFiles.Add(new UploadedGuestFile { Id = Guid.NewGuid(), CampaignId = ev, FileName = "list.xlsx", CreatedAt = DateTimeOffset.UtcNow });
            db.Inquiries.Add(new Inquiry { Id = Guid.NewGuid(), Name = "Host", Email = host.Email!, Occasion = "Wedding", Message = "Hello", CreatedAt = DateTimeOffset.UtcNow });
            db.VerifiedContactLinks.Add(new VerifiedContactLink { Id = Guid.NewGuid(), Email = host.Email!, PhoneE164 = Phone(), VerifiedFrom = "email", CreatedAt = DateTimeOffset.UtcNow });
            await Task.CompletedTask;
        });
        var avatar = await api.Storage.PutAsync($"avatars/{host.Id:N}/{Guid.NewGuid():N}.jpg", [1, 2, 3], "image/jpeg");
        var oldAvatar = await api.Storage.PutAsync($"avatars/{host.Id:N}/{Guid.NewGuid():N}.jpg", [1, 2, 3], "image/jpeg");
        await api.WriteAsync(async db =>
        {
            var u = await db.Users.FirstAsync(x => x.Id == host.Id);
            u.AvatarUrl = avatar;
            u.ThemePreference = "dark";
        });
        var client = api.Client(host.Token);
        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.PostAsJsonAsync("/api/me/devices", new { token = $"dev-{Guid.NewGuid():N}", platform = "ios" })).StatusCode);
        Assert.True(await api.ReadAsync(db => db.DeviceTokens.AnyAsync(d => d.UserId == host.Id)));

        var summary = await client.GetFromJsonAsync<Envelope<SummaryDto>>("/api/auth/me/deletion/summary", Web);
        Assert.Equal(1, summary!.Data.EventsDeleted);
        Assert.Equal(0, summary.Data.EventsPassedOn);
        Assert.Equal(1, summary.Data.Albums);
        Assert.Equal(1, summary.Data.Photos);
        Assert.Equal(1, summary.Data.Videos);
        Assert.True(summary.Data.AutoRenews);
        Assert.Contains("Premium pass", summary.Data.Paid);
        Assert.Contains(summary.Data.Paid, p => p.StartsWith("Platinum pass on"));
        Assert.Null(summary.Data.Blocked);
        Assert.Equal("email", summary.Data.CodeChannel);

        await DeleteThroughApiAsync(client, host.Email!);

        await api.ReadAsync(async db =>
        {
            Assert.False(await db.Users.AnyAsync(u => u.Id == host.Id));
            Assert.False(await db.UserRoles.AnyAsync(r => r.UserId == host.Id));
            Assert.False(await db.UserExternalLogins.AnyAsync(l => l.UserId == host.Id));
            Assert.False(await db.DeviceTokens.AnyAsync(d => d.UserId == host.Id));
            Assert.False(await db.Campaigns.AnyAsync(c => c.Id == ev));
            Assert.False(await db.Guests.AnyAsync(g => g.CampaignId == ev));
            Assert.False(await db.Invites.AnyAsync(i => i.CampaignId == ev));
            Assert.False(await db.DeliveryAttempts.AnyAsync(a => a.InviteId == invite));
            Assert.False(await db.RsvpResponses.AnyAsync(r => r.InviteId == invite));
            Assert.False(await db.InviteTrustedIps.AnyAsync(t => t.InviteId == invite));
            Assert.False(await db.UploadedGuestFiles.AnyAsync(f => f.CampaignId == ev));
            Assert.False(await db.CampaignAssets.AnyAsync(a => a.CampaignId == ev));
            Assert.False(await db.MediaBuckets.AnyAsync(b => b.CampaignId == ev));
            Assert.False(await db.MediaBucketQrs.AnyAsync(q => q.BucketId == album));
            Assert.False(await db.EventPhotos.AnyAsync(p => p.CampaignId == ev));
            Assert.False(await db.PostComments.AnyAsync(c => c.CampaignId == ev));
            Assert.False(await db.PostLikes.AnyAsync(l => l.CampaignId == ev));
            Assert.False(await db.Inquiries.AnyAsync(i => i.Email == host.Email));
            Assert.False(await db.VerifiedContactLinks.AnyAsync(l => l.Email == host.Email));
            Assert.False(await db.OtpChallenges.AnyAsync(o => o.Email == host.Email));
            Assert.False(await db.Inviters.AnyAsync(i => i.Email == host.Email));
            // The event's own hidden template row goes with it.
            Assert.False(await db.Templates.AnyAsync(t => t.Slug == $"own-{ev:N}"));
            return true;
        });

        // Files go after the commit, through the removal list.
        foreach (var url in new[] { photo.Url, photo.ThumbUrl, photo.OriginalUrl, video.Url, removedEarlier.Url, qr, asset, avatar, oldAvatar })
            Assert.True(await ExistsAsync(url), $"{url} should still be there before the removals run");
        await api.RunRemovalsAsync();
        foreach (var url in new[] { photo.Url, photo.ThumbUrl, photo.OriginalUrl, video.Url, removedEarlier.Url, qr, asset, avatar, oldAvatar })
            Assert.False(await ExistsAsync(url), $"{url} should be gone");
        Assert.Contains(host.Customer!, api.Bank.Forgotten);
        Assert.Equal(0, await api.ReadAsync(db => db.PendingRemovals.CountAsync(r => r.Target.Contains(host.Id.ToString("N")))));
    }

    [PostgresFact]
    public async Task What_they_added_to_someone_elses_event_stays_with_its_name_and_space_and_still_displays()
    {
        var owner = await UserAsync();
        var person = await UserAsync();
        var ev = await EventAsync(owner);
        var album = await AlbumAsync(ev, owner.Id);
        var row = await GuestAsync(ev, "The person", person.Email);
        var invite = await InviteAsync(ev, row);
        var theirs = await PhotoAsync(ev, album, guestId: row, name: "The person", bytes: 1000);
        await api.WriteAsync(async db =>
        {
            db.CampaignCelebrants.Add(new CampaignCelebrant { Id = Guid.NewGuid(), CampaignId = ev, Name = "The person", Email = person.Email, CreatedAt = DateTimeOffset.UtcNow });
            await Task.CompletedTask;
        });

        var summary = await api.Client(person.Token).GetFromJsonAsync<Envelope<SummaryDto>>("/api/auth/me/deletion/summary", Web);
        Assert.Equal(0, summary!.Data.EventsDeleted);
        Assert.Equal(0, summary.Data.EventsPassedOn);
        Assert.Equal(0, summary.Data.Photos);

        await DeleteThroughApiAsync(api.Client(person.Token), person.Email!);
        await api.RunRemovalsAsync();

        Assert.Equal(1000, await UsedBytesAsync(album));
        Assert.True(await ExistsAsync(theirs.Url));
        Assert.True(await ExistsAsync(theirs.OriginalUrl));
        await api.ReadAsync(async db =>
        {
            var photo = await db.EventPhotos.SingleAsync(p => p.Id == theirs.Id);
            Assert.Null(photo.DeletedAt);
            Assert.Equal("The person", photo.UploaderName);
            Assert.Equal(person.Email, (await db.Guests.SingleAsync(g => g.Id == row)).Email);
            Assert.False(await db.CampaignCelebrants.AnyAsync(c => c.CampaignId == ev && c.Email == person.Email));
            return true;
        });

        // It still shows in the host's album, under the name it was added with.
        var box = await (await api.Client(owner.Token).GetAsync($"/api/media-buckets/{album}/media")).Content.ReadAsStringAsync();
        Assert.Contains(theirs.Id.ToString(), box);
        Assert.Contains("The person", box);
        Assert.Equal(HttpStatusCode.OK, (await api.Client().GetAsync($"/api/invites/by-token/{invite:N}")).StatusCode);
    }

    // ----- Passing an event on -----------------------------------------------------------------

    [PostgresFact]
    public async Task With_no_other_celebrant_the_event_is_deleted_and_their_own_celebrant_row_doesnt_count()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        await CelebrantAsync(ev, host.Email);

        var summary = await api.Client(host.Token).GetFromJsonAsync<Envelope<SummaryDto>>("/api/auth/me/deletion/summary", Web);
        Assert.Equal(1, summary!.Data.EventsDeleted);
        Assert.Equal(0, summary.Data.EventsPassedOn);

        await DeleteThroughApiAsync(api.Client(host.Token), host.Email!);
        Assert.False(await api.ReadAsync(db => db.Campaigns.AnyAsync(c => c.Id == ev)));
    }

    [PostgresFact]
    public async Task An_event_passes_to_the_celebrant_with_an_account_who_can_really_run_it_and_the_old_keys_stop_working()
    {
        var host = await UserAsync();
        var heir = await UserAsync();
        var inviter = Guid.NewGuid();
        await api.WriteAsync(async db =>
        {
            db.Inviters.Add(new Inviter { Id = inviter, Name = "Host", PhoneE164 = Phone(), Email = host.Email!, CreatedAt = DateTimeOffset.UtcNow });
            await Task.CompletedTask;
        });
        var ev = await EventAsync(host, inviterId: inviter);
        await WritePassAsync(ev, EventPassKind.Platinum);
        var key = Guid.NewGuid().ToString("N");
        await api.WriteAsync(db => db.Campaigns.Where(c => c.Id == ev).ExecuteUpdateAsync(s => s.SetProperty(c => c.AccessTokenHash, InvitesBlog.Application.Security.TokenService.Hash(key))));
        var album = await AlbumAsync(ev, host.Id);
        var guest = await GuestAsync(ev, "A guest", "g-" + Guid.NewGuid().ToString("N") + "@example.com");
        var photo = await PhotoAsync(ev, album, guestId: guest, name: "A guest");
        // Several accountless celebrants to pick from, one with an account: only the account can win.
        for (var i = 0; i < 6; i++) await CelebrantAsync(ev, $"none-{Guid.NewGuid():N}@example.com");
        await CelebrantAsync(ev, heir.Email);
        var hostClient = api.Client(host.Token);

        var summary = await hostClient.GetFromJsonAsync<Envelope<SummaryDto>>("/api/auth/me/deletion/summary", Web);
        Assert.Equal(0, summary!.Data.EventsDeleted);
        Assert.Equal(1, summary.Data.EventsPassedOn);
        Assert.Equal(0, summary.Data.Albums);
        Assert.Equal(0, summary.Data.Photos);
        Assert.Empty(summary.Data.Paid);

        Assert.Equal(HttpStatusCode.OK, (await api.Client(key).GetAsync($"/api/campaigns/{ev}/summary")).StatusCode);
        await DeleteThroughApiAsync(hostClient, host.Email!);
        await api.RunRemovalsAsync();

        await api.ReadAsync(async db =>
        {
            var c = await db.Campaigns.SingleAsync(x => x.Id == ev);
            Assert.Equal(heir.Id, c.CreatedByUserId);
            Assert.Null(c.InviterId);
            Assert.Equal(EventPassKind.Platinum, c.EventPass);
            Assert.Equal(heir.Id, (await db.MediaBuckets.SingleAsync(b => b.Id == album)).OwnerUserId);
            Assert.True(await db.Guests.AnyAsync(g => g.Id == guest));
            Assert.Null((await db.EventPhotos.SingleAsync(p => p.Id == photo.Id)).DeletedAt);
            Assert.True((await db.CampaignCelebrants.SingleAsync(x => x.CampaignId == ev && x.Email == heir.Email)).CanManage);
            Assert.False(await db.Inviters.AnyAsync(i => i.Id == inviter));
            Assert.Null((await db.Users.SingleAsync(u => u.Id == heir.Id)).AutoRenewKind);
            return true;
        });
        Assert.True(await ExistsAsync(photo.Url));
        Assert.Contains(api.Mail.Sent, m => m.To == heir.Email && m.Subject.Contains("You now manage"));

        // Runs it for real: dashboard, guest list, a change, and rights only an organiser has.
        var heirClient = api.Client(heir.Token);
        Assert.Equal(HttpStatusCode.OK, (await heirClient.GetAsync($"/api/campaigns/{ev}/summary")).StatusCode);
        var dashboard = await heirClient.GetAsync($"/api/me/campaigns/{ev}/dashboard");
        Assert.Equal(HttpStatusCode.OK, dashboard.StatusCode);
        Assert.Contains("A guest", await dashboard.Content.ReadAsStringAsync());
        var events = await heirClient.GetFromJsonAsync<Envelope<List<CampaignRow>>>("/api/me/campaigns", Web);
        Assert.Contains(events!.Data, c => c.Id == ev);
        Assert.Equal(HttpStatusCode.OK, (await heirClient.GetAsync($"/api/media-buckets/{album}")).StatusCode);
        Assert.True((await heirClient.PostAsJsonAsync($"/api/campaigns/{ev}/celebrants", new { name = "One more", email = $"more-{Guid.NewGuid():N}@example.com" })).IsSuccessStatusCode);

        // The old JWT and the old possession key both open nothing now.
        Assert.Equal(HttpStatusCode.Unauthorized, (await hostClient.GetAsync($"/api/campaigns/{ev}/summary")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await hostClient.PostAsync("/api/auth/me/refresh", null)).StatusCode);
        var withKey = (await api.Client(key).GetAsync($"/api/campaigns/{ev}/summary")).StatusCode;
        Assert.True(withKey is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden, withKey.ToString());
    }

    [PostgresFact]
    public async Task With_only_an_accountless_celebrant_the_event_waits_for_them_and_a_stranger_cant_take_it()
    {
        var host = await UserAsync();
        var stranger = await UserAsync();
        var heirEmail = $"later-{Guid.NewGuid():N}@example.com";
        var ev = await EventAsync(host);
        var album = await AlbumAsync(ev, host.Id);
        await CelebrantAsync(ev, heirEmail);

        await DeleteThroughApiAsync(api.Client(host.Token), host.Email!);

        await api.ReadAsync(async db =>
        {
            var c = await db.Campaigns.SingleAsync(x => x.Id == ev);
            Assert.Null(c.CreatedByUserId);
            Assert.Equal(Guid.Empty, (await db.MediaBuckets.SingleAsync(b => b.Id == album)).OwnerUserId);
            return true;
        });
        Assert.Contains(api.Mail.Sent, m => m.To == heirEmail && m.Subject.Contains("You now manage"));

        // A stranger, signed in: nothing, even after going through sign-in themselves.
        var strangerClient = api.Client(stranger.Token);
        Assert.Equal(HttpStatusCode.Forbidden, (await strangerClient.GetAsync($"/api/campaigns/{ev}/summary")).StatusCode);
        Assert.True((await strangerClient.PostAsync("/api/auth/me/refresh", null)).IsSuccessStatusCode);
        Assert.Null(await api.ReadAsync(db => db.Campaigns.Where(c => c.Id == ev).Select(c => c.CreatedByUserId).FirstAsync()));
        // Nobody with no token either, and the event's guest pages don't break for having no owner.
        Assert.NotEqual(HttpStatusCode.OK, (await api.Client().GetAsync($"/api/campaigns/{ev}/summary")).StatusCode);

        // They really sign up, proving the address with a code, and it is theirs.
        var anon = api.Client();
        var started = await (await anon.PostAsJsonAsync("/api/auth/signup/start", new { identifier = heirEmail })).Content.ReadFromJsonAsync<Envelope<CodeDto>>(Web);
        var signedUp = await anon.PostAsJsonAsync("/api/auth/signup", new { challengeId = started!.Data.ChallengeId, code = api.Codes.CodeFor(heirEmail), password = "a-long-password-1", displayName = "Heir" });
        Assert.True(signedUp.IsSuccessStatusCode, await signedUp.Content.ReadAsStringAsync());
        var token = (await signedUp.Content.ReadFromJsonAsync<Envelope<TokenDto>>(Web))!.Data.Token;

        var userId = await api.ReadAsync(db => db.Users.Where(u => u.Email == heirEmail).Select(u => u.Id).FirstAsync());
        await api.ReadAsync(async db =>
        {
            Assert.Equal(userId, (await db.Campaigns.SingleAsync(c => c.Id == ev)).CreatedByUserId);
            Assert.Equal(userId, (await db.MediaBuckets.SingleAsync(b => b.Id == album)).OwnerUserId);
            Assert.False((await db.CampaignCelebrants.SingleAsync(c => c.CampaignId == ev && c.Email == heirEmail)).InheritsOwnership);
            return true;
        });
        Assert.Equal(HttpStatusCode.OK, (await api.Client(token).GetAsync($"/api/campaigns/{ev}/summary")).StatusCode);
    }

    private async Task CelebrantAsync(Guid ev, string? email) =>
        await api.WriteAsync(async db =>
        {
            db.CampaignCelebrants.Add(new CampaignCelebrant { Id = Guid.NewGuid(), CampaignId = ev, Name = "A celebrant", Email = email, CreatedAt = DateTimeOffset.UtcNow });
            await Task.CompletedTask;
        });

    [PostgresFact]
    public async Task Comments_and_likes_on_other_events_go_and_replies_from_others_stay_visible()
    {
        var owner = await UserAsync();
        var person = await UserAsync();
        var replier = await UserAsync();
        var ev = await EventAsync(owner);

        var comment = Guid.NewGuid();
        var reply = Guid.NewGuid();
        var replierComment = Guid.NewGuid();
        await api.WriteAsync(async db =>
        {
            db.PostComments.Add(new PostComment { Id = comment, CampaignId = ev, UserId = person.Id, Body = "Mine", CreatedAt = DateTimeOffset.UtcNow });
            db.PostComments.Add(new PostComment { Id = reply, CampaignId = ev, UserId = replier.Id, ParentId = comment, Body = "Reply", CreatedAt = DateTimeOffset.UtcNow });
            db.PostComments.Add(new PostComment { Id = replierComment, CampaignId = ev, UserId = replier.Id, Body = "Theirs", CreatedAt = DateTimeOffset.UtcNow });
            db.PostLikes.Add(new PostLike { Id = Guid.NewGuid(), CampaignId = ev, UserId = person.Id, CreatedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
            // A like on someone else's comment has no cascade from users: this is what would block the delete.
            db.CommentLikes.Add(new CommentLike { Id = Guid.NewGuid(), CommentId = replierComment, UserId = person.Id, CreatedAt = DateTimeOffset.UtcNow });
        });

        await DeleteThroughApiAsync(api.Client(person.Token), person.Email!);

        await api.ReadAsync(async db =>
        {
            Assert.False(await db.PostComments.AnyAsync(c => c.UserId == person.Id));
            Assert.False(await db.PostLikes.AnyAsync(c => c.UserId == person.Id));
            Assert.False(await db.CommentLikes.AnyAsync(c => c.UserId == person.Id));
            Assert.Null((await db.PostComments.SingleAsync(c => c.Id == reply)).ParentId);
            Assert.True(await db.PostComments.AnyAsync(c => c.Id == replierComment));
            return true;
        });
    }

    // ----- Accounts of each kind ---------------------------------------------------------------

    [PostgresFact]
    public async Task A_phone_only_account_is_confirmed_by_text_and_hosts_found_by_phone_go()
    {
        var phone = Phone();
        var person = await UserAsync(email: null, phone: phone);
        var inviter = Guid.NewGuid();
        await api.WriteAsync(async db =>
        {
            db.Inviters.Add(new Inviter { Id = inviter, Name = "By phone", PhoneE164 = phone, Email = $"inviter-{inviter:N}@example.com", CreatedAt = DateTimeOffset.UtcNow });
            await Task.CompletedTask;
        });
        // Merged-account lookup: no creator recorded, found through the host row's phone alone.
        var ev = await EventAsync(creator: null, inviterId: inviter);

        var client = api.Client(person.Token);
        var summary = await client.GetFromJsonAsync<Envelope<SummaryDto>>("/api/auth/me/deletion/summary", Web);
        Assert.Equal("sms", summary!.Data.CodeChannel);
        Assert.Equal(1, summary.Data.EventsDeleted);

        await DeleteThroughApiAsync(client, phone);

        Assert.Equal("sms", api.Codes.ChannelFor(phone));
        await api.ReadAsync(async db =>
        {
            Assert.False(await db.Users.AnyAsync(u => u.Id == person.Id));
            Assert.False(await db.Campaigns.AnyAsync(c => c.Id == ev));
            Assert.False(await db.Inviters.AnyAsync(i => i.Id == inviter));
            Assert.False(await db.OtpChallenges.AnyAsync(o => o.PhoneE164 == phone));
            return true;
        });
    }

    [PostgresFact]
    public async Task A_google_only_account_with_no_password_can_delete_itself_and_its_login_goes()
    {
        var person = await UserAsync(password: null);
        await api.WriteAsync(async db =>
        {
            db.UserExternalLogins.Add(new UserExternalLogin { Id = Guid.NewGuid(), UserId = person.Id, Provider = "google", ExternalSubjectId = $"sub-{person.Id:N}", Email = person.Email!, CreatedAt = DateTimeOffset.UtcNow });
            await Task.CompletedTask;
        });

        await DeleteThroughApiAsync(api.Client(person.Token), person.Email!);

        await api.ReadAsync(async db =>
        {
            Assert.False(await db.Users.AnyAsync(u => u.Id == person.Id));
            Assert.False(await db.UserExternalLogins.AnyAsync(l => l.ExternalSubjectId == $"sub-{person.Id:N}"));
            return true;
        });
    }

    // ----- Refusals, nothing changed -----------------------------------------------------------

    [PostgresFact]
    public async Task A_wrong_code_a_sign_in_code_and_another_accounts_code_all_change_nothing()
    {
        var person = await UserAsync();
        var stranger = await UserAsync();
        var ev = await EventAsync(person);
        var client = api.Client(person.Token);

        var sent = await RequestCodeAsync(client);
        var wrong = await client.PostAsJsonAsync("/api/auth/me/deletion/confirm", new { challengeId = sent.ChallengeId, code = "000000" == api.Codes.CodeFor(person.Email!) ? "000001" : "000000" });
        Assert.Equal(HttpStatusCode.BadRequest, wrong.StatusCode);

        // A code for sign-in, to the same address, doesn't delete.
        var signIn = await (await api.Client().PostAsJsonAsync("/api/auth/signup/start", new { identifier = person.Email })).Content.ReadFromJsonAsync<Envelope<CodeDto>>(Web);
        var withSignIn = await client.PostAsJsonAsync("/api/auth/me/deletion/confirm", new { challengeId = signIn!.Data.ChallengeId, code = api.Codes.CodeFor(person.Email!) });
        Assert.False(withSignIn.IsSuccessStatusCode);

        // Another account's own deletion code doesn't delete this one.
        var theirs = await RequestCodeAsync(api.Client(stranger.Token));
        var crossed = await client.PostAsJsonAsync("/api/auth/me/deletion/confirm", new { challengeId = theirs.ChallengeId, code = api.Codes.CodeFor(stranger.Email!) });
        Assert.False(crossed.IsSuccessStatusCode);

        await api.ReadAsync(async db =>
        {
            Assert.True(await db.Users.AnyAsync(u => u.Id == person.Id));
            Assert.True(await db.Users.AnyAsync(u => u.Id == stranger.Id));
            Assert.True(await db.Campaigns.AnyAsync(c => c.Id == ev));
            return true;
        });
    }

    [PostgresFact]
    public async Task A_deletion_code_cant_be_used_to_sign_in()
    {
        var person = await UserAsync();
        var sent = await RequestCodeAsync(api.Client(person.Token));
        var attempt = await api.Client().PostAsJsonAsync("/api/otp/verify", new { challengeId = sent.ChallengeId, code = api.Codes.CodeFor(person.Email!) });
        Assert.False(attempt.IsSuccessStatusCode);
        Assert.Null(await api.ReadAsync(db => db.OtpChallenges.Where(o => o.Id == sent.ChallengeId).Select(o => o.VerifiedAt).FirstAsync()));
    }

    [PostgresFact]
    public async Task The_only_admin_is_refused_with_a_clear_message_until_there_is_another()
    {
        // Serial with every other admin test: this one is about how many admins exist.
        var person = await UserAsync(role: Roles.Admin, roleNames: [Roles.Admin, Roles.Customer]);
        await api.WriteAsync(async db =>
        {
            var admin = await db.Roles.Where(r => r.Name == Roles.Admin).Select(r => r.Id).FirstAsync();
            await db.UserRoles.Where(r => r.RoleId == admin && r.UserId != person.Id).ExecuteDeleteAsync();
        });
        var client = api.Client(person.Token);

        var summary = await client.GetFromJsonAsync<Envelope<SummaryDto>>("/api/auth/me/deletion/summary", Web);
        Assert.Contains("only admin", summary!.Data.Blocked);

        var refused = await client.PostAsync("/api/auth/me/deletion/request", null);
        Assert.False(refused.IsSuccessStatusCode);
        Assert.Contains("only admin", await refused.Content.ReadAsStringAsync());
        var confirm = await client.PostAsJsonAsync("/api/auth/me/deletion/confirm", new { challengeId = Guid.NewGuid(), code = "123456" });
        Assert.Contains("only admin", await confirm.Content.ReadAsStringAsync());
        Assert.True(await api.ReadAsync(db => db.Users.AnyAsync(u => u.Id == person.Id)));

        // With a second admin it goes through.
        var second = await UserAsync(role: Roles.Admin, roleNames: [Roles.Admin]);
        await DeleteThroughApiAsync(client, person.Email!);
        Assert.False(await api.ReadAsync(db => db.Users.AnyAsync(u => u.Id == person.Id)));
        Assert.True(await api.ReadAsync(db => db.UserRoles.AnyAsync(r => r.UserId == second.Id)));
    }

    [PostgresFact]
    public async Task Too_many_tries_from_one_address_are_limited()
    {
        var person = await UserAsync();
        var client = api.Client(person.Token, address: "10.99.0.1");
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 11; i++)
            statuses.Add((await client.PostAsJsonAsync("/api/auth/me/deletion/confirm", new { challengeId = Guid.NewGuid(), code = "123456" })).StatusCode);
        Assert.DoesNotContain(HttpStatusCode.TooManyRequests, statuses.Take(10));
        Assert.Equal(HttpStatusCode.TooManyRequests, statuses[10]);
    }

    // ----- Sessions ----------------------------------------------------------------------------

    [PostgresFact]
    public async Task A_deleted_accounts_token_gets_401_on_every_protected_route_and_never_a_500()
    {
        var person = await UserAsync();
        var client = api.Client(person.Token);
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/auth/me/refresh", null)).StatusCode);

        await DeleteThroughApiAsync(client, person.Email!);

        // Every route the API has that asks for a permission a public caller lacks. Asked of the
        // framework, so a route added later is covered without anyone remembering to list it.
        var publicPermissions = Roles.Definitions[Roles.Public].ToHashSet();
        var endpoints = api.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>().ToList();
        var checkedRoutes = 0;
        foreach (var endpoint in endpoints)
        {
            var permissions = endpoint.Metadata.GetOrderedMetadata<HasPermissionAttribute>();
            if (permissions.Count == 0) continue;
            var route = endpoint.RoutePattern;
            var path = "/" + string.Join('/', route.PathSegments.Select(s => string.Concat(s.Parts.Select(p => p switch
            {
                Microsoft.AspNetCore.Routing.Patterns.RoutePatternLiteralPart l => l.Content,
                _ => Guid.NewGuid().ToString(),
            }))));
            var verbs = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"];
            foreach (var verb in verbs.Where(v => v != "OPTIONS"))
            {
                using var request = new HttpRequestMessage(new HttpMethod(verb), path);
                if (verb is "POST" or "PUT" or "PATCH") request.Content = JsonContent.Create(new { });
                var status = (await client.SendAsync(request)).StatusCode;
                Assert.True((int)status < 500, $"{verb} {path} answered {(int)status}");
                if (!permissions.Any(p => publicPermissions.Contains(p.Permission)))
                {
                    // 415: a form-upload route turning away a JSON body at routing, before any identity is looked at.
                    Assert.True(status is HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests or HttpStatusCode.UnsupportedMediaType, $"{verb} {path} answered {(int)status}");
                    checkedRoutes++;
                }
            }
        }
        Assert.True(checkedRoutes > 50, $"Only {checkedRoutes} protected routes were checked.");
    }

    // ----- Payments ----------------------------------------------------------------------------

    [PostgresFact]
    public async Task Payments_survive_with_no_link_to_the_person_and_renewal_is_stopped()
    {
        var person = await UserAsync(subscription: SubscriptionTier.Premium, autoRenew: true, customer: $"cus_{Guid.NewGuid():N}", endsAt: DateTimeOffset.UtcNow.AddHours(2));
        var ev = await EventAsync(person, title: "A very personal title");
        var paid = await PaymentAsync(person.Id, ev, PaymentStatus.Paid, "Platinum pass · A very personal title", PaymentKind.PlatinumPass);
        var plan = await PaymentAsync(person.Id, null, PaymentStatus.Paid, "Premium pass, a month", PaymentKind.PremiumMonthly);
        var pending = await PaymentAsync(person.Id, null, PaymentStatus.Pending, "Venue, a month", PaymentKind.VenueMonthly);
        var refund = Guid.NewGuid();
        await api.WriteAsync(async db =>
        {
            db.Refunds.Add(new Refund { Id = refund, PaymentId = paid, Amount = 1, CreatedAt = DateTimeOffset.UtcNow });
            await Task.CompletedTask;
        });

        await DeleteThroughApiAsync(api.Client(person.Token), person.Email!);

        await api.ReadAsync(async db =>
        {
            var row = await db.Payments.SingleAsync(p => p.Id == paid);
            Assert.Null(row.UserId);
            Assert.Equal(PaymentStatus.Paid, row.Status);
            Assert.Equal(699m, row.Amount);
            Assert.Equal("MVR", row.Currency);
            Assert.Equal("tx-1", row.ProviderPaymentId);
            Assert.Equal("Platinum pass", row.Description);
            Assert.Null((await db.Payments.SingleAsync(p => p.Id == plan)).UserId);
            Assert.True(await db.Refunds.AnyAsync(r => r.Id == refund));
            return true;
        });

        // The plan was due within the day. Nothing is left to charge.
        var charges = api.Bank.Charges.Count;
        using (var scope = api.Services.CreateScope())
        {
            await scope.ServiceProvider.GetRequiredService<ISubscriptionRenewalService>().RunOnceAsync(DateTimeOffset.UtcNow);
            // A payment still open when the account went can finish at the bank later: it must not fail.
            await scope.ServiceProvider.GetRequiredService<InvitesBlog.Infrastructure.Persistence.AppDbContext>().Payments
                .Where(p => p.Id == pending).ExecuteUpdateAsync(s => s.SetProperty(p => p.Status, PaymentStatus.Paid));
            await scope.ServiceProvider.GetRequiredService<IBillingService>().FulfilAsync(pending);
        }
        Assert.Equal(charges, api.Bank.Charges.Count);
        Assert.NotNull(await api.ReadAsync(db => db.Payments.Where(p => p.Id == pending).Select(p => p.FulfilledAt).FirstAsync()));
    }

    [PostgresFact]
    public async Task Renewal_is_stopped_for_good_even_when_the_rest_of_the_deletion_fails_and_a_retry_finishes_it()
    {
        var person = await UserAsync(subscription: SubscriptionTier.Premium, autoRenew: true, customer: $"cus_{Guid.NewGuid():N}");
        var ev = await EventAsync(person);
        var album = await AlbumAsync(ev, person.Id);
        var photo = await PhotoAsync(ev, album);
        var client = api.Client(person.Token);

        // The last statement of the transaction fails, after everything else has run.
        var trigger = $"block_{person.Id:N}";
        await api.WriteAsync(async db =>
        {
            await db.Database.ExecuteSqlRawAsync($"""
                CREATE FUNCTION {trigger}() RETURNS trigger AS $$ BEGIN
                  IF OLD.id = '{person.Id}' THEN RAISE EXCEPTION 'blocked by test'; END IF; RETURN OLD; END $$ LANGUAGE plpgsql;
                CREATE TRIGGER {trigger} BEFORE DELETE ON users FOR EACH ROW EXECUTE FUNCTION {trigger}();
                """);
        });
        var sent = await RequestCodeAsync(client);
        var failed = await client.PostAsJsonAsync("/api/auth/me/deletion/confirm", new { challengeId = sent.ChallengeId, code = api.Codes.CodeFor(person.Email!) });
        await api.WriteAsync(db => db.Database.ExecuteSqlRawAsync($"DROP TRIGGER {trigger} ON users; DROP FUNCTION {trigger}();"));
        Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode);

        await api.ReadAsync(async db =>
        {
            // All of it rolled back, and the account still works; only renewal stays off.
            var user = await db.Users.SingleAsync(u => u.Id == person.Id);
            Assert.Null(user.AutoRenewKind);
            Assert.True(await db.Campaigns.AnyAsync(c => c.Id == ev));
            Assert.True(await db.EventPhotos.AnyAsync(p => p.Id == photo.Id));
            Assert.False(await db.PendingRemovals.AnyAsync(r => r.Target.Contains(ev.ToString("N"))));
            return true;
        });
        Assert.True(await ExistsAsync(photo.Url));
        Assert.Equal(HttpStatusCode.OK, (await client.PostAsync("/api/auth/me/refresh", null)).StatusCode);

        await DeleteThroughApiAsync(client, person.Email!);
        Assert.False(await api.ReadAsync(db => db.Users.AnyAsync(u => u.Id == person.Id)));
    }

    // ----- Files -------------------------------------------------------------------------------

    [PostgresFact]
    public async Task A_failed_file_removal_is_retried_and_doesnt_bring_the_account_back()
    {
        var person = await UserAsync();
        var ev = await EventAsync(person);
        var album = await AlbumAsync(ev, person.Id);
        var photo = await PhotoAsync(ev, album);

        await DeleteThroughApiAsync(api.Client(person.Token), person.Email!);

        api.Storage.FailRemovals = true;
        try
        {
            await api.RunRemovalsAsync();
        }
        finally
        {
            api.Storage.FailRemovals = false;
        }
        Assert.False(await api.ReadAsync(db => db.Users.AnyAsync(u => u.Id == person.Id)));
        Assert.True(await ExistsAsync(photo.Url));
        Assert.True(await api.ReadAsync(db => db.PendingRemovals.AnyAsync(r => r.Target.Contains(album.ToString("N")) && r.Attempts == 1)));

        // Its wait over, the next sweep finishes the job.
        await api.WriteAsync(db => db.PendingRemovals.Where(r => r.Target.Contains(album.ToString("N")))
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.NextAttemptAt, DateTimeOffset.UtcNow.AddMinutes(-1))));
        await api.RunRemovalsAsync();
        Assert.False(await ExistsAsync(photo.Url));
        Assert.False(await api.ReadAsync(db => db.PendingRemovals.AnyAsync(r => r.Target.Contains(album.ToString("N")))));
    }

    // ----- Templates ---------------------------------------------------------------------------

    [PostgresFact]
    public async Task A_template_in_use_keeps_working_uncredited_and_an_unused_private_one_is_deleted()
    {
        var designer = await UserAsync(subscription: SubscriptionTier.Premium, displayName: "Designer Name");
        var other = await UserAsync();

        var used = await TemplateAsync(designer, TemplateVisibility.Public);
        var usedOwnOnly = await TemplateAsync(designer, TemplateVisibility.Public);
        var privateUnused = await TemplateAsync(designer, TemplateVisibility.Private);
        var forSomeone = await TemplateAsync(designer, TemplateVisibility.Private, assignedEmail: "someone-" + Guid.NewGuid().ToString("N") + "@example.com");
        var design = Guid.NewGuid();
        await api.WriteAsync(async db =>
        {
            db.TemplateDesigns.Add(new TemplateDesign { Id = design, OwnerUserId = designer.Id, Name = "D", SceneJson = "{}", Revision = 1, TemplateId = used.Id, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            db.TemplateDesignPublishes.Add(new TemplateDesignPublish { Id = Guid.NewGuid(), DesignId = design, UserId = designer.Id, TemplateId = used.Id, Version = "1.0.0", Visibility = "Public", PublishedAt = DateTimeOffset.UtcNow });
            await Task.CompletedTask;
        });

        var theirEvent = await EventAsync(other, template: used);
        var guest = await GuestAsync(theirEvent, "G", "g-" + Guid.NewGuid().ToString("N") + "@example.com");
        var invite = await InviteAsync(theirEvent, guest);
        await EventAsync(designer, template: usedOwnOnly);

        // A personal link is bound to the address it first opened from, so one address throughout.
        var guestPhone = api.Client(address: "10.77.0.1");
        Assert.Equal(HttpStatusCode.OK, (await guestPhone.GetAsync($"/api/invites/by-token/{invite:N}")).StatusCode);
        await DeleteThroughApiAsync(api.Client(designer.Token), designer.Email!);
        await api.RunRemovalsAsync();

        await api.ReadAsync(async db =>
        {
            var kept = await db.Templates.SingleAsync(t => t.Id == used.Id);
            Assert.Null(kept.DesignerUserId);
            Assert.Null(kept.DesignerName);
            Assert.False(kept.IsActive);

            Assert.False(await db.Templates.AnyAsync(t => t.Id == privateUnused.Id));
            // Only their own events used it, and those have gone.
            Assert.False(await db.Templates.AnyAsync(t => t.Id == usedOwnOnly.Id));

            var promised = await db.Templates.SingleAsync(t => t.Id == forSomeone.Id);
            Assert.Null(promised.DesignerUserId);
            Assert.True(promised.IsActive);
            Assert.NotNull(promised.AssignedEmail);

            Assert.False(await db.TemplateDesigns.AnyAsync(d => d.OwnerUserId == designer.Id));
            Assert.False(await db.TemplateDesignPublishes.AnyAsync(p => p.UserId == designer.Id));
            return true;
        });

        Assert.True(await ExistsAsync(used.File), "the package other events pin must stay");
        Assert.False(await ExistsAsync(privateUnused.File));
        Assert.False(await ExistsAsync(usedOwnOnly.File));

        var opened = await guestPhone.GetAsync($"/api/invites/by-token/{invite:N}");
        Assert.Equal(HttpStatusCode.OK, opened.StatusCode);
        var body = await opened.Content.ReadAsStringAsync();
        Assert.Contains(used.Slug, body);
        Assert.DoesNotContain("Designer Name", body);
    }

    // ----- Venues, and events someone else started ---------------------------------------------

    [PostgresFact]
    public async Task A_venue_owners_venue_and_staff_go_and_other_hosts_events_there_stay_with_a_fresh_lapse()
    {
        var owner = await UserAsync(subscription: SubscriptionTier.Venue);
        var host = await UserAsync();
        var venue = await VenueAsync(owner);
        var staffEmail = "staff-" + Guid.NewGuid().ToString("N") + "@example.com";
        await api.WriteAsync(async db =>
        {
            db.VenueStaff.Add(new VenueStaff { Id = Guid.NewGuid(), VenueId = venue, Email = staffEmail, CreatedAt = DateTimeOffset.UtcNow });
            await Task.CompletedTask;
        });
        // A year old: on its free cover alone, its photos would be removed at the next sweep.
        var theirs = await EventAsync(host, venueId: venue, eventAt: DateTimeOffset.UtcNow.AddYears(-1));
        var own = await EventAsync(owner, venueId: venue);

        await DeleteThroughApiAsync(api.Client(owner.Token), owner.Email!);

        await api.ReadAsync(async db =>
        {
            Assert.False(await db.Venues.AnyAsync(v => v.Id == venue));
            Assert.False(await db.VenueStaff.AnyAsync(s => s.VenueId == venue));
            Assert.False(await db.Campaigns.AnyAsync(c => c.Id == own));
            var kept = await db.Campaigns.SingleAsync(c => c.Id == theirs);
            Assert.Null(kept.VenueId);
            Assert.True(kept.KeepPhotosUntil > DateTimeOffset.UtcNow.AddMinutes(-5));
            return true;
        });
        // Their event never had a pass, so it is a free event: kept for good (owner, 2026-11-03).
        using var scope = api.Services.CreateScope();
        var plan = await scope.ServiceProvider.GetRequiredService<InvitesBlog.Application.Plans.IPlanService>().ForCampaignAsync(theirs);
        Assert.Equal(InvitesBlog.Application.Plans.MediaPhase.Active, plan.Phase);
        Assert.Null(plan.CoveredUntil);
    }

    [PostgresFact]
    public async Task A_staff_members_venue_event_passes_to_the_venue_owner_and_their_staff_link_goes()
    {
        var owner = await UserAsync(subscription: SubscriptionTier.Venue);
        var staff = await UserAsync();
        var venue = await VenueAsync(owner);
        await api.WriteAsync(async db =>
        {
            db.VenueStaff.Add(new VenueStaff { Id = Guid.NewGuid(), VenueId = venue, Email = staff.Email!, CreatedAt = DateTimeOffset.UtcNow });
            await Task.CompletedTask;
        });
        var ev = await EventAsync(staff, venueId: venue);
        var album = await AlbumAsync(ev, staff.Id);

        var summary = await api.Client(staff.Token).GetFromJsonAsync<Envelope<SummaryDto>>("/api/auth/me/deletion/summary", Web);
        Assert.Equal(0, summary!.Data.EventsDeleted);

        await DeleteThroughApiAsync(api.Client(staff.Token), staff.Email!);

        await api.ReadAsync(async db =>
        {
            Assert.Equal(owner.Id, (await db.Campaigns.SingleAsync(c => c.Id == ev)).CreatedByUserId);
            Assert.Equal(owner.Id, (await db.MediaBuckets.SingleAsync(b => b.Id == album)).OwnerUserId);
            Assert.False(await db.VenueStaff.AnyAsync(s => s.Email == staff.Email));
            return true;
        });
    }

    [PostgresFact]
    public async Task An_event_someone_else_started_naming_this_person_as_host_stays_theirs()
    {
        var creator = await UserAsync();
        var named = await UserAsync();
        var inviter = Guid.NewGuid();
        await api.WriteAsync(async db =>
        {
            db.Inviters.Add(new Inviter { Id = inviter, Name = "Named", PhoneE164 = Phone(), Email = named.Email!, CreatedAt = DateTimeOffset.UtcNow });
            await Task.CompletedTask;
        });
        var ev = await EventAsync(creator, inviterId: inviter);
        var album = await AlbumAsync(ev, named.Id);   // made while they had access

        await DeleteThroughApiAsync(api.Client(named.Token), named.Email!);

        await api.ReadAsync(async db =>
        {
            Assert.True(await db.Campaigns.AnyAsync(c => c.Id == ev));
            Assert.True(await db.Inviters.AnyAsync(i => i.Id == inviter));
            Assert.Equal(creator.Id, (await db.MediaBuckets.SingleAsync(b => b.Id == album)).OwnerUserId);
            return true;
        });
    }

    /// <summary>A new foreign key to users must be thought about here, not discovered when a deletion fails.</summary>
    [PostgresFact]
    public async Task Every_foreign_key_to_users_is_one_the_deletion_accounts_for()
    {
        var found = await api.ReadAsync(async db =>
        {
            var c = db.Database.GetDbConnection();
            await c.OpenAsync();
            await using var q = c.CreateCommand();
            q.CommandText = "select conrelid::regclass::text || '.' || a.attname from pg_constraint k join pg_attribute a on a.attrelid = k.conrelid and a.attnum = any(k.conkey) where k.contype = 'f' and k.confrelid = 'users'::regclass";
            var rows = new List<string>();
            await using var r = await q.ExecuteReaderAsync();
            while (await r.ReadAsync()) rows.Add(r.GetString(0));
            return rows;
        });
        Assert.Equal(
            new[] { "comment_likes.user_id", "content_reports.reporter_user_id", "device_tokens.user_id", "favourites.user_id", "pass_credits.owner_user_id", "post_comments.user_id", "post_likes.user_id", "template_designs.owner_user_id", "user_blocks.blocked_user_id", "user_blocks.blocker_user_id", "user_external_logins.user_id", "user_roles.user_id", "venues.owner_user_id" },
            found.Order(StringComparer.Ordinal));
    }

    [PostgresFact]
    public async Task The_audit_entry_holds_counts_and_nothing_personal()
    {
        var person = await UserAsync(displayName: "Very Identifiable Name");
        await EventAsync(person);
        var since = DateTimeOffset.UtcNow.AddSeconds(-1);

        await DeleteThroughApiAsync(api.Client(person.Token), person.Email!);

        var entries = await api.ReadAsync(db => db.AuditLogs.Where(a => a.Action == "account.delete" && a.CreatedAt >= since).ToListAsync());
        var mine = entries.Where(a => a.DataJson.Contains("events")).ToList();
        Assert.NotEmpty(mine);
        foreach (var entry in mine)
        {
            var text = entry.Actor + entry.DataJson;
            Assert.DoesNotContain(person.Id.ToString(), text);
            Assert.DoesNotContain(person.Email!, text);
            Assert.DoesNotContain("Identifiable", text);
        }
    }

    // ----- Helpers -----------------------------------------------------------------------------

    private async Task DeleteThroughApiAsync(HttpClient client, string contact)
    {
        var sent = await RequestCodeAsync(client);
        var done = await client.PostAsJsonAsync("/api/auth/me/deletion/confirm", new { challengeId = sent.ChallengeId, code = api.Codes.CodeFor(contact) });
        Assert.True(done.IsSuccessStatusCode, await done.Content.ReadAsStringAsync());
    }

    private static async Task<CodeDto> RequestCodeAsync(HttpClient client)
    {
        var res = await client.PostAsync("/api/auth/me/deletion/request", null);
        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        return (await res.Content.ReadFromJsonAsync<Envelope<CodeDto>>(Web))!.Data;
    }

    private async Task<long> UsedBytesAsync(Guid album) =>
        await api.ReadAsync(db => db.MediaBuckets.Where(b => b.Id == album).Select(b => b.UsedBytes).FirstAsync());

    private async Task<bool> ExistsAsync(string url) =>
        await api.Storage.GetAsync(api.Storage.KeyFor(url)!) is not null;

    private static string Phone() => $"+9607{Random.Shared.Next(100000, 999999)}";

    private sealed record Person(Guid Id, string? Email, string? Phone, string Token, string? Customer);
    private sealed record Stored(Guid Id, string Url, string ThumbUrl, string OriginalUrl);
    private sealed record TemplateRow(Guid Id, string Slug, string File);

    private async Task<Person> UserAsync(
        string? email = "default", string? phone = null, string? password = "not-a-real-hash", SubscriptionTier subscription = SubscriptionTier.None,
        bool autoRenew = false, string? customer = null, DateTimeOffset? endsAt = null, string displayName = "Test Person",
        string? role = null, string[]? roleNames = null)
    {
        var id = Guid.NewGuid();
        var mail = email == "default" ? $"u-{id:N}@example.com" : email;
        await api.WriteAsync(async db =>
        {
            var wanted = roleNames ?? [Roles.Customer];
            var ids = await db.Roles.Where(r => wanted.Contains(r.Name)).Select(r => r.Id).ToListAsync();
            var user = new AppUser
            {
                Id = id, Email = mail, PhoneE164 = phone, DisplayName = displayName, PasswordHash = password, IsActive = true,
                SubscriptionTier = subscription,
                SubscriptionEndsAt = endsAt ?? (subscription == SubscriptionTier.None ? null : DateTimeOffset.UtcNow.AddDays(20)),
                AutoRenewKind = autoRenew ? PaymentKind.PremiumMonthly : null,
                PaymentCustomerId = customer,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            foreach (var r in ids) user.UserRoles.Add(new UserRole { RoleId = r });
            db.Users.Add(user);
            await Task.CompletedTask;
        });
        var names = roleNames ?? [Roles.Customer];
        var token = api.TokenFor(id, mail is null ? "phone" : "email", mail ?? phone!, names);
        return new Person(id, mail, phone, token, customer);
    }

    private async Task<TemplateRow> TemplateAsync(Person designer, string visibility, string? assignedEmail = null)
    {
        var id = Guid.NewGuid();
        var slug = $"t-{id:N}";
        var file = await api.Storage.PutAsync($"templates/{slug}@1.0.0/index.html", [1], "text/html");
        await api.WriteAsync(async db =>
        {
            db.Templates.Add(new Template
            {
                Id = id, Name = "T", Slug = slug, Version = "1.0.0", Category = "Wedding", Description = "D", PreviewImageUrl = "",
                SceneJson = "{}", ManifestJson = "{}", PackageUrl = api.Storage.PublicUrl($"templates/{slug}@1.0.0/"), Visibility = visibility,
                AssignedEmail = assignedEmail, DesignerUserId = designer.Id, DesignerName = "Designer Name",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await Task.CompletedTask;
        });
        return new TemplateRow(id, slug, file);
    }

    private async Task<Guid> EventAsync(
        Person? creator, Guid? inviterId = null, Guid? venueId = null, TemplateRow? template = null,
        string title = "An event", DateTimeOffset? eventAt = null)
    {
        var id = Guid.NewGuid();
        var templateId = template?.Id ?? Guid.NewGuid();
        await api.WriteAsync(async db =>
        {
            if (template is null)
                db.Templates.Add(new Template
                {
                    Id = templateId, Name = title, Slug = $"own-{id:N}", Version = "1.0.0", Category = "Imported", Description = "D", PreviewImageUrl = "",
                    SceneJson = "{}", ManifestJson = "{}", PackageUrl = "", Visibility = TemplateVisibility.Imported,
                    CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
                });
            db.Campaigns.Add(new Campaign
            {
                Id = id, TemplateId = templateId, TemplateVersion = "1.0.0",
                TemplatePackageUrl = template is null ? "" : api.Storage.PublicUrl($"templates/{template.Slug}@1.0.0/"),
                InviterId = inviterId, CreatedByUserId = creator?.Id, VenueId = venueId, AccessTokenHash = Guid.NewGuid().ToString("N"),
                Title = title, Slug = $"e-{id:N}", Status = CampaignStatus.Dispatched,
                EventStartAt = eventAt ?? DateTimeOffset.UtcNow.AddDays(10),
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await Task.CompletedTask;
        });
        return id;
    }

    private async Task WritePassAsync(Guid ev, EventPassKind pass) =>
        await api.WriteAsync(db => db.Campaigns.Where(c => c.Id == ev).ExecuteUpdateAsync(s => s
            .SetProperty(c => c.EventPass, pass).SetProperty(c => c.EventPassUntil, DateTimeOffset.UtcNow.AddMonths(6))));

    private async Task<Guid> GuestAsync(Guid ev, string name, string? email)
    {
        var id = Guid.NewGuid();
        await api.WriteAsync(async db =>
        {
            db.Guests.Add(new Guest { Id = id, CampaignId = ev, Name = name, Email = email, CreatedAt = DateTimeOffset.UtcNow });
            await Task.CompletedTask;
        });
        return id;
    }

    /// <summary>The invite's token, for opening its link, is its own id without dashes.</summary>
    private async Task<Guid> InviteAsync(Guid ev, Guid guest)
    {
        var id = Guid.NewGuid();
        await api.WriteAsync(async db =>
        {
            db.Invites.Add(new Invite { Id = id, CampaignId = ev, GuestId = guest, TokenHash = InvitesBlog.Application.Security.TokenService.Hash(id.ToString("N")), CreatedAt = DateTimeOffset.UtcNow });
            await Task.CompletedTask;
        });
        return id;
    }

    private async Task<Guid> AlbumAsync(Guid ev, Guid owner)
    {
        var id = Guid.NewGuid();
        await api.WriteAsync(async db =>
        {
            db.MediaBuckets.Add(new MediaBucket { Id = id, CampaignId = ev, OwnerUserId = owner, EventDate = DateTimeOffset.UtcNow, CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            await Task.CompletedTask;
        });
        return id;
    }

    private async Task<Stored> PhotoAsync(
        Guid ev, Guid album, Guid? guestId = null, string? name = null, string contentType = "image/jpeg",
        long bytes = 10, bool deleted = false, bool counted = true)
    {
        var id = Guid.NewGuid();
        var url = await api.Storage.PutAsync($"buckets/{album:N}/media/{id:N}.jpg", [1], contentType);
        var thumb = await api.Storage.PutAsync($"buckets/{album:N}/media/{id:N}_t.jpg", [1], "image/jpeg");
        var original = await api.Storage.PutAsync($"buckets/{album:N}/media/{id:N}_o.jpg", [1], "image/jpeg");
        await api.WriteAsync(async db =>
        {
            db.EventPhotos.Add(new EventPhoto
            {
                Id = id, CampaignId = ev, BucketId = album, GuestId = guestId, UploaderName = name,
                Url = url, ThumbUrl = thumb, OriginalUrl = original, ContentType = contentType, SizeBytes = bytes,
                DeletedAt = deleted ? DateTimeOffset.UtcNow : null, CreatedAt = DateTimeOffset.UtcNow,
            });
            await db.SaveChangesAsync();
            if (counted && !deleted)
                await db.MediaBuckets.Where(b => b.Id == album).ExecuteUpdateAsync(s => s.SetProperty(b => b.UsedBytes, b => b.UsedBytes + bytes));
        });
        return new Stored(id, url, thumb, original);
    }

    private async Task<string> QrAsync(Guid album)
    {
        var id = Guid.NewGuid();
        var image = await api.Storage.PutAsync($"buckets/{album:N}/qr/{id:N}.png", [1], "image/png");
        await api.WriteAsync(async db =>
        {
            db.MediaBucketQrs.Add(new MediaBucketQr { Id = id, BucketId = album, TokenHash = id.ToString("N"), ImageUrl = image, TokenHint = "abc", CreatedAt = DateTimeOffset.UtcNow });
            await Task.CompletedTask;
        });
        return image;
    }

    private async Task<string> AssetAsync(Guid ev)
    {
        var url = await api.Storage.PutAsync($"campaigns/{ev:N}/images/{Guid.NewGuid():N}.jpg", [1], "image/jpeg");
        await api.WriteAsync(async db =>
        {
            db.CampaignAssets.Add(new CampaignAsset { Id = Guid.NewGuid(), CampaignId = ev, Url = url, ContentType = "image/jpeg", SizeBytes = 1, CreatedAt = DateTimeOffset.UtcNow });
            await Task.CompletedTask;
        });
        return url;
    }

    private async Task FeedActivityOnAsync(Guid ev, Guid user) =>
        await api.WriteAsync(async db =>
        {
            db.PostComments.Add(new PostComment { Id = Guid.NewGuid(), CampaignId = ev, UserId = user, Body = "Nice", CreatedAt = DateTimeOffset.UtcNow });
            db.PostLikes.Add(new PostLike { Id = Guid.NewGuid(), CampaignId = ev, UserId = user, CreatedAt = DateTimeOffset.UtcNow });
            await Task.CompletedTask;
        });

    private async Task<Guid> VenueAsync(Person owner)
    {
        var id = Guid.NewGuid();
        await api.WriteAsync(async db =>
        {
            db.Venues.Add(new Venue { Id = id, OwnerUserId = owner.Id, Name = "A venue", CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow });
            await Task.CompletedTask;
        });
        return id;
    }

    private async Task<Guid> PaymentAsync(Guid user, Guid? ev, PaymentStatus status, string description, PaymentKind kind)
    {
        var id = Guid.NewGuid();
        await api.WriteAsync(async db =>
        {
            db.Payments.Add(new Payment
            {
                Id = id, UserId = user, CampaignId = ev, Kind = kind, Amount = 699m, Currency = "MVR", Status = status, Provider = "Fake",
                Description = description, ProviderPaymentId = "tx-1", CreatedAt = DateTimeOffset.UtcNow,
            });
            await Task.CompletedTask;
        });
        return id;
    }

    private sealed record Envelope<T>(T Data);
    private sealed record SummaryDto(int EventsDeleted, int EventsPassedOn, int Albums, int Photos, int Videos, List<string> Paid, bool AutoRenews, string? Blocked, string CodeChannel, string CodeSentTo);
    private sealed record CodeDto(Guid ChallengeId, string Channel, string SentTo, int ExpiresInSeconds);
    private sealed record CampaignRow(Guid Id);
    private sealed record TokenDto(string Token);
}
