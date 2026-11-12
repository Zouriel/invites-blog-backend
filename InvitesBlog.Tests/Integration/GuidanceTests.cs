using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InvitesBlog.Application.Services.Guidance;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using InvitesBlog.Infrastructure.Demo;
using InvitesBlog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace InvitesBlog.Tests.Integration;

[CollectionDefinition("guidance")]
public sealed class GuidanceCollection : ICollectionFixture<PostgresApiFixture>;

/// <summary>
/// Tips, tours, offers and the demo event (docs/GUIDANCE.md), through the real API on real Postgres.
/// </summary>
[Collection("guidance")]
public sealed class GuidanceTests(PostgresApiFixture api)
{
    private sealed record Person(Guid Id, string Email, string Name, string Token);

    // ----- Auth -----------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Every_guidance_route_needs_an_account_but_the_demo_does_not()
    {
        var anon = api.Client();
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/api/me/guidance")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsJsonAsync("/api/me/guidance/seen", new { ids = new[] { "tip.a" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PutAsJsonAsync("/api/me/guidance", new { tipsOn = false })).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync("/api/me/guidance/reset", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.PostAsync($"/api/me/roles/{Guid.NewGuid()}/ack", null)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await anon.GetAsync("/api/demo/event")).StatusCode);
    }

    // ----- State ----------------------------------------------------------------------------------

    [PostgresFact]
    public async Task A_new_account_has_tips_on_nothing_seen_and_a_30_day_welcome()
    {
        var me = await UserAsync("Mariyam");
        var g = await GuidanceAsync(me);
        Assert.True(g.GetProperty("tipsOn").GetBoolean());
        Assert.Empty(g.GetProperty("seen").EnumerateArray());
        var created = await api.ReadAsync(db => db.Users.Where(u => u.Id == me.Id).Select(u => u.CreatedAt).SingleAsync());
        Assert.Equal(created.AddDays(30), g.GetProperty("welcomeUntil").GetDateTimeOffset(), TimeSpan.FromSeconds(1));
        Assert.Empty(g.GetProperty("newRoles").EnumerateArray());
    }

    [PostgresFact]
    public async Task Seen_ids_are_kept_once_in_order_and_survive_on_the_account()
    {
        var me = await UserAsync("Seen");
        Assert.Equal(HttpStatusCode.NoContent, (await SeenAsync(me, "offer.welcome", "tip.feed.templates")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SeenAsync(me, "tip.feed.templates", "tour.welcome", "tour.welcome")).StatusCode);
        Assert.Equal(["offer.welcome", "tip.feed.templates", "tour.welcome"], await SeenListAsync(me));
        Assert.Equal(HttpStatusCode.NoContent, (await SeenAsync(me)).StatusCode); // an empty list is fine
        Assert.Equal(["offer.welcome", "tip.feed.templates", "tour.welcome"],
            await api.ReadAsync(db => db.Users.Where(u => u.Id == me.Id).Select(u => u.GuidanceSeen).SingleAsync()));
    }

    [PostgresTheory]
    [InlineData("Tip.Upper")]
    [InlineData("tip feed")]
    [InlineData("tip_under")]
    [InlineData("")]
    [InlineData("tip/slash")]
    [InlineData("tïp")]
    public async Task A_bad_id_is_refused_and_nothing_from_that_call_is_kept(string bad)
    {
        var me = await UserAsync("Bad");
        var res = await SeenAsync(me, "tip.good", bad);
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Empty(await SeenListAsync(me));
    }

    [PostgresFact]
    public async Task Ids_are_at_most_64_characters_and_50_per_call()
    {
        var me = await UserAsync("Bounds");
        Assert.Equal(HttpStatusCode.NoContent, (await SeenAsync(me, new string('a', 64))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SeenAsync(me, new string('a', 65))).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await SeenAsync(me, Ids(0, 50))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await SeenAsync(me, Ids(100, 51))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest,
            (await api.Client(me.Token).PostAsJsonAsync("/api/me/guidance/seen", new { })).StatusCode);
        Assert.Equal(51, (await SeenListAsync(me)).Count);
    }

