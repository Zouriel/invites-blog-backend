using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InvitesBlog.Application.Push;
using InvitesBlog.Application.Services.Portfolios;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using InvitesBlog.Infrastructure.Persistence;
using InvitesBlog.Infrastructure.Portfolios;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using Xunit;

namespace InvitesBlog.Tests.Integration;

[CollectionDefinition("portfolio")]
public sealed class PortfolioCollection : ICollectionFixture<PostgresApiFixture>;

/// <summary>
/// The Studio portfolio (docs/PORTFOLIO.md) through the real API on real Postgres: who gets it, handle rules, the
/// per-event consent and "Add to portfolio", the public page, enquiries and "Ask to remove a photo".
/// </summary>
[Collection("portfolio")]
public sealed class PortfolioTests(PostgresApiFixture api)
{
    private sealed record Person(Guid Id, string Email, string Name, string Token);

    [PostgresFact]
    public async Task Only_a_Studio_in_force_gets_the_portfolio_and_its_first_visit_makes_a_draft()
    {
        var premium = await UserAsync("Prem", SubscriptionTier.Premium, DateTimeOffset.UtcNow.AddDays(10));
        var lapsed = await UserAsync("Old Studio", SubscriptionTier.Venue, DateTimeOffset.UtcNow.AddDays(-1));
        foreach (var who in new[] { premium, lapsed })
        {
            var r = await api.Client(who.Token).GetAsync("/api/me/portfolio");
            Assert.Equal(HttpStatusCode.Forbidden, r.StatusCode);
            Assert.Contains("studio_required", await r.Content.ReadAsStringAsync());
        }

        var studio = await StudioAsync("Lens & Light");
        var p = await DataAsync(await api.Client(studio.Token).GetAsync("/api/me/portfolio"));
        Assert.Equal("Lens & Light", p.GetProperty("displayName").GetString());
        Assert.Equal(JsonValueKind.Null, p.GetProperty("handle").ValueKind);
        Assert.Equal(["cover", "contact"], p.GetProperty("sections").EnumerateArray().Select(s => s.GetProperty("kind").GetString()!).ToList());
        Assert.Equal(500, p.GetProperty("photoLimit").GetInt32());
        // A second visit doesn't make another.
        await DataAsync(await api.Client(studio.Token).GetAsync("/api/me/portfolio"));
        Assert.Equal(1, await api.ReadAsync(db => db.Portfolios.CountAsync(x => x.UserId == studio.Id)));
    }

    [PostgresFact]
    public async Task Handles_are_unique_without_case_refuse_reserved_words_and_a_changed_one_redirects_for_30_days()
    {
        var a = await StudioAsync("Alpha Photo");
        var b = await StudioAsync("Beta Photo");
        var stem = "pf" + Guid.NewGuid().ToString("N")[..8];

        var check = await DataAsync(await api.Client(a.Token).GetAsync($"/api/me/portfolio/handle-check?handle=pricing"));
        Assert.Equal("reserved", check.GetProperty("reason").GetString());
        Assert.False(check.GetProperty("available").GetBoolean());
        Assert.False(string.IsNullOrEmpty(check.GetProperty("suggestion").GetString()));
        Assert.Equal("invalid", (await DataAsync(await api.Client(a.Token).GetAsync("/api/me/portfolio/handle-check?handle=a%20b"))).GetProperty("reason").GetString());
        Assert.Equal(HttpStatusCode.BadRequest, (await api.Client(a.Token).PutAsJsonAsync("/api/me/portfolio/handle", new { handle = "Admin" })).StatusCode);

        // Typed in capitals, stored lowercase; the same name in other capitals is taken for b.
        var set = await DataAsync(await api.Client(a.Token).PutAsJsonAsync("/api/me/portfolio/handle", new { handle = stem.ToUpperInvariant() }));
        Assert.Equal(stem, set.GetProperty("handle").GetString());
        Assert.EndsWith($"/@{stem}", set.GetProperty("publicUrl").GetString());
        Assert.Equal("taken", (await DataAsync(await api.Client(b.Token).GetAsync($"/api/me/portfolio/handle-check?handle={stem.ToUpperInvariant()}"))).GetProperty("reason").GetString());
        var clash = await api.Client(b.Token).PutAsJsonAsync("/api/me/portfolio/handle", new { handle = stem });
        Assert.Equal(HttpStatusCode.Conflict, clash.StatusCode);
        Assert.Contains("handle_taken", await clash.Content.ReadAsStringAsync());

        // a moves to a new name: the old one leads there and stays taken for 30 days.
        await DataAsync(await api.Client(a.Token).PutAsJsonAsync("/api/me/portfolio/publish", new { published = true }));
        await DataAsync(await api.Client(a.Token).PutAsJsonAsync("/api/me/portfolio/handle", new { handle = stem + ".new" }));
        PortfolioPublicService.ClearCache();
        var old = await api.BrowserClient().GetAsync($"/@{stem}");
        Assert.Equal(HttpStatusCode.MovedPermanently, old.StatusCode);
        Assert.Equal($"/@{stem}.new", old.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Conflict, (await api.Client(b.Token).PutAsJsonAsync("/api/me/portfolio/handle", new { handle = stem })).StatusCode);

        // After 30 days it is free again.
        await api.WriteAsync(async db => await db.PortfolioHandleRedirects.Where(r => r.Handle == stem)
            .ExecuteUpdateAsync(s => s.SetProperty(r => r.ExpiresAt, DateTimeOffset.UtcNow.AddMinutes(-1))));
        Assert.Equal(stem, (await DataAsync(await api.Client(b.Token).PutAsJsonAsync("/api/me/portfolio/handle", new { handle = stem }))).GetProperty("handle").GetString());
    }

