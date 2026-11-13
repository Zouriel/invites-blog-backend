using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InvitesBlog.Application.Plans;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InvitesBlog.Tests.Integration;

[CollectionDefinition("photographer-plan")]
public sealed class PhotographerPlanCollection : ICollectionFixture<PostgresApiFixture>;

/// <summary>
/// Photographer (owner, 2026-11-13: Studio without the AI designer, $35 / MVR 540 a month) through the real API on
/// real Postgres: the catalogue, the account, the portfolio from first visit to the public page and its lapse, the
/// events it organises, the website checkout and the admin switch.
/// </summary>
[Collection("photographer-plan")]
public sealed class PhotographerPlanTests(PostgresApiFixture api)
{
    private sealed record Person(Guid Id, string Email, string Token);

    [PostgresFact]
    public async Task The_catalogue_sells_photographer_between_premium_and_studio_with_its_own_perks()
    {
        var catalog = await DataAsync(await api.Client().GetAsync("/api/plans"));
        var plans = catalog.GetProperty("plans").EnumerateArray().ToList();
        Assert.Equal(["Free", "GoldPass", "PlatinumPass", "Premium", "Photographer", "Venue"], plans.Select(p => p.GetProperty("kind").GetString()));

        var photographer = plans.Single(p => p.GetProperty("kind").GetString() == "Photographer");
        Assert.Equal("Photographer", photographer.GetProperty("name").GetString());
        Assert.Equal((await PricesAsync()).PhotographerMonthly, photographer.GetProperty("price").GetDecimal());
        Assert.Equal(10 * PlanCatalog.Gb, photographer.GetProperty("eventBytes").GetInt64());
        var perks = photographer.GetProperty("perks").EnumerateArray().Select(p => p.GetString()!).ToList();
        Assert.Contains(PlanPerks.Portfolio, perks);
        Assert.DoesNotContain(PlanPerks.DesignerAiIncluded, perks);
        Assert.Equal(["Premium"], catalog.GetProperty("addOns")[0].GetProperty("forKinds").EnumerateArray().Select(k => k.GetString()));
    }

    [PostgresFact]
    public async Task A_photographer_account_says_so_can_download_all_and_has_no_ai_designer()
    {
        var who = await UserAsync(SubscriptionTier.Photographer, DateTimeOffset.UtcNow.AddDays(20));

        var me = await DataAsync(await api.Client(who.Token).GetAsync("/api/auth/me"));
        Assert.Equal("Photographer", me.GetProperty("subscriptionTier").GetString());
        Assert.True(me.GetProperty("canDownloadAll").GetBoolean());
        var ai = me.GetProperty("addOns")[0];
        Assert.False(ai.GetProperty("active").GetBoolean());
        Assert.False(ai.GetProperty("included").GetBoolean());

        var billing = await DataAsync(await api.Client(who.Token).GetAsync("/api/billing"));
        Assert.Equal("Photographer", billing.GetProperty("account").GetProperty("tier").GetString());
        Assert.Equal((await PricesAsync()).PhotographerMonthly, billing.GetProperty("prices").GetProperty("photographerMonthly").GetDecimal());
    }