    [PostgresFact]
    public async Task At_most_500_are_stored_and_the_oldest_go_first()
    {
        var me = await UserAsync("Many");
        for (var batch = 0; batch < 11; batch++)
            Assert.Equal(HttpStatusCode.NoContent, (await SeenAsync(me, Ids(batch * 50, 50))).StatusCode);
        var seen = await SeenListAsync(me);
        Assert.Equal(GuidanceRules.MaxStored, seen.Count);
        Assert.Equal("t.50", seen[0]);
        Assert.Equal("t.549", seen[^1]);
        Assert.DoesNotContain("t.0", seen);
    }

    [PostgresFact]
    public async Task Tips_can_be_switched_off_and_reset_brings_everything_back()
    {
        var me = await UserAsync("Tips");
        await SeenAsync(me, "offer.welcome", "tour.welcome");
        Assert.Equal(HttpStatusCode.NoContent, (await api.Client(me.Token).PutAsJsonAsync("/api/me/guidance", new { tipsOn = false })).StatusCode);
        Assert.False((await GuidanceAsync(me)).GetProperty("tipsOn").GetBoolean());
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Client(me.Token).PutAsJsonAsync("/api/me/guidance", new { })).StatusCode);
        Assert.False((await GuidanceAsync(me)).GetProperty("tipsOn").GetBoolean());

        Assert.Equal(HttpStatusCode.NoContent, (await api.Client(me.Token).PostAsync("/api/me/guidance/reset", null)).StatusCode);
        var g = await GuidanceAsync(me);
        Assert.True(g.GetProperty("tipsOn").GetBoolean());
        Assert.Empty(g.GetProperty("seen").EnumerateArray());

        Assert.Equal(HttpStatusCode.NoContent, (await api.Client(me.Token).PutAsJsonAsync("/api/me/guidance", new { tipsOn = false })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await api.Client(me.Token).PutAsJsonAsync("/api/me/guidance", new { tipsOn = true })).StatusCode);
        Assert.True((await GuidanceAsync(me)).GetProperty("tipsOn").GetBoolean());
    }

    // ----- Progress -------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Progress_starts_all_false_for_a_placeholder_name()
    {
        var id = Guid.NewGuid();
        var mail = $"aishath.{id:N}@example.com";
        var me = await UserAsync(mail.Split('@')[0], mail, id);
        var p = (await GuidanceAsync(me)).GetProperty("progress");
        foreach (var flag in new[] { "hasName", "hasPhoto", "hasEvent", "hasGuests", "hasSent", "hasPhotoInAlbum", "hasDesign" })
            Assert.False(p.GetProperty(flag).GetBoolean(), flag);
    }

    [PostgresFact]
    public async Task Progress_follows_what_the_host_did()
    {
        var me = await UserAsync("Aisha Ahmed");
        await api.WriteAsync(async db =>
        {
            await db.Users.Where(u => u.Id == me.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.AvatarUrl, "http://localhost/assets/a.webp"));
        });
        var p = await ProgressAsync(me);
        Assert.True(p["hasName"]);
        Assert.True(p["hasPhoto"]);
        Assert.False(p["hasEvent"]);

        var ev = await EventAsync(me);
        p = await ProgressAsync(me);
        Assert.True(p["hasEvent"]);
        Assert.False(p["hasGuests"]);
        Assert.False(p["hasSent"]);

        var guest = await GuestAsync(ev, "Hawwa", $"h-{Guid.NewGuid():N}@example.com");
        await InviteAsync(ev, guest, InviteStatus.Created);
        p = await ProgressAsync(me);
        Assert.True(p["hasGuests"]);
        Assert.False(p["hasSent"]);  // added, not sent

        await api.WriteAsync(async db =>
            await db.Invites.Where(i => i.CampaignId == ev).ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, InviteStatus.Sent)));
        Assert.True((await ProgressAsync(me))["hasSent"]);

        var photo = await PhotoAsync(ev);
        Assert.True((await ProgressAsync(me))["hasPhotoInAlbum"]);
        await api.WriteAsync(async db =>
            await db.EventPhotos.Where(x => x.Id == photo).ExecuteUpdateAsync(s => s.SetProperty(x => x.DeletedAt, DateTimeOffset.UtcNow)));
        Assert.False((await ProgressAsync(me))["hasPhotoInAlbum"]);  // deleted media doesn't count

        Assert.False((await ProgressAsync(me))["hasDesign"]);
        await api.WriteAsync(async db =>
        {
            db.TemplateDesigns.Add(new TemplateDesign
            {
                Id = Guid.NewGuid(), OwnerUserId = me.Id, Name = "Mine", SceneJson = "{}",
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await Task.CompletedTask;
        });
        Assert.True((await ProgressAsync(me))["hasDesign"]);
    }

    [PostgresFact]
    public async Task An_open_link_or_a_hand_send_counts_as_sent()
    {
        var linked = await UserAsync("Linked");
        var ev = await EventAsync(linked);
        Assert.False((await ProgressAsync(linked))["hasSent"]);
        await api.WriteAsync(async db =>
            await db.Campaigns.Where(c => c.Id == ev).ExecuteUpdateAsync(s => s.SetProperty(c => c.OpenLinkCode, $"c{Guid.NewGuid():N}"[..12])));
        Assert.True((await ProgressAsync(linked))["hasSent"]);

        var hand = await UserAsync("Hand");
        var ev2 = await EventAsync(hand);
        var invite = await InviteAsync(ev2, await GuestAsync(ev2, "Ali", null), InviteStatus.NotSent);
        Assert.False((await ProgressAsync(hand))["hasSent"]);
        await api.WriteAsync(async db =>
        {
            db.DeliveryAttempts.Add(new DeliveryAttempt
            {
                Id = Guid.NewGuid(), InviteId = invite, Channel = "whatsapp-manual", RecipientAddress = "+9607000000",
                Status = DeliveryStatus.Sent, AttemptedAt = DateTimeOffset.UtcNow,
            });
            await Task.CompletedTask;
        });
        Assert.True((await ProgressAsync(hand))["hasSent"]);
    }

    [PostgresFact]
    public async Task A_celebrant_event_is_not_a_hosted_one()
    {
        var host = await UserAsync("Host");
        var me = await UserAsync("Couple");
        var ev = await EventAsync(host);
        await GuestAsync(ev, "Guest", null);
        await CelebrantAsync(ev, me.Email, acknowledged: true);
        var p = await ProgressAsync(me);
        Assert.False(p["hasEvent"]);
        Assert.False(p["hasGuests"]);
    }

    // ----- New roles ------------------------------------------------------------------------------

    [PostgresFact]
    public async Task A_celebrant_added_by_email_is_told_once_and_the_card_goes_after_ack()
    {
        var host = await UserAsync("Mariyam Hassan");
        var me = await UserAsync("Aisha");
        var ev = await EventAsync(host, "Aisha & Ibrahim");

        // Added the real way, by the organiser.
        var add = await api.Client(host.Token).PostAsJsonAsync($"/api/campaigns/{ev}/celebrants",
            new { name = "Aisha", email = me.Email.ToUpperInvariant(), canManage = false });
        Assert.True(add.IsSuccessStatusCode, await add.Content.ReadAsStringAsync());

        var roles = (await GuidanceAsync(me)).GetProperty("newRoles").EnumerateArray().ToList();
        var role = Assert.Single(roles);
        Assert.Equal(ev, role.GetProperty("campaignId").GetGuid());
        Assert.Equal("Aisha & Ibrahim", role.GetProperty("title").GetString());
        Assert.Equal("look", role.GetProperty("access").GetString());
        Assert.Equal("Mariyam Hassan", role.GetProperty("addedBy").GetString());
        Assert.True(role.TryGetProperty("coverUrl", out _));
        Assert.True(role.GetProperty("addedAt").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(-5));

        // The organiser isn't told about their own event, and a stranger's ack changes nothing.
        Assert.Empty((await GuidanceAsync(host)).GetProperty("newRoles").EnumerateArray());
        var stranger = await UserAsync("Stranger");
        Assert.Equal(HttpStatusCode.NoContent, (await api.Client(stranger.Token).PostAsync($"/api/me/roles/{ev}/ack", null)).StatusCode);
        Assert.Single((await GuidanceAsync(me)).GetProperty("newRoles").EnumerateArray());

        Assert.Equal(HttpStatusCode.NoContent, (await api.Client(me.Token).PostAsync($"/api/me/roles/{ev}/ack", null)).StatusCode);
        Assert.Empty((await GuidanceAsync(me)).GetProperty("newRoles").EnumerateArray());
        Assert.Equal(HttpStatusCode.NoContent, (await api.Client(me.Token).PostAsync($"/api/me/roles/{ev}/ack", null)).StatusCode);
    }

    [PostgresFact]
    public async Task Opening_the_dashboard_as_the_celebrant_acknowledges_it()
    {
        var host = await UserAsync("Host Name");
        var me = await UserAsync("Couple");
        var ev = await EventAsync(host, "Our day");
        await CelebrantAsync(ev, me.Email, canManage: true);
        var role = Assert.Single((await GuidanceAsync(me)).GetProperty("newRoles").EnumerateArray());
        Assert.Equal("full", role.GetProperty("access").GetString());

        // The organiser opening it doesn't count.
        Assert.Equal(HttpStatusCode.OK, (await api.Client(host.Token).GetAsync($"/api/me/campaigns/{ev}/dashboard")).StatusCode);
        Assert.Single((await GuidanceAsync(me)).GetProperty("newRoles").EnumerateArray());

        var open = await api.Client(me.Token).GetAsync($"/api/me/campaigns/{ev}/dashboard");
        Assert.Equal(HttpStatusCode.OK, open.StatusCode);
        Assert.Empty((await GuidanceAsync(me)).GetProperty("newRoles").EnumerateArray());
        Assert.NotNull(await api.ReadAsync(db => db.CampaignCelebrants.Where(c => c.CampaignId == ev).Select(c => c.AcknowledgedAt).SingleAsync()));
    }

    [PostgresFact]
    public async Task The_plain_dashboard_route_acknowledges_too_and_a_phone_match_counts()
    {
        var host = await UserAsync("Host");
        var phone = $"+9607{Random.Shared.Next(100000, 999999)}";
        var me = await UserAsync("By phone");
        await api.WriteAsync(async db =>
            await db.Users.Where(u => u.Id == me.Id).ExecuteUpdateAsync(s => s.SetProperty(u => u.PhoneE164, phone)));
        var ev = await EventAsync(host, "Phone event");
        await api.WriteAsync(async db =>
        {
            db.CampaignCelebrants.Add(new CampaignCelebrant { Id = Guid.NewGuid(), CampaignId = ev, Name = "P", PhoneE164 = phone, CreatedAt = DateTimeOffset.UtcNow });
            await Task.CompletedTask;
        });
        Assert.Single((await GuidanceAsync(me)).GetProperty("newRoles").EnumerateArray());
        Assert.Equal(HttpStatusCode.OK, (await api.Client(me.Token).GetAsync($"/api/dashboard/{ev}")).StatusCode);
        Assert.Empty((await GuidanceAsync(me)).GetProperty("newRoles").EnumerateArray());
    }

    [PostgresFact]
    public async Task Acknowledged_and_cancelled_events_are_not_listed()
    {
        var host = await UserAsync("Host");
        var me = await UserAsync("Couple");
        var known = await EventAsync(host, "Known");
        await CelebrantAsync(known, me.Email, acknowledged: true);
        var cancelled = await EventAsync(host, "Off");
        await CelebrantAsync(cancelled, me.Email);
        await api.WriteAsync(async db =>
            await db.Campaigns.Where(c => c.Id == cancelled).ExecuteUpdateAsync(s => s.SetProperty(c => c.Status, CampaignStatus.Cancelled)));
        Assert.Empty((await GuidanceAsync(me)).GetProperty("newRoles").EnumerateArray());
    }

    /// <summary>
    /// The migration marks every celebrant that already existed as acknowledged, so nobody gets a card
    /// for an event they've known about for months. Run on a database of its own, with data in it.
    /// </summary>
    [PostgresFact]
    public async Task Celebrants_from_before_the_migration_are_already_acknowledged()
    {
        var server = Environment.GetEnvironmentVariable(PostgresApiFixture.ServerVariable)!;
        var name = $"ib_guid_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(server) { Database = "postgres" }.ToString()))
        {
            await admin.OpenAsync();
            await new NpgsqlCommand($"CREATE DATABASE {name}", admin).ExecuteNonQueryAsync();
        }
        var conn = new NpgsqlConnectionStringBuilder(server) { Database = name }.ToString();
        try
        {
            await using (var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(conn).Options))
            {
                var migrator = db.GetService<IMigrator>();
                var all = db.Database.GetMigrations().ToList();
                var guidance = all.Single(m => m.EndsWith("_AddGuidance"));
                await migrator.MigrateAsync(all[all.IndexOf(guidance) - 1]);
                await db.Database.ExecuteSqlRawAsync("""
                    INSERT INTO templates (id,name,slug,version,category,description,preview_image_url,scene_json,manifest_json,package_url,is_active,created_at)
                      VALUES ('33333333-3333-4333-8333-333333333333','T','t','1.0.0','Imported','D','','{{}}','{{}}','',true,now());
                    INSERT INTO campaigns (id,template_id,template_version,access_token_hash,title,slug,status,event_type,event_start_at,paid_invite_capacity,is_sensitive,retention_days,custom_content_json,theme_overrides_json,delivery_settings_json,rules_json,created_at,updated_at)
                      VALUES ('44444444-4444-4444-8444-444444444444','33333333-3333-4333-8333-333333333333','1.0.0','x','Old','old',6,'Wedding',now(),0,false,90,'{{}}','{{}}','{{}}','{{"rules":[]}}',now(),now());
                    INSERT INTO campaign_celebrants (id,campaign_id,name,email,can_manage,created_at)
                      VALUES ('55555555-5555-4555-8555-555555555555','44444444-4444-4444-8444-444444444444','Old','old@example.com',false,now());
                    """);
                await migrator.MigrateAsync(guidance);
            }
            await using (var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(conn).Options))
            {
                Assert.NotNull(await db.CampaignCelebrants.Select(c => c.AcknowledgedAt).SingleAsync());
                // A row added after the migration starts unacknowledged.
                db.CampaignCelebrants.Add(new CampaignCelebrant
                {
                    Id = Guid.NewGuid(), CampaignId = Guid.Parse("44444444-4444-4444-8444-444444444444"),
                    Name = "New", Email = "new@example.com", CreatedAt = DateTimeOffset.UtcNow,
                });
                await db.SaveChangesAsync();
                Assert.Null(await db.CampaignCelebrants.Where(c => c.Email == "new@example.com").Select(c => c.AcknowledgedAt).SingleAsync());
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(server) { Database = "postgres" }.ToString());
            await admin.OpenAsync();
            await new NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)", admin).ExecuteNonQueryAsync();
        }
    }

    // ----- Demo -----------------------------------------------------------------------------------

    [PostgresFact]
    public async Task The_demo_event_is_the_dashboard_shape_with_fixed_sample_content()
    {
        var res = await api.Client().GetAsync("/api/demo/event");
        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.True(res.Headers.CacheControl?.Public);
        var d = await DataAsync(res);

        Assert.True(d.GetProperty("demo").GetBoolean());
        var campaign = d.GetProperty("campaign");
        Assert.Equal("Aisha & Ibrahim", campaign.GetProperty("title").GetString());
        Assert.Equal("organiser", campaign.GetProperty("viewer").GetString());
        var startsAt = campaign.GetProperty("eventStartAt").GetDateTimeOffset();
        Assert.InRange((startsAt - DateTimeOffset.UtcNow).TotalDays, 19, 23);
        Assert.False(string.IsNullOrEmpty(campaign.GetProperty("coverImageUrl").GetString()));

        var guests = d.GetProperty("guests").EnumerateArray().ToList();
        Assert.Equal(24, guests.Count);
        var replies = guests.GroupBy(g => g.GetProperty("rsvpStatus").GetString()!).ToDictionary(g => g.Key, g => g.Count());
        foreach (var kind in new[] { "Going", "Maybe", "NotGoing", "NoResponse" }) Assert.True(replies.GetValueOrDefault(kind) > 0, kind);
        Assert.Contains(guests, g => g.GetProperty("viewedAt").ValueKind == JsonValueKind.Null);
        var report = d.GetProperty("report");
        Assert.Equal(24, report.GetProperty("total").GetInt32());
        Assert.Equal(replies["Going"], report.GetProperty("rsvp").GetProperty("going").GetInt32());

        Assert.Equal(2, d.GetProperty("celebrants").GetArrayLength());
        var bucket = Assert.Single(d.GetProperty("buckets").EnumerateArray());
        var album = d.GetProperty("album");
        var photos = album.GetProperty("photos").EnumerateArray().ToList();
        Assert.InRange(photos.Count, 10, 14);
        Assert.Equal(photos.Count, album.GetProperty("count").GetInt32());
        Assert.Equal(photos.Count, bucket.GetProperty("itemCount").GetInt32());
        Assert.Equal(2, d.GetProperty("covers").GetProperty("photoIds").GetArrayLength());

        // Every photo is published where its URL says.
        foreach (var p in photos)
            foreach (var key in new[] { "url", "thumbUrl" })
            {
                var url = p.GetProperty(key).GetString()!;
                Assert.Contains("/demo/v1/", url);
                var file = Path.Combine(api.StorageRoot, url[(url.IndexOf("/demo/v1/", StringComparison.Ordinal) + 1)..]);
                Assert.True(File.Exists(file), file);
            }
    }

    [PostgresFact]
    public async Task Nothing_in_the_demo_points_at_anything_real_or_writable()
    {
        var host = await UserAsync("Signed in");
        var raw = await (await api.Client().GetAsync("/api/demo/event")).Content.ReadAsStringAsync();
        var d = JsonDocument.Parse(raw).RootElement.GetProperty("data");

        // Every id is a demo id, and none exists anywhere.
        var ids = new List<Guid>();
        Collect(d, ids);
        Assert.True(ids.Count > 40);
        Assert.All(ids, id => Assert.StartsWith(DemoEventBuilder.IdPrefix, id.ToString()));
        Assert.False(await api.ReadAsync(db => db.Campaigns.AnyAsync(c => ids.Contains(c.Id))));

        // No tokens, links or contacts; nothing may be added or removed.
        var campaign = d.GetProperty("campaign");
        Assert.Equal(JsonValueKind.Null, campaign.GetProperty("openLink").ValueKind);
        foreach (var g in d.GetProperty("guests").EnumerateArray())
            foreach (var field in new[] { "email", "phoneE164", "shareLink", "shareText", "typedContact" })
                Assert.Equal(JsonValueKind.Null, g.GetProperty(field).ValueKind);
        foreach (var c in d.GetProperty("celebrants").EnumerateArray())
            foreach (var field in new[] { "email", "phone" })
                Assert.Equal(JsonValueKind.Null, c.GetProperty(field).ValueKind);
        Assert.False(d.GetProperty("album").GetProperty("canUpload").GetBoolean());
        Assert.All(d.GetProperty("album").GetProperty("photos").EnumerateArray(), p => Assert.False(p.GetProperty("canDelete").GetBoolean()));
        Assert.DoesNotContain("token", raw, StringComparison.OrdinalIgnoreCase);

        // Writing to it, even signed in, finds nothing.
        var id = campaign.GetProperty("id").GetGuid();
        var client = api.Client(host.Token);
        foreach (var res in new[]
        {
            await client.PostAsJsonAsync($"/api/campaigns/{id}/guests", new { name = "X", email = "x@example.com" }),
            await client.PutAsJsonAsync($"/api/campaigns/{id}/title", new { title = "Mine now" }),
            await client.PostAsJsonAsync($"/api/campaigns/{id}/celebrants", new { name = "X", email = "x@example.com" }),
            await client.DeleteAsync($"/api/campaigns/{id}"),
        })
            Assert.False(res.IsSuccessStatusCode, $"{res.RequestMessage!.Method} {res.RequestMessage.RequestUri} answered {(int)res.StatusCode}");
    }

    // ----- Unit-level -----------------------------------------------------------------------------

    [Fact]
    public void Merge_keeps_first_order_and_trims_the_oldest()
    {
        Assert.Equal(["a", "b", "c"], GuidanceRules.Merge(["a", "b"], ["b", "c", "c"]));
        var full = Enumerable.Range(0, GuidanceRules.MaxStored).Select(i => $"x{i}").ToList();
        var merged = GuidanceRules.Merge(full, ["new"]);
        Assert.Equal(GuidanceRules.MaxStored, merged.Count);
        Assert.Equal("x1", merged[0]);
        Assert.Equal("new", merged[^1]);
    }

    [Theory]
    [InlineData("Aisha Ahmed", "aisha@example.com", null, true)]
    [InlineData("aisha", "aisha@example.com", null, false)]
    [InlineData("You", "x@privaterelay.appleid.com", null, false)]
    [InlineData("invites.blog member", "x@example.com", null, false)]
    [InlineData("+9607001234", null, "+9607001234", false)]
    [InlineData("  ", "x@example.com", null, false)]
    public void Placeholder_names_do_not_count(string name, string? email, string? phone, bool counts) =>
        Assert.Equal(counts, GuidanceRules.HasOwnName(new AppUser { DisplayName = name, Email = email, PhoneE164 = phone }));

    // ----- Helpers --------------------------------------------------------------------------------

    private static void Collect(JsonElement e, List<Guid> ids)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in e.EnumerateObject()) Collect(p.Value, ids);
                break;
            case JsonValueKind.Array:
                foreach (var x in e.EnumerateArray()) Collect(x, ids);
                break;
            case JsonValueKind.String when Guid.TryParse(e.GetString(), out var g):
                ids.Add(g);
                break;
        }
    }

    private static string[] Ids(int from, int count) => Enumerable.Range(from, count).Select(i => $"t.{i}").ToArray();

    private Task<HttpResponseMessage> SeenAsync(Person who, params string[] ids) =>
        api.Client(who.Token).PostAsJsonAsync("/api/me/guidance/seen", new { ids });

    private async Task<List<string>> SeenListAsync(Person who) =>
        (await GuidanceAsync(who)).GetProperty("seen").EnumerateArray().Select(x => x.GetString()!).ToList();

    private async Task<JsonElement> GuidanceAsync(Person who) =>
        await DataAsync(await api.Client(who.Token).GetAsync("/api/me/guidance"));

    private async Task<Dictionary<string, bool>> ProgressAsync(Person who) =>
        (await GuidanceAsync(who)).GetProperty("progress").EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetBoolean());

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("data");
    }

    private async Task<Person> UserAsync(string name, string? mail = null, Guid? id = null)
    {
        var userId = id ?? Guid.NewGuid();
        mail ??= $"m-{userId:N}@example.com";
        await api.WriteAsync(async db =>
        {
            var roleId = await db.Roles.Where(r => r.Name == Roles.Customer).Select(r => r.Id).SingleAsync();
            var user = new AppUser { Id = userId, Email = mail, DisplayName = name, IsActive = true, CreatedAt = DateTimeOffset.UtcNow };
            user.UserRoles.Add(new UserRole { RoleId = roleId });
            db.Users.Add(user);
        });
        return new Person(userId, mail, name, api.TokenFor(userId, "email", mail, Roles.Customer));
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

    private async Task<Guid> PhotoAsync(Guid ev)
    {
        var id = Guid.NewGuid();
        await api.WriteAsync(async db =>
        {
            db.EventPhotos.Add(new EventPhoto
            {
                Id = id, CampaignId = ev, OriginalUrl = "http://localhost/assets/o.jpg", Url = "http://localhost/assets/v.jpg",
                ThumbUrl = "http://localhost/assets/t.jpg", ContentType = "image/jpeg", SizeBytes = 10, Width = 1, Height = 1,
                CreatedAt = DateTimeOffset.UtcNow,
            });
            await Task.CompletedTask;
        });
        return id;
    }

    private async Task CelebrantAsync(Guid ev, string email, bool canManage = false, bool acknowledged = false) =>
        await api.WriteAsync(async db =>
        {
            db.CampaignCelebrants.Add(new CampaignCelebrant
            {
                Id = Guid.NewGuid(), CampaignId = ev, Name = "Celebrant", Email = email.ToLowerInvariant(), CanManage = canManage,
                AcknowledgedAt = acknowledged ? DateTimeOffset.UtcNow.AddDays(-1) : null, CreatedAt = DateTimeOffset.UtcNow,
            });
            await Task.CompletedTask;
        });
}