    [PostgresFact]
    public async Task Add_to_portfolio_needs_the_events_consent_takes_only_the_studios_own_photos_and_copies_them()
    {
        var studio = await StudioAsync("Copy Studio");
        var guest = await UserAsync("Guest", SubscriptionTier.None, null);
        var ev = await EventAsync(studio);
        var mine1 = await PhotoAsync(ev, studio.Id);
        var mine2 = await PhotoAsync(ev, studio.Id);
        var theirs = await PhotoAsync(ev, guest.Id);
        var clip = await PhotoAsync(ev, studio.Id, video: true);

        var refused = await api.Client(studio.Token).PostAsJsonAsync("/api/me/portfolio/photos",
            new { photoIds = new[] { mine1 }, newCollectionTitle = "Weddings" });
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("no_consent", await refused.Content.ReadAsStringAsync());

        // Only the organiser can declare consent for an event.
        var other = await StudioAsync("Other Studio");
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client(other.Token).PutAsJsonAsync($"/api/me/campaigns/{ev}/portfolio-consent", new { consented = true })).StatusCode);

        var consent = await DataAsync(await api.Client(studio.Token).PutAsJsonAsync($"/api/me/campaigns/{ev}/portfolio-consent", new { consented = true }));
        Assert.True(consent.GetProperty("consented").GetBoolean());
        Assert.Equal(1, await api.ReadAsync(db => db.PortfolioConsents.CountAsync(c => c.UserId == studio.Id && c.CampaignId == ev)));

        var added = await DataAsync(await api.Client(studio.Token).PostAsJsonAsync("/api/me/portfolio/photos",
            new { photoIds = new[] { mine1, mine2, theirs, clip }, newCollectionTitle = "Weddings" }));
        Assert.Equal(2, added.GetProperty("added").GetInt32());
        Assert.Equal(0, added.GetProperty("skippedGuest").GetInt32());
        Assert.Equal(1, added.GetProperty("skippedOther").GetInt32());
        Assert.Equal(1, added.GetProperty("skippedVideo").GetInt32());
        Assert.Equal("2 photos added. 1 was added by someone else and can't be used. Videos can't be added yet.", added.GetProperty("message").GetString());
        var skipped = added.GetProperty("skipped").EnumerateArray().ToDictionary(x => x.GetProperty("photoId").GetGuid(), x => x.GetProperty("reason").GetString());
        Assert.Equal("other_account", skipped[theirs]);
        Assert.Equal("video", skipped[clip]);
        var collection = added.GetProperty("portfolio").GetProperty("collections")[0];
        Assert.Equal("Weddings", collection.GetProperty("title").GetString());
        var photos = collection.GetProperty("photos").EnumerateArray().ToList();
        Assert.Equal([mine1, mine2], photos.Select(x => x.GetProperty("sourcePhotoId").GetGuid()).ToList());
        // Copies, not the album's files: under the portfolio's own folder, and still there when the album photo goes.
        var url = photos[0].GetProperty("url").GetString()!;
        Assert.Contains("/portfolios/", url);
        Assert.NotNull(await api.Storage.GetAsync(api.Storage.KeyFor(url)!));

        // Adding the same photo again into the same collection: already there.
        var cid = collection.GetProperty("id").GetGuid();
        var again = await DataAsync(await api.Client(studio.Token).PostAsJsonAsync("/api/me/portfolio/photos", new { photoIds = new[] { mine1 }, collectionId = cid }));
        Assert.Equal(0, again.GetProperty("added").GetInt32());
        Assert.Equal(1, again.GetProperty("alreadyIn").GetInt32());

        // Taking consent back removes the event's photos and their files.
        var back = await DataAsync(await api.Client(studio.Token).PutAsJsonAsync($"/api/me/campaigns/{ev}/portfolio-consent", new { consented = false }));
        Assert.False(back.GetProperty("consented").GetBoolean());
        Assert.Equal(0, back.GetProperty("photosInPortfolio").GetInt32());
        Assert.Null(await api.Storage.GetAsync(api.Storage.KeyFor(url)!));
    }

    [PostgresFact]
    public async Task A_portfolio_holds_at_most_500_photos()
    {
        var studio = await StudioAsync("Full Studio");
        var ev = await EventAsync(studio);
        await DataAsync(await api.Client(studio.Token).PutAsJsonAsync($"/api/me/campaigns/{ev}/portfolio-consent", new { consented = true }));
        var p = await DataAsync(await api.Client(studio.Token).PostAsJsonAsync("/api/me/portfolio/collections", new { title = "All" }));
        var cid = p.GetProperty("collections")[0].GetProperty("id").GetGuid();
        var pid = p.GetProperty("id").GetGuid();
        await api.WriteAsync(async db =>
        {
            for (var i = 0; i < 499; i++)
                db.PortfolioPhotos.Add(new PortfolioPhoto
                {
                    Id = Guid.NewGuid(), PortfolioId = pid, CollectionId = cid, Url = "https://x/a.jpg", ThumbUrl = "https://x/a_m.jpg",
                    Width = 1, Height = 1, Position = i, CreatedAt = DateTimeOffset.UtcNow,
                });
            await Task.CompletedTask;
        });
        var two = new[] { await PhotoAsync(ev, studio.Id), await PhotoAsync(ev, studio.Id) };
        var refused = await api.Client(studio.Token).PostAsJsonAsync("/api/me/portfolio/photos", new { photoIds = two, collectionId = cid });
        Assert.Equal(HttpStatusCode.Conflict, refused.StatusCode);
        Assert.Contains("photo_limit", await refused.Content.ReadAsStringAsync());
        var one = await DataAsync(await api.Client(studio.Token).PostAsJsonAsync("/api/me/portfolio/photos", new { photoIds = two[..1], collectionId = cid }));
        Assert.Equal(1, one.GetProperty("added").GetInt32());
        Assert.Equal(500, one.GetProperty("portfolio").GetProperty("photoCount").GetInt32());
    }

    [PostgresFact]
    public async Task Sections_keep_the_cover_first_and_reorder_hide_and_edit()
    {
        var studio = await StudioAsync("Order Studio");
        var c = api.Client(studio.Token);
        await DataAsync(await c.PostAsJsonAsync("/api/me/portfolio/sections", new { kind = "about", body = "We shoot weddings." }));
        var p = await DataAsync(await c.PostAsJsonAsync("/api/me/portfolio/collections", new { title = "Beach", layout = "masonry" }));
        // A new collection lands above contact, so the page still ends with it.
        Assert.Equal(["cover", "about", "collection", "contact"], Kinds(p));
        var dup = await c.PostAsJsonAsync("/api/me/portfolio/sections", new { kind = "about" });
        Assert.Equal(HttpStatusCode.Conflict, dup.StatusCode);

        var ids = p.GetProperty("sections").EnumerateArray().Select(s => s.GetProperty("id").GetGuid()).ToList();
        // The cover is asked to move down: it stays first.
        p = await DataAsync(await c.PutAsJsonAsync("/api/me/portfolio/sections/order", new { ids = new[] { ids[3], ids[0], ids[2], ids[1] } }));
        Assert.Equal(["cover", "contact", "collection", "about"], Kinds(p));
        Assert.Equal(HttpStatusCode.Conflict, (await c.PutAsJsonAsync("/api/me/portfolio/sections/order", new { ids = ids.Take(2) })).StatusCode);

        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync($"/api/me/portfolio/sections/{ids[0]}", new { hidden = true })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await c.DeleteAsync($"/api/me/portfolio/sections/{ids[0]}")).StatusCode);
        p = await DataAsync(await c.PutAsJsonAsync($"/api/me/portfolio/sections/{ids[1]}", new { hidden = true }));
        Assert.True(p.GetProperty("sections").EnumerateArray().Single(s => s.GetProperty("kind").GetString() == "about").GetProperty("hidden").GetBoolean());

        // Deleting a collection's section deletes the collection.
        p = await DataAsync(await c.DeleteAsync($"/api/me/portfolio/sections/{ids[2]}"));
        Assert.Equal(0, p.GetProperty("collections").GetArrayLength());
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PutAsJsonAsync("/api/me/portfolio", new { theme = "neon" })).StatusCode);
    }

    [PostgresFact]
    public async Task The_public_page_shows_only_a_published_live_Studio_with_link_preview_tags()
    {
        var studio = await StudioAsync("Page Studio");
        var handle = "pg" + Guid.NewGuid().ToString("N")[..8];
        var c = api.Client(studio.Token);
        var ev = await EventAsync(studio);
        await DataAsync(await c.PutAsJsonAsync($"/api/me/campaigns/{ev}/portfolio-consent", new { consented = true }));
        await DataAsync(await c.PostAsJsonAsync("/api/me/portfolio/photos", new { photoIds = new[] { await PhotoAsync(ev, studio.Id) }, newCollectionTitle = "Weddings" }));
        var p = await DataAsync(await c.PutAsJsonAsync("/api/me/portfolio/handle", new { handle }));
        await DataAsync(await c.PutAsJsonAsync("/api/me/portfolio",
            new { tagline = "Weddings in Addu <b>", contact = new { whatsApp = "+960 7771234", instagram = "https://instagram.com/page.studio/" } }));
        PortfolioPublicService.ClearCache();

        // Not published yet: unavailable, and the API says so too.
        Assert.Equal(HttpStatusCode.NotFound, (await api.Client().GetAsync($"/@{handle}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Client().GetAsync($"/api/portfolio/@{handle}")).StatusCode);
        // The studio's preview link shows it anyway, not indexed.
        var preview = (await DataAsync(await c.PostAsync("/api/me/portfolio/preview-link", null))).GetProperty("url").GetString()!;
        var previewHtml = await (await api.Client().GetAsync(new Uri(preview).PathAndQuery)).Content.ReadAsStringAsync();
        Assert.Contains("noindex", previewHtml);
        Assert.Contains("Only you can see this", previewHtml);

        await DataAsync(await c.PutAsJsonAsync("/api/me/portfolio/publish", new { published = true }));
        PortfolioPublicService.ClearCache();
        var page = await api.Client().GetAsync($"/@{handle}");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        var html = await page.Content.ReadAsStringAsync();
        var cover = (await DataAsync(await c.GetAsync("/api/me/portfolio"))).GetProperty("coverUrl").GetString()!;
        Assert.Contains($"<meta property=\"og:image\" content=\"{cover}\">", html);
        Assert.Contains("<meta property=\"og:title\" content=\"Page Studio\">", html);
        Assert.Contains("Weddings in Addu &lt;b&gt;", html);
        Assert.DoesNotContain("Weddings in Addu <b>", html);
        Assert.Contains("https://wa.me/9607771234", html);
        Assert.Contains("https://instagram.com/page.studio", html);
        Assert.Contains("Ask to remove a photo", html);
        Assert.Contains("Made with invites.blog", html);
        Assert.Contains("script-src 'nonce-", page.Headers.GetValues("Content-Security-Policy").Single());
        // Capitals lead to the one address.
        Assert.Equal(HttpStatusCode.MovedPermanently, (await api.BrowserClient().GetAsync($"/@{handle.ToUpperInvariant()}")).StatusCode);

        // Studio ends: unavailable at once (and kept, for now).
        await api.WriteAsync(async db => await db.Users.Where(u => u.Id == studio.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.SubscriptionEndsAt, DateTimeOffset.UtcNow.AddMinutes(-1))));
        PortfolioPublicService.ClearCache();
        var gone = await api.Client().GetAsync($"/@{handle}");
        Assert.Equal(HttpStatusCode.NotFound, gone.StatusCode);
        Assert.Contains("This portfolio isn't available", await gone.Content.ReadAsStringAsync());
        Assert.Equal(1, await api.ReadAsync(db => db.Portfolios.CountAsync(x => x.UserId == studio.Id)));
    }

    [PostgresFact]
    public async Task A_lapsed_studios_portfolio_is_deleted_90_days_later_with_its_files()
    {
        var studio = await StudioAsync("Gone Studio");
        var ev = await EventAsync(studio);
        var c = api.Client(studio.Token);
        await DataAsync(await c.PutAsJsonAsync($"/api/me/campaigns/{ev}/portfolio-consent", new { consented = true }));
        var added = await DataAsync(await c.PostAsJsonAsync("/api/me/portfolio/photos", new { photoIds = new[] { await PhotoAsync(ev, studio.Id) }, newCollectionTitle = "X" }));
        var url = added.GetProperty("portfolio").GetProperty("collections")[0].GetProperty("photos")[0].GetProperty("url").GetString()!;
        await api.WriteAsync(async db => await db.Users.Where(u => u.Id == studio.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.SubscriptionEndsAt, DateTimeOffset.UtcNow.AddDays(-1))));

        async Task Sweep(DateTimeOffset now)
        {
            using var scope = api.Services.CreateScope();
            await PortfolioRetentionService.SweepAsync(scope.ServiceProvider.GetRequiredService<AppDbContext>(), api.Storage, now, default);
        }
        var now = DateTimeOffset.UtcNow;
        await Sweep(now);
        await Sweep(now.AddDays(89));
        Assert.Equal(1, await api.ReadAsync(db => db.Portfolios.CountAsync(x => x.UserId == studio.Id)));
        await Sweep(now.AddDays(91));
        Assert.Equal(0, await api.ReadAsync(db => db.Portfolios.CountAsync(x => x.UserId == studio.Id)));
        Assert.Null(await api.Storage.GetAsync(api.Storage.KeyFor(url)!));
    }

    [PostgresFact]
    public async Task Enquiries_reach_the_studio_bots_are_dropped_and_one_visitor_is_limited()
    {
        var studio = await StudioAsync("Ask Studio");
        var handle = await PublishedAsync(studio);
        var visitor = api.Client(null, "10.77.1.1");
        var before = api.Mail.Sent.Count(m => m.To == studio.Email);

        var ok = await DataAsync(await visitor.PostAsJsonAsync($"/api/portfolio/@{handle}/enquiries",
            new { name = "Mariyam", contact = "mariyam@example.com", eventDate = "2027-02-14", message = "Are you free for our wedding?" }));
        Assert.True(ok.GetProperty("ok").GetBoolean());
        var row = await api.ReadAsync(db => db.PortfolioEnquiries.SingleAsync(e => e.Name == "Mariyam" && e.Contact == "mariyam@example.com"));
        Assert.Equal("2027-02-14", row.EventDate);
        var mail = api.Mail.Sent.Last(m => m.To == studio.Email);
        Assert.Equal(before + 1, api.Mail.Sent.Count(m => m.To == studio.Email));
        Assert.Equal("mariyam@example.com", mail.ReplyTo);
        Assert.Contains("Are you free for our wedding?", mail.Html);
        using (var scope = api.Services.CreateScope())
        {
            var notes = await scope.ServiceProvider.GetRequiredService<PushNotificationService>().NotesAsync(new PortfolioEnquiryReceived(row.Id), default);
            var note = Assert.Single(notes);
            Assert.Equal(studio.Id, note.UserId);
            Assert.Equal("New enquiry from Mariyam", note.Title);
        }
        var list = await DataAsync(await api.Client(studio.Token).GetAsync("/api/me/portfolio/enquiries"));
        Assert.Equal(1, list.GetArrayLength());
        Assert.Equal(1, (await DataAsync(await api.Client(studio.Token).GetAsync("/api/me/portfolio"))).GetProperty("unreadEnquiries").GetInt32());

        // A filled honeypot is told it worked; nothing is kept.
        await DataAsync(await visitor.PostAsJsonAsync($"/api/portfolio/@{handle}/enquiries",
            new { name = "Bot", contact = "bot@example.com", message = "Buy now", website = "http://spam.example" }));
        Assert.Equal(0, await api.ReadAsync(db => db.PortfolioEnquiries.CountAsync(e => e.Name == "Bot")));
        // Links are refused.
        Assert.Equal(HttpStatusCode.BadRequest, (await visitor.PostAsJsonAsync($"/api/portfolio/@{handle}/enquiries",
            new { name = "L", contact = "l@example.com", message = "see http://a.example http://b.example http://c.example" })).StatusCode);

        // Five an hour per visitor (two used above, plus the refused one).
        HttpStatusCode last = HttpStatusCode.OK;
        for (var i = 0; i < 4; i++)
            last = (await visitor.PostAsJsonAsync($"/api/portfolio/@{handle}/enquiries",
                new { name = $"N{i}", contact = "n@example.com", message = $"Hello {i}" })).StatusCode;
        Assert.Equal(HttpStatusCode.TooManyRequests, last);
        // Another visitor is not affected.
        Assert.Equal(HttpStatusCode.OK, (await api.Client(null, "10.77.1.2").PostAsJsonAsync($"/api/portfolio/@{handle}/enquiries",
            new { name = "Other", contact = "o@example.com", message = "Hi" })).StatusCode);
    }

    [PostgresFact]
    public async Task Ask_to_remove_a_photo_needs_no_account_reaches_the_queue_and_removal_deletes_the_copy()
    {
        var studio = await StudioAsync("Report Studio");
        var ev = await EventAsync(studio);
        var c = api.Client(studio.Token);
        await DataAsync(await c.PutAsJsonAsync($"/api/me/campaigns/{ev}/portfolio-consent", new { consented = true }));
        var added = await DataAsync(await c.PostAsJsonAsync("/api/me/portfolio/photos", new { photoIds = new[] { await PhotoAsync(ev, studio.Id) }, newCollectionTitle = "X" }));
        var photo = added.GetProperty("portfolio").GetProperty("collections")[0].GetProperty("photos")[0];
        var photoId = photo.GetProperty("id").GetGuid();
        var handle = await PublishedAsync(studio);

        var ok = await DataAsync(await api.Client().PostAsJsonAsync($"/api/portfolio/@{handle}/report",
            new { photoId, reason = "me", note = "That's me on the left", contact = "me@example.com" }));
        Assert.True(ok.GetProperty("ok").GetBoolean());
        var report = await api.ReadAsync(db => db.ContentReports.SingleAsync(r => r.TargetId == photoId));
        Assert.Null(report.ReporterUserId);
        Assert.Equal(PortfolioReportKinds.Photo, report.Kind);
        Assert.Equal(studio.Id, report.SubjectUserId);
        Assert.Contains(api.Mail.Sent, m => m.To == PostgresApiFixture.ReportInbox && m.Subject.Contains("portfolio photo"));
        Assert.Contains(api.Mail.Sent, m => m.To == studio.Email && m.Subject == "Someone asked you to remove a photo");

        var admin = await UserAsync("Admin", SubscriptionTier.None, null, Roles.Admin);
        var queue = await DataAsync(await api.Client(admin.Token).GetAsync("/api/admin/reports"));
        var item = queue.EnumerateArray().Single(r => r.GetProperty("id").GetGuid() == report.Id);
        Assert.Equal("Visitor (no account)", item.GetProperty("reporterName").GetString());
        Assert.Equal(photo.GetProperty("thumbUrl").GetString(), item.GetProperty("currentImageUrl").GetString());

        await DataAsync(await api.Client(admin.Token).PostAsJsonAsync($"/api/admin/reports/{report.Id}/resolve", new { action = "remove" }));
        Assert.Equal(0, await api.ReadAsync(db => db.PortfolioPhotos.CountAsync(x => x.Id == photoId)));
        Assert.Null(await api.Storage.GetAsync(api.Storage.KeyFor(photo.GetProperty("url").GetString()!)!));
    }

    [PostgresFact]
    public async Task Old_host_uploads_count_as_the_studios_own_and_guest_link_uploads_never_do()
    {
        var studio = await StudioAsync("Legacy Studio");
        var ev = await EventAsync(studio);
        var guestRow = await GuestAsync(ev);
        var old = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        // Uploaded by the host through the dashboard before uploaders were recorded: no guest, no account, no name.
        var hostOld = await PhotoAsync(ev, null, createdAt: old, name: null);
        // Through the event's QR code or invitation camera before then: no guest, no account, but a typed name.
        // Whoever typed it, never assumed to be the organiser (2026-11-13 night: Rani's photos showed as the owner's).
        var qrOld = await PhotoAsync(ev, null, createdAt: old, name: "Rani");
        // Through a guest link with no account: the guest's.
        var guestOld = await PhotoAsync(ev, null, guestId: guestRow, createdAt: old);
        // Through a guest link while signed in as the studio: the studio's.
        var studioViaLink = await PhotoAsync(ev, studio.Id, guestId: guestRow);
        // No guest, no account, but recent: not something we can vouch for.
        var anonymousNew = await PhotoAsync(ev, null);
        var c = api.Client(studio.Token);
        await DataAsync(await c.PutAsJsonAsync($"/api/me/campaigns/{ev}/portfolio-consent", new { consented = true }));

        var list = await DataAsync(await c.GetAsync($"/api/me/campaigns/{ev}/portfolio-photos"));
        Assert.True(list.GetProperty("consented").GetBoolean());
        var byId = list.GetProperty("photos").EnumerateArray().ToDictionary(x => x.GetProperty("id").GetGuid());
        Assert.True(byId[hostOld].GetProperty("eligible").GetBoolean());
        Assert.True(byId[studioViaLink].GetProperty("eligible").GetBoolean());
        Assert.False(byId[guestOld].GetProperty("eligible").GetBoolean());
        Assert.Equal("guest", byId[guestOld].GetProperty("reason").GetString());
        Assert.Equal("Added by a guest", byId[guestOld].GetProperty("reasonText").GetString());
        Assert.Equal("guest", byId[anonymousNew].GetProperty("reason").GetString());
        Assert.False(byId[qrOld].GetProperty("eligible").GetBoolean());
        Assert.Equal("guest", byId[qrOld].GetProperty("reason").GetString());
        Assert.Equal(2, list.GetProperty("eligibleCount").GetInt32());
        Assert.Equal(3, list.GetProperty("guestCount").GetInt32());

        // Only guests' photos chosen: refused with the reason, and no empty collection is left behind.
        var none = await c.PostAsJsonAsync("/api/me/portfolio/photos", new { photoIds = new[] { guestOld, anonymousNew }, newCollectionTitle = "Weddings" });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, none.StatusCode);
        var body = await none.Content.ReadAsStringAsync();
        Assert.Contains("nothing_added", body);
        Assert.Contains("None of these could be added: they were added by guests.", body);
        Assert.Equal(0, await api.ReadAsync(db => db.PortfolioCollections.CountAsync(x => db.Portfolios.Any(p => p.Id == x.PortfolioId && p.UserId == studio.Id))));

        var added = await DataAsync(await c.PostAsJsonAsync("/api/me/portfolio/photos",
            new { photoIds = new[] { hostOld, studioViaLink, guestOld }, newCollectionTitle = "Weddings" }));
        Assert.Equal(2, added.GetProperty("added").GetInt32());
        Assert.Equal(1, added.GetProperty("skippedGuest").GetInt32());
        Assert.Equal("2 photos added. 1 was added by a guest and can't be used.", added.GetProperty("message").GetString());
        // The collection page's "Add photos" (an existing collection) with only guests' photos: refused too.
        var cid = added.GetProperty("collectionId").GetGuid();
        var none2 = await c.PostAsJsonAsync("/api/me/portfolio/photos", new { photoIds = new[] { guestOld }, collectionId = cid });
        Assert.Equal(HttpStatusCode.UnprocessableEntity, none2.StatusCode);
        Assert.Contains("nothing_added", await none2.Content.ReadAsStringAsync());
        var after = await DataAsync(await c.GetAsync($"/api/me/campaigns/{ev}/portfolio-photos"));
        Assert.True(after.GetProperty("photos").EnumerateArray().Single(x => x.GetProperty("id").GetGuid() == hostOld).GetProperty("inPortfolio").GetBoolean());

        // An old host upload in someone else's event isn't this studio's.
        var other = await StudioAsync("Other Organiser");
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client(other.Token).GetAsync($"/api/me/campaigns/{ev}/portfolio-photos")).StatusCode);
    }

    [PostgresFact]
    public async Task The_backfill_gives_old_host_uploads_in_Studio_events_their_organiser_and_nothing_else()
    {
        var studio = await StudioAsync("Backfill Studio");
        var premium = await UserAsync("Not Studio", SubscriptionTier.Premium, DateTimeOffset.UtcNow.AddDays(5));
        var ev = await EventAsync(studio);
        var plain = await EventAsync(premium);
        var guestRow = await GuestAsync(ev);
        var old = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        var host = await PhotoAsync(ev, null, createdAt: old, name: null);
        var guest = await PhotoAsync(ev, null, guestId: guestRow, createdAt: old);
        var recent = await PhotoAsync(ev, null);
        var notStudio = await PhotoAsync(plain, null, createdAt: old);

        await api.WriteAsync(async db => await db.Database.ExecuteSqlRawAsync(PortfolioBackfill.HostUploadsSql));

        var rows = await api.ReadAsync(db => db.EventPhotos.Where(p => new[] { host, guest, recent, notStudio }.Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.UploaderUserId));
        Assert.Equal(studio.Id, rows[host]);
        Assert.Null(rows[guest]);
        Assert.Null(rows[recent]);
        Assert.Null(rows[notStudio]);
    }

    [PostgresFact]
    public async Task A_cover_picture_can_be_uploaded_shows_on_the_page_and_link_preview_and_a_chosen_photo_replaces_it()
    {
        var studio = await StudioAsync("Cover Studio");
        var c = api.Client(studio.Token);
        var ev = await EventAsync(studio);
        await DataAsync(await c.PutAsJsonAsync($"/api/me/campaigns/{ev}/portfolio-consent", new { consented = true }));
        var added = await DataAsync(await c.PostAsJsonAsync("/api/me/portfolio/photos", new { photoIds = new[] { await PhotoAsync(ev, studio.Id) }, newCollectionTitle = "W" }));
        Assert.Equal("auto", added.GetProperty("portfolio").GetProperty("coverSource").GetString());

        using var form = new MultipartFormDataContent();
        var file = new ByteArrayContent(Jpeg(1600, 900));
        file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        form.Add(file, "file", "cover.jpg");
        var p = await DataAsync(await c.PostAsync("/api/me/portfolio/cover", form));
        Assert.Equal("upload", p.GetProperty("coverSource").GetString());
        var uploaded = p.GetProperty("coverImageUrl").GetString()!;
        Assert.Contains("/portfolios/", uploaded);
        Assert.Contains("/cover-", uploaded);
        Assert.Equal(uploaded, p.GetProperty("coverUrl").GetString());
        Assert.NotNull(await api.Storage.GetAsync(api.Storage.KeyFor(uploaded)!));

        var handle = await PublishedAsync(studio);
        var html = await (await api.Client().GetAsync($"/@{handle}")).Content.ReadAsStringAsync();
        Assert.Contains(uploaded, html);
        Assert.Matches($"<meta property=\"og:image\" content=\"[^\"]*{System.Text.RegularExpressions.Regex.Escape(uploaded)}\">", html);

        // Not an image: refused.
        using var bad = new MultipartFormDataContent { { new ByteArrayContent("hello"u8.ToArray()), "file", "x.jpg" } };
        Assert.Equal(HttpStatusCode.BadRequest, (await c.PostAsync("/api/me/portfolio/cover", bad)).StatusCode);

        // Choosing one of the portfolio's photos replaces the uploaded picture (and its file goes).
        var photoId = p.GetProperty("collections")[0].GetProperty("photos")[0].GetProperty("id").GetGuid();
        var chosen = await DataAsync(await c.PutAsJsonAsync("/api/me/portfolio", new { coverPhotoId = photoId }));
        Assert.Equal("photo", chosen.GetProperty("coverSource").GetString());
        Assert.Equal(JsonValueKind.Null, chosen.GetProperty("coverImageUrl").ValueKind);
        Assert.Null(await api.Storage.GetAsync(api.Storage.KeyFor(uploaded)!));

        // Upload again, then remove it: back to the chosen/first photo.
        using var form2 = new MultipartFormDataContent();
        var file2 = new ByteArrayContent(Jpeg(1200, 700));
        file2.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
        form2.Add(file2, "file", "cover.jpg");
        var again = await DataAsync(await c.PostAsync("/api/me/portfolio/cover", form2));
        var second = again.GetProperty("coverImageUrl").GetString()!;
        var removed = await DataAsync(await c.DeleteAsync("/api/me/portfolio/cover"));
        Assert.Equal(JsonValueKind.Null, removed.GetProperty("coverImageUrl").ValueKind);
        Assert.Equal("auto", removed.GetProperty("coverSource").GetString());
        Assert.Null(await api.Storage.GetAsync(api.Storage.KeyFor(second)!));
    }

    [PostgresFact]
    public async Task Every_remove_request_is_recorded_page_requests_need_a_description_and_mails_carry_whole_links()
    {
        var studio = await StudioAsync("Many Reports Studio");
        var ev = await EventAsync(studio);
        var c = api.Client(studio.Token);
        await DataAsync(await c.PutAsJsonAsync($"/api/me/campaigns/{ev}/portfolio-consent", new { consented = true }));
        await DataAsync(await c.PostAsJsonAsync("/api/me/portfolio/photos", new { photoIds = new[] { await PhotoAsync(ev, studio.Id) }, newCollectionTitle = "X" }));
        var handle = await PublishedAsync(studio);
        var portfolioId = await api.ReadAsync(db => db.Portfolios.Where(x => x.UserId == studio.Id).Select(x => x.Id).SingleAsync());

        // Neither a photo nor a word about it: nothing to act on.
        var empty = await api.Client().PostAsJsonAsync($"/api/portfolio/@{handle}/report", new { reason = "me" });
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        Assert.Contains("photo_required", await empty.Content.ReadAsStringAsync());

        var studioMailsBefore = api.Mail.Sent.Count(m => m.To == studio.Email);
        await DataAsync(await api.Client().PostAsJsonAsync($"/api/portfolio/@{handle}/report", new { reason = "me", note = "The one by the sea" }));
        await DataAsync(await api.Client().PostAsJsonAsync($"/api/portfolio/@{handle}/report", new { reason = "not_agreed", note = "Our cake photo", contact = "b@example.com" }));
        var rows = await api.ReadAsync(db => db.ContentReports.Where(r => r.TargetId == portfolioId).ToListAsync());
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Note!.Contains("Our cake photo") && r.Note.Contains("b@example.com"));
        // The studio hears once per open request, the moderators every time.
        Assert.Equal(studioMailsBefore + 1, api.Mail.Sent.Count(m => m.To == studio.Email));
        Assert.Equal(2, api.Mail.Sent.Count(m => m.To == PostgresApiFixture.ReportInbox && (m.Html.Contains("Our cake photo") || m.Html.Contains("The one by the sea"))));

        var admin = await UserAsync("Admin2", SubscriptionTier.None, null, Roles.Admin);
        var queue = await DataAsync(await api.Client(admin.Token).GetAsync("/api/admin/reports"));
        Assert.Equal(2, queue.EnumerateArray().Count(r => r.GetProperty("targetId").GetGuid() == portfolioId));
    }

    // ---------- helpers ----------

    private static List<string> Kinds(JsonElement p) =>
        p.GetProperty("sections").EnumerateArray().Select(s => s.GetProperty("kind").GetString()!).ToList();

    [PostgresFact]
    public async Task The_night_fix_gives_named_QR_uploads_back_and_keeps_the_organisers_own_name()
    {
        var studio = await StudioAsync("Mohamed Imdhah");
        var ev = await EventAsync(studio);
        var guestRow = await GuestAsync(ev);
        var old = new DateTimeOffset(2026, 10, 30, 12, 0, 0, TimeSpan.Zero);
        // What the evening backfill left: every old no-guest upload owned by the organiser, named or not.
        var rani = await PhotoAsync(ev, studio.Id, createdAt: old, name: "Rani");
        var baamira = await PhotoAsync(ev, studio.Id, createdAt: old, name: " Baamira ");
        var lastWord = await PhotoAsync(ev, studio.Id, createdAt: old, name: "Imdhah");
        var fullName = await PhotoAsync(ev, studio.Id, createdAt: old, name: "mohamed  imdhah".Replace("  ", " "));
        var firstWord = await PhotoAsync(ev, studio.Id, createdAt: old, name: "Mohamed");
        var unnamed = await PhotoAsync(ev, studio.Id, createdAt: old, name: null);
        var recent = await PhotoAsync(ev, studio.Id, name: "Rani");
        var viaGuestLink = await PhotoAsync(ev, studio.Id, guestId: guestRow, createdAt: old, name: "Rani");
        var claimId = Guid.NewGuid();
        await api.WriteAsync(async db =>
        {
            db.PortfolioPhotoClaims.Add(new PortfolioPhotoClaim
            {
                Id = claimId, UserId = studio.Id, CampaignId = ev, Key = "name:rany", Name = "Rany", PhotoCount = 1, CreatedAt = DateTimeOffset.UtcNow,
            });
            await Task.CompletedTask;
        });
        var claimed = await PhotoAsync(ev, studio.Id, createdAt: old, name: "Rany", claimId: claimId);

        await api.WriteAsync(async db => await db.Database.ExecuteSqlRawAsync(PortfolioBackfill.NamedUploadsFixSql));

        var all = new[] { rani, baamira, lastWord, fullName, firstWord, unnamed, recent, viaGuestLink, claimed };
        var rows = await api.ReadAsync(db => db.EventPhotos.Where(p => all.Contains(p.Id)).ToDictionaryAsync(p => p.Id, p => p.UploaderUserId));
        Assert.Null(rows[rani]);
        Assert.Null(rows[baamira]);
        // A first name is too common to vouch for: unsure stays unowned (the studio can still claim it).
        Assert.Null(rows[firstWord]);
        Assert.Equal(studio.Id, rows[lastWord]);
        Assert.Equal(studio.Id, rows[fullName]);
        Assert.Equal(studio.Id, rows[unnamed]);
        Assert.Equal(studio.Id, rows[recent]);
        Assert.Equal(studio.Id, rows[viaGuestLink]);
        Assert.Equal(studio.Id, rows[claimed]);
    }

    [PostgresFact]
    public async Task These_are_mine_lists_the_names_and_a_claim_makes_them_the_studios_until_undone()
    {
        var studio = await StudioAsync("Claim Studio");
        var someoneSignedIn = await UserAsync("Signed In", SubscriptionTier.None, null);
        var ev = await EventAsync(studio);
        var immy = await GuestAsync(ev);
        var immy1 = await PhotoAsync(ev, null, guestId: immy, name: "Immy");
        var immy2 = await PhotoAsync(ev, null, guestId: immy, name: "Immy");
        var immyClip = await PhotoAsync(ev, null, guestId: immy, name: "Immy", video: true);
        var rany = await PhotoAsync(ev, null, name: "Rany");
        var signedIn = await PhotoAsync(ev, someoneSignedIn.Id, name: "Signed In");
        var c = api.Client(studio.Token);
        var guestKey = $"guest:{immy}";

        // Only the organiser, only a Studio.
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client((await StudioAsync("Not Organiser")).Token).GetAsync($"/api/me/campaigns/{ev}/portfolio-uploaders")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client(someoneSignedIn.Token).GetAsync($"/api/me/campaigns/{ev}/portfolio-uploaders")).StatusCode);

        var list = await DataAsync(await c.GetAsync($"/api/me/campaigns/{ev}/portfolio-uploaders"));
        Assert.False(list.GetProperty("consented").GetBoolean());
        var groups = list.GetProperty("groups").EnumerateArray().ToList();
        // Someone who was signed in is never claimable.
        Assert.Equal([guestKey, "name:rany"], groups.Select(g => g.GetProperty("key").GetString()).ToList());
        Assert.Equal("Immy", groups[0].GetProperty("name").GetString());
        Assert.Equal(2, groups[0].GetProperty("photoCount").GetInt32());
        Assert.Equal(1, groups[0].GetProperty("videoCount").GetInt32());
        Assert.Equal(3, groups[0].GetProperty("thumbUrls").GetArrayLength());
        Assert.False(groups[0].GetProperty("claimed").GetBoolean());

        // Consent first.
        var early = await c.PostAsJsonAsync($"/api/me/campaigns/{ev}/portfolio-uploaders/claim", new { key = guestKey });
        Assert.Equal(HttpStatusCode.Forbidden, early.StatusCode);
        Assert.Contains("no_consent", await early.Content.ReadAsStringAsync());
        await DataAsync(await c.PutAsJsonAsync($"/api/me/campaigns/{ev}/portfolio-consent", new { consented = true }));
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync($"/api/me/campaigns/{ev}/portfolio-uploaders/claim", new { key = "name:nobody" })).StatusCode);

        var claim = await DataAsync(await c.PostAsJsonAsync($"/api/me/campaigns/{ev}/portfolio-uploaders/claim", new { key = guestKey }));
        Assert.Equal(3, claim.GetProperty("count").GetInt32());
        Assert.Equal("3 photos are now yours. You can add them to your portfolio.", claim.GetProperty("message").GetString());
        var after = claim.GetProperty("uploaders").GetProperty("groups").EnumerateArray().ToList();
        Assert.True(after.Single(g => g.GetProperty("key").GetString() == guestKey).GetProperty("claimed").GetBoolean());
        Assert.False(after.Single(g => g.GetProperty("key").GetString() == "name:rany").GetProperty("claimed").GetBoolean());
        var record = await api.ReadAsync(db => db.PortfolioPhotoClaims.SingleAsync(x => x.CampaignId == ev));
        Assert.Equal(studio.Id, record.UserId);
        Assert.Equal("Immy", record.Name);
        Assert.Null(record.UndoneAt);

        // Now they're the studio's: selectable in the picker and addable (the clip still isn't, being a video).
        var picker = await DataAsync(await c.GetAsync($"/api/me/campaigns/{ev}/portfolio-photos"));
        var byId = picker.GetProperty("photos").EnumerateArray().ToDictionary(x => x.GetProperty("id").GetGuid());
        Assert.True(byId[immy1].GetProperty("eligible").GetBoolean());
        Assert.True(byId[immy2].GetProperty("eligible").GetBoolean());
        Assert.Equal("video", byId[immyClip].GetProperty("reason").GetString());
        Assert.Equal("guest", byId[rany].GetProperty("reason").GetString());
        Assert.Equal("other_account", byId[signedIn].GetProperty("reason").GetString());
        var added = await DataAsync(await c.PostAsJsonAsync("/api/me/portfolio/photos", new { photoIds = new[] { immy1 }, newCollectionTitle = "Birthdays" }));
        Assert.Equal(1, added.GetProperty("added").GetInt32());

        // "Not me": back to the guest, and out of the portfolio.
        var undo = await DataAsync(await c.PostAsJsonAsync($"/api/me/campaigns/{ev}/portfolio-uploaders/unclaim", new { key = guestKey }));
        Assert.Equal(3, undo.GetProperty("count").GetInt32());
        Assert.Equal(1, undo.GetProperty("removedFromPortfolio").GetInt32());
        var owners = await api.ReadAsync(db => db.EventPhotos.Where(p => new[] { immy1, immy2, immyClip }.Contains(p.Id)).Select(p => new { p.UploaderUserId, p.ClaimId }).ToListAsync());
        Assert.All(owners, o => { Assert.Null(o.UploaderUserId); Assert.Null(o.ClaimId); });
        Assert.Equal(0, await api.ReadAsync(db => db.PortfolioPhotos.CountAsync(x => x.SourcePhotoId == immy1)));
        Assert.NotNull((await api.ReadAsync(db => db.PortfolioPhotoClaims.SingleAsync(x => x.CampaignId == ev))).UndoneAt);
        Assert.Equal(HttpStatusCode.NotFound, (await c.PostAsJsonAsync($"/api/me/campaigns/{ev}/portfolio-uploaders/unclaim", new { key = guestKey })).StatusCode);
        Assert.False((await DataAsync(await c.GetAsync($"/api/me/campaigns/{ev}/portfolio-uploaders"))).GetProperty("groups")[0].GetProperty("claimed").GetBoolean());
    }


    private async Task<string> PublishedAsync(Person studio)
    {
        var handle = "pp" + Guid.NewGuid().ToString("N")[..8];
        var c = api.Client(studio.Token);
        await DataAsync(await c.PutAsJsonAsync("/api/me/portfolio/handle", new { handle }));
        await DataAsync(await c.PutAsJsonAsync("/api/me/portfolio/publish", new { published = true }));
        PortfolioPublicService.ClearCache();
        return handle;
    }

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{(int)response.StatusCode}: {body}");
        return JsonDocument.Parse(body).RootElement.GetProperty("data");
    }

    private Task<Person> StudioAsync(string name) => UserAsync(name, SubscriptionTier.Venue, DateTimeOffset.UtcNow.AddDays(20));

    private async Task<Person> UserAsync(string name, SubscriptionTier tier, DateTimeOffset? ends, string role = Roles.Customer)
    {
        var id = Guid.NewGuid();
        var mail = $"p-{id:N}@example.com";
        await api.WriteAsync(async db =>
        {
            var roleId = await db.Roles.Where(r => r.Name == role).Select(r => r.Id).SingleAsync();
            var user = new AppUser
            {
                Id = id, Email = mail, DisplayName = name, IsActive = true, CreatedAt = DateTimeOffset.UtcNow,
                SubscriptionTier = tier, SubscriptionEndsAt = ends, Country = "MV",
            };
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
                Id = templateId, Name = "T", Slug = $"own-{id:N}", Version = "1.0.0", Category = "Imported", Description = "D", PreviewImageUrl = "",
                SceneJson = "{}", ManifestJson = "{}", PackageUrl = "", Visibility = TemplateVisibility.Imported,
                CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            db.Campaigns.Add(new Campaign
            {
                Id = id, TemplateId = templateId, TemplateVersion = "1.0.0", TemplatePackageUrl = "",
                CreatedByUserId = creator.Id, AccessTokenHash = Guid.NewGuid().ToString("N"),
                Title = "A wedding", Slug = $"e-{id:N}", Status = CampaignStatus.Dispatched,
                EventStartAt = DateTimeOffset.UtcNow.AddDays(-3), CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await Task.CompletedTask;
        });
        return id;
    }

    private async Task<Guid> GuestAsync(Guid ev)
    {
        var id = Guid.NewGuid();
        await api.WriteAsync(async db =>
        {
            db.Guests.Add(new Guest { Id = id, CampaignId = ev, Name = "A guest", Email = $"g-{id:N}@example.com", CreatedAt = DateTimeOffset.UtcNow });
            await Task.CompletedTask;
        });
        return id;
    }

    [PostgresFact]
    public async Task The_logo_position_and_size_are_named_choices_saved_and_shown_on_the_page()
    {
        var studio = await StudioAsync("Logo Place");
        var c = api.Client(studio.Token);
        var p = await DataAsync(await c.GetAsync("/api/me/portfolio"));
        // The original look until they choose.
        Assert.Equal("bottom-left", p.GetProperty("logoPosition").GetString());
        Assert.Equal("medium", p.GetProperty("logoSize").GetString());

        var options = await DataAsync(await api.Client().GetAsync("/api/portfolio/options"));
        Assert.Equal(["bottom-left", "bottom-center", "center", "top-left", "top-center"],
            options.GetProperty("logoPositions").EnumerateArray().Select(x => x.GetProperty("key").GetString()!).ToList());
        Assert.Equal(["small", "medium", "large"], options.GetProperty("logoSizes").EnumerateArray().Select(x => x.GetProperty("key").GetString()!).ToList());
        Assert.All(options.GetProperty("logoPositions").EnumerateArray(), x => Assert.False(string.IsNullOrEmpty(x.GetProperty("label").GetString())));

        p = await DataAsync(await c.PutAsJsonAsync("/api/me/portfolio", new { logoPosition = "top-center", logoSize = "large" }));
        Assert.Equal("top-center", p.GetProperty("logoPosition").GetString());
        Assert.Equal("large", p.GetProperty("logoSize").GetString());
        // Leaving them out of a later save keeps them.
        p = await DataAsync(await c.PutAsJsonAsync("/api/me/portfolio", new { tagline = "Hi" }));
        Assert.Equal("top-center", p.GetProperty("logoPosition").GetString());

        foreach (var bad in new object[] { new { logoPosition = "sideways" }, new { logoSize = "huge" } })
        {
            var r = await c.PutAsJsonAsync("/api/me/portfolio", bad);
            Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
            Assert.Contains("invalid_choice", await r.Content.ReadAsStringAsync());
        }

        var handle = await PublishedAsync(studio);
        var html = await (await api.Client().GetAsync($"/@{handle}")).Content.ReadAsStringAsync();
        Assert.Contains("pos-top-center logo-large", html);
    }

    [PostgresFact]
    public async Task A_transparent_logo_keeps_its_transparency()
    {
        var studio = await StudioAsync("Clear Logo");
        var c = api.Client(studio.Token);
        foreach (var (type, bytes) in new[] { ("image/png", ClearLogo(png: true)), ("image/webp", ClearLogo(png: false)) })
        {
            using var form = new MultipartFormDataContent();
            var file = new ByteArrayContent(bytes);
            file.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(type);
            form.Add(file, "file", type == "image/png" ? "logo.png" : "logo.webp");
            var p = await DataAsync(await c.PostAsync("/api/me/portfolio/logo", form));
            var url = p.GetProperty("logoUrl").GetString()!;
            Assert.DoesNotContain(".jpg", url);
            var stored = await api.Storage.GetAsync(api.Storage.KeyFor(url)!);
            Assert.NotNull(stored);
            using var image = Image.Load<Rgba32>(stored!);
            // The corner was see-through when uploaded and still is: no white or black box behind the logo.
            Assert.True(image[0, 0].A < 16, $"{type}: corner alpha {image[0, 0].A}");
            Assert.Equal(255, image[image.Width / 2, image.Height / 2].A);
        }
    }

    /// <summary>
    /// A 1000x500 logo: a detailed (noisy) opaque mark, fully transparent around it. Detailed on purpose: a JPEG of it
    /// is smaller than the PNG, so a "turn it into JPEG" path would really be taken and the test would see the box.
    /// </summary>
    private static byte[] ClearLogo(bool png)
    {
        var rnd = new Random(7);
        using var image = new Image<Rgba32>(1000, 500, new Rgba32(0, 0, 0, 0));
        for (var y = 60; y < 440; y++)
            for (var x = 60; x < 940; x++)
                image[x, y] = new Rgba32((byte)rnd.Next(256), (byte)rnd.Next(256), (byte)rnd.Next(256), 255);
        using var ms = new MemoryStream();
        if (png) image.SaveAsPng(ms); else image.SaveAsWebp(ms);
        return ms.ToArray();
    }

    private static byte[] Jpeg(int w = 300, int h = 200)
    {
        using var image = new Image<Rgba32>(w, h, new Rgba32(120, 30, 40));
        using var ms = new MemoryStream();
        image.SaveAsJpeg(ms);
        return ms.ToArray();
    }

    /// <summary>An album photo with real files in storage (so it can be copied), uploaded by <paramref name="uploader"/>.</summary>
    private async Task<Guid> PhotoAsync(Guid ev, Guid? uploader, bool video = false, Guid? guestId = null, DateTimeOffset? createdAt = null,
        string? name = "Someone", Guid? claimId = null)
    {
        var id = Guid.NewGuid();
        var view = await api.Storage.PutAsync($"buckets/test/media/{id:N}.jpg", Jpeg(), "image/jpeg");
        await api.WriteAsync(async db =>
        {
            db.EventPhotos.Add(new EventPhoto
            {
                Id = id, CampaignId = ev, UploaderUserId = uploader, GuestId = guestId, UploaderName = name, ClaimId = claimId,
                OriginalUrl = view, Url = view, ThumbUrl = view, ContentType = video ? "video/mp4" : "image/jpeg",
                SizeBytes = 10, Width = 300, Height = 200, CreatedAt = createdAt ?? DateTimeOffset.UtcNow,
            });
            await Task.CompletedTask;
        });
        return id;
    }
}