    [PostgresFact]
    public async Task A_photographer_gets_the_portfolio_and_it_goes_away_when_the_plan_ends()
    {
        var who = await UserAsync(SubscriptionTier.Photographer, DateTimeOffset.UtcNow.AddDays(20), "Lens Photographer");
        var c = api.Client(who.Token);

        var draft = await DataAsync(await c.GetAsync("/api/me/portfolio"));
        Assert.Equal("Lens Photographer", draft.GetProperty("displayName").GetString());
        var handle = $"lens{Guid.NewGuid():N}"[..16];
        await DataAsync(await c.PutAsJsonAsync("/api/me/portfolio/handle", new { handle }));
        await DataAsync(await c.PutAsJsonAsync("/api/me/portfolio/publish", new { published = true }));

        Application.Services.Portfolios.PortfolioPublicService.ClearCache();
        Assert.Equal(HttpStatusCode.OK, (await api.Client().GetAsync($"/api/portfolio/@{handle}")).StatusCode);
        var page = await api.Client().GetAsync($"/@{handle}");
        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Lens Photographer", await page.Content.ReadAsStringAsync());

        // Lapsed: the controls refuse and the page is unavailable (kept 90 days, as for Studio).
        await api.WriteAsync(db => db.Users.Where(u => u.Id == who.Id)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.SubscriptionEndsAt, DateTimeOffset.UtcNow.AddMinutes(-1))));
        var refused = await c.GetAsync("/api/me/portfolio");
        Assert.Equal(HttpStatusCode.Forbidden, refused.StatusCode);
        Assert.Contains("studio_required", await refused.Content.ReadAsStringAsync());
        // The public answer is cached for up to a minute; past that, the page and its API are unavailable.
        Application.Services.Portfolios.PortfolioPublicService.ClearCache();
        Assert.Equal(HttpStatusCode.NotFound, (await api.Client().GetAsync($"/api/portfolio/@{handle}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await api.Client().GetAsync($"/@{handle}")).StatusCode);
    }

    [PostgresFact]
    public async Task Events_a_photographer_organises_are_on_photographer_with_its_badge_space_and_no_mark()
    {
        var who = await UserAsync(SubscriptionTier.Photographer, DateTimeOffset.UtcNow.AddDays(20), "Lens Photographer", "https://cdn.example.com/a.webp");
        var ev = await EventAsync(who);

        var plan = await PlanAsync(ev);
        Assert.Equal(PlanKind.Photographer, plan.Kind);
        Assert.Equal(10 * PlanCatalog.Gb, plan.EventBytes);
        Assert.False(plan.Branded);
        Assert.Null(plan.CoveredUntil);
        Assert.True(PlanRules.IncludesFaceGrouping(plan));
        Assert.Equal(who.Id, plan.VenueAccountId);

        var tile = (await DataAsync(await api.Client(who.Token).GetAsync("/api/me/campaigns"))).EnumerateArray()
            .Single(t => t.GetProperty("id").GetGuid() == ev);
        Assert.Equal("Photographer", tile.GetProperty("plan").GetString());
        Assert.Equal("Lens Photographer", tile.GetProperty("venueName").GetString());

        var billing = await DataAsync(await api.Client(who.Token).GetAsync($"/api/billing/events/{ev}"));
        Assert.Equal("Photographer", billing.GetProperty("plan").GetString());
        Assert.True(billing.GetProperty("atVenue").GetBoolean());
    }

    [PostgresFact]
    public async Task The_website_quotes_photographer_at_540_and_refuses_switching_from_another_plan()
    {
        var none = await UserAsync();
        var quote = await DataAsync(await api.Client(none.Token).PostAsJsonAsync("/api/billing/quote", new { item = "photographer-monthly" }));
        Assert.Equal((await PricesAsync()).PhotographerMonthly, quote.GetProperty("amount").GetDecimal());
        Assert.Equal(quote.GetProperty("amount").GetDecimal(), quote.GetProperty("renewsAmount").GetDecimal());

        var premium = await UserAsync(SubscriptionTier.Premium, DateTimeOffset.UtcNow.AddDays(20));
        var r = await api.Client(premium.Token).PostAsJsonAsync("/api/billing/quote", new { item = "photographer-monthly" });
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
        Assert.Contains("on Premium", await r.Content.ReadAsStringAsync());

        var photographer = await UserAsync(SubscriptionTier.Photographer, DateTimeOffset.UtcNow.AddDays(20));
        foreach (var item in new[] { "premium-monthly", "venue-monthly" })
        {
            var refused = await api.Client(photographer.Token).PostAsJsonAsync("/api/billing/quote", new { item });
            Assert.Equal(HttpStatusCode.BadRequest, refused.StatusCode);
            Assert.Contains("on Photographer", await refused.Content.ReadAsStringAsync());
        }
    }

    [PostgresFact]
    public async Task An_admin_can_set_photographer_and_switch_it_to_studio_and_filter_by_it()
    {
        var admin = await UserAsync(roles: [Roles.Admin]);
        var who = await UserAsync();
        var c = api.Client(admin.Token);

        var set = await c.PutAsJsonAsync($"/api/admin/users/{who.Id}/subscription", new { tier = "Photographer", endsAt = DateTimeOffset.UtcNow.AddDays(30) });
        Assert.True(set.IsSuccessStatusCode, await set.Content.ReadAsStringAsync());
        Assert.Equal(SubscriptionTier.Photographer, (await api.ReadAsync(db => db.Users.AsNoTracking().FirstAsync(u => u.Id == who.Id))).SubscriptionTier);

        var listed = await DataAsync(await c.GetAsync("/api/admin/users?plan=photographer&pageSize=100"));
        Assert.Contains(listed.GetProperty("items").EnumerateArray(), u => u.GetProperty("id").GetGuid() == who.Id);

        var studio = await c.PutAsJsonAsync($"/api/admin/users/{who.Id}/subscription", new { tier = "Venue", endsAt = DateTimeOffset.UtcNow.AddDays(30) });
        Assert.True(studio.IsSuccessStatusCode);
        Assert.Equal(SubscriptionTier.Venue, (await api.ReadAsync(db => db.Users.AsNoTracking().FirstAsync(u => u.Id == who.Id))).SubscriptionTier);
    }

    // ----- Helpers ------------------------------------------------------------------------------

    private static async Task<JsonElement> DataAsync(HttpResponseMessage res)
    {
        Assert.True(res.IsSuccessStatusCode, $"{(int)res.StatusCode} {await res.Content.ReadAsStringAsync()}");
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("data").Clone();
    }

    private async Task<Prices> PricesAsync()
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IPriceBook>().CurrentAsync();
    }

    private async Task<EventPlan> PlanAsync(Guid ev)
    {
        using var scope = api.Services.CreateScope();
        return await scope.ServiceProvider.GetRequiredService<IPlanService>().ForCampaignAsync(ev);
    }

    private async Task<Person> UserAsync(SubscriptionTier tier = SubscriptionTier.None, DateTimeOffset? endsAt = null,
        string name = "Person", string? avatar = null, string[]? roles = null)
    {
        var id = Guid.NewGuid();
        var mail = $"photographer-{id:N}@example.com";
        var roleNames = roles ?? [Roles.Customer];
        await api.WriteAsync(async db =>
        {
            var roleIds = await db.Roles.Where(r => roleNames.Contains(r.Name)).Select(r => r.Id).ToListAsync();
            var user = new AppUser
            {
                Id = id, Email = mail, DisplayName = name, AvatarUrl = avatar, PasswordHash = "x", IsActive = true,
                SubscriptionTier = tier, SubscriptionEndsAt = endsAt, CreatedAt = DateTimeOffset.UtcNow,
            };
            foreach (var r in roleIds) user.UserRoles.Add(new UserRole { RoleId = r });
            db.Users.Add(user);
        });
        return new Person(id, mail, api.TokenFor(id, "email", mail, roleNames));
    }

    private async Task<Guid> EventAsync(Person host)
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
                CreatedByUserId = host.Id, AccessTokenHash = Guid.NewGuid().ToString("N"),
                Title = "Photographer test", Slug = $"e-{id:N}", Status = CampaignStatus.Dispatched,
                EventStartAt = DateTimeOffset.UtcNow.AddDays(10), CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await Task.CompletedTask;
        });
        return id;
    }
}
