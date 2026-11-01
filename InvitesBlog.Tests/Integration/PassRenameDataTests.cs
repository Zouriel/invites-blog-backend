using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InvitesBlog.Application.Plans;
using InvitesBlog.Application.Services.Admin;
using InvitesBlog.Application.Dtos.Admin;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace InvitesBlog.Tests.Integration;

/// <summary>
/// Rows as production holds them from before Party and Wedding became Gold and Platinum, written with
/// plain SQL so nothing in the new code can flatter them, and read through the real API on real Postgres.
/// </summary>
[Collection("account-deletion")] // The one fixture: it sets process-wide settings, so two can't run side by side.
public sealed class PassRenameDataTests(PostgresApiFixture api)
{
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    private async Task<int> SqlAsync(string sql, params object[] args)
    {
        return await api.ReadAsync(db => db.Database.ExecuteSqlRawAsync(sql, args));
    }

    private async Task<(Guid Id, string Token)> UserAsync()
    {
        var id = Guid.NewGuid();
        var mail = $"u-{id:N}@example.com";
        await api.WriteAsync(async db =>
        {
            var role = await db.Roles.Where(r => r.Name == Roles.Customer).Select(r => r.Id).SingleAsync();
            var user = new AppUser { Id = id, Email = mail, DisplayName = "Host", IsActive = true, CreatedAt = DateTimeOffset.UtcNow };
            user.UserRoles.Add(new UserRole { RoleId = role });
            db.Users.Add(user);
        });
        return (id, api.TokenFor(id, "email", mail));
    }

    private async Task<Guid> EventAsync(Guid host)
    {
        var id = Guid.NewGuid();
        var template = Guid.NewGuid();
        var now = DateTimeOffset.UtcNow;
        await api.WriteAsync(db =>
        {
            db.Templates.Add(new Template
            {
                Id = template, Name = "T", Slug = "t-" + template.ToString("N"), Version = "1.0.0", Category = "Imported", Description = "D", PreviewImageUrl = "",
                SceneJson = "{}", ManifestJson = "{}", PackageUrl = "", Visibility = TemplateVisibility.Imported,
                CreatedAt = now, UpdatedAt = now,
            });
            db.Campaigns.Add(new Campaign
            {
                Id = id, TemplateId = template, TemplateVersion = "1.0.0", TemplatePackageUrl = "", CreatedByUserId = host,
                AccessTokenHash = Guid.NewGuid().ToString("N"), Title = "An event", Slug = $"e-{id:N}", Status = CampaignStatus.Dispatched,
                EventStartAt = now.AddMonths(2), CreatedAt = now, UpdatedAt = now,
            });
            return Task.CompletedTask;
        });
        return id;
    }

    private async Task<int> StoredPassAsync(Guid ev)
    {
        var builder = new NpgsqlConnectionStringBuilder(await api.ReadAsync(db => Task.FromResult(db.Database.GetConnectionString()!)));
        await using var conn = new NpgsqlConnection(builder.ToString());
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("SELECT event_pass FROM campaigns WHERE id = @id", conn);
        cmd.Parameters.AddWithValue("id", ev);
        return (int)(await cmd.ExecuteScalarAsync())!;
    }

    [PostgresFact]
    public async Task An_event_stored_as_1_is_on_Gold_and_as_2_on_Platinum_and_the_api_says_so()
    {
        var host = await UserAsync();
        var gold = await EventAsync(host.Id);
        var platinum = await EventAsync(host.Id);
        var until = DateTimeOffset.UtcNow.AddMonths(6);
        await SqlAsync("UPDATE campaigns SET event_pass = 1, event_pass_until = {0} WHERE id = {1}", until, gold);
        await SqlAsync("UPDATE campaigns SET event_pass = 2, event_pass_until = {0} WHERE id = {1}", until, platinum);

        Assert.Equal(EventPassKind.Gold, await api.ReadAsync(db => db.Campaigns.Where(c => c.Id == gold).Select(c => c.EventPass).SingleAsync()));
        Assert.Equal(EventPassKind.Platinum, await api.ReadAsync(db => db.Campaigns.Where(c => c.Id == platinum).Select(c => c.EventPass).SingleAsync()));

        var client = api.Client(host.Token);
        var a = await (await client.GetAsync($"/api/billing/events/{gold}")).Content.ReadFromJsonAsync<JsonElement>(Web);
        var b = await (await client.GetAsync($"/api/billing/events/{platinum}")).Content.ReadFromJsonAsync<JsonElement>(Web);
        Assert.Equal(("GoldPass", "Gold"), (a.GetProperty("data").GetProperty("plan").GetString(), a.GetProperty("data").GetProperty("pass").GetString()));
        Assert.Equal(("PlatinumPass", "Platinum"), (b.GetProperty("data").GetProperty("plan").GetString(), b.GetProperty("data").GetProperty("pass").GetString()));
    }

    [PostgresFact]
    public async Task Other_stored_kinds_keep_their_numbers_read_and_written()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host.Id);
        var payment = Guid.NewGuid();
        var credit = Guid.NewGuid();
        await api.WriteAsync(db =>
        {
            db.Payments.Add(new Payment { Id = payment, CampaignId = ev, UserId = host.Id, Kind = PaymentKind.Sending, Provider = "Fake", Currency = "MVR", Status = PaymentStatus.Pending, Description = "Wedding pass · An event", CreatedAt = DateTimeOffset.UtcNow });
            db.PassCredits.Add(new PassCredit { Id = credit, OwnerUserId = host.Id, Kind = EventPassKind.None, CreatedAt = DateTimeOffset.UtcNow });
            return Task.CompletedTask;
        });

        foreach (var (number, kind) in new[] { (2, PaymentKind.GoldPass), (3, PaymentKind.PlatinumPass), (8, PaymentKind.StudioGoldCredits), (9, PaymentKind.StudioPlatinumCredits), (10, PaymentKind.GoldExtension), (11, PaymentKind.PlatinumExtension) })
        {
            await SqlAsync("UPDATE payments SET kind = {0} WHERE id = {1}", number, payment);
            Assert.Equal(kind, await api.ReadAsync(db => db.Payments.Where(p => p.Id == payment).Select(p => p.Kind).SingleAsync()));
        }
        await SqlAsync("UPDATE pass_credits SET kind = 1 WHERE id = {0}", credit);
        Assert.Equal(EventPassKind.Gold, await api.ReadAsync(db => db.PassCredits.Where(c => c.Id == credit).Select(c => c.Kind).SingleAsync()));
        await SqlAsync("UPDATE pass_credits SET kind = 2 WHERE id = {0}", credit);
        Assert.Equal(EventPassKind.Platinum, await api.ReadAsync(db => db.PassCredits.Where(c => c.Id == credit).Select(c => c.Kind).SingleAsync()));
        await SqlAsync("UPDATE users SET auto_renew_kind = 2 WHERE id = {0}", host.Id);
        Assert.Equal(PaymentKind.GoldPass, await api.ReadAsync(db => db.Users.Where(u => u.Id == host.Id).Select(u => u.AutoRenewKind).SingleAsync()));

        // What was written on a sale before the rename is history and stays as it was.
        Assert.Equal("Wedding pass · An event", await api.ReadAsync(db => db.Payments.Where(p => p.Id == payment).Select(p => p.Description).SingleAsync()));

        // And a new write stores the same number.
        await api.WriteAsync(db => db.Campaigns.Where(c => c.Id == ev).ExecuteUpdateAsync(s => s.SetProperty(c => c.EventPass, EventPassKind.Platinum)));
        Assert.Equal(2, await StoredPassAsync(ev));
    }

    [PostgresFact]
    public async Task A_prices_row_in_the_old_shape_survives_in_jsonb_and_reaches_the_public_catalogue()
    {
        const string oldRow = """{"PartyPass":249,"WeddingPass":799,"KeepPhotosYearly":160,"PremiumMonthly":210,"VenueDiscountPercent":40,"SendingPerBlock":60,"MvrPerUsd":15.5,"PartyExtension":120,"WeddingExtension":400,"VenueMonthly":2500}""";
        await SqlAsync("INSERT INTO app_settings (key, value_json, updated_at) VALUES ('prices', {0}::jsonb, now()) ON CONFLICT (key) DO UPDATE SET value_json = EXCLUDED.value_json", oldRow);
        PriceBook.ClearCache();
        try
        {
            var body = await (await api.Client().GetAsync("/api/plans")).Content.ReadFromJsonAsync<JsonElement>(Web);
            var plans = body.GetProperty("data").GetProperty("plans").EnumerateArray().ToList();
            var gold = plans.Single(p => p.GetProperty("kind").GetString() == "GoldPass");
            var platinum = plans.Single(p => p.GetProperty("kind").GetString() == "PlatinumPass");
            Assert.Equal((249m, 120m), (gold.GetProperty("price").GetDecimal(), gold.GetProperty("extensionPrice").GetDecimal()));
            Assert.Equal((799m, 400m), (platinum.GetProperty("price").GetDecimal(), platinum.GetProperty("extensionPrice").GetDecimal()));

            // The row itself was left alone by reading it.
            var stored = await api.ReadAsync(db => db.AppSettings.Where(s => s.Key == "prices").Select(s => s.ValueJson).SingleAsync());
            Assert.Contains("PartyPass", stored);
        }
        finally
        {
            await SqlAsync("DELETE FROM app_settings WHERE key = 'prices'");
            PriceBook.ClearCache();
        }
    }

    [PostgresFact]
    public async Task Billing_keys_from_before_the_rename_are_quoted_as_the_new_item()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host.Id);
        var client = api.Client(host.Token);
        foreach (var (old, now, topic) in new[] { ("party-pass", "gold-pass", "gold"), ("wedding-pass", "platinum-pass", "platinum") })
        {
            var res = await client.PostAsJsonAsync("/api/billing/quote", new { item = old, campaignId = ev, quantity = 1 });
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            var data = (await res.Content.ReadFromJsonAsync<JsonElement>(Web)).GetProperty("data");
            Assert.Equal(now, data.GetProperty("item").GetString());
            Assert.Equal(topic, data.GetProperty("inquireTopic").GetString());
        }
    }

    [PostgresFact]
    public async Task An_admin_tab_from_before_the_deploy_can_still_say_Party_or_Wedding()
    {
        var host = await UserAsync();
        var a = await EventAsync(host.Id);
        var b = await EventAsync(host.Id);
        using var scope = api.Services.CreateScope();
        var admin = scope.ServiceProvider.GetRequiredService<IAdminService>();
        await admin.SetEventPassAsync(a, new SetEventPassRequest("Party"));
        await admin.SetEventPassAsync(b, new SetEventPassRequest("wedding"));
        Assert.Equal(1, await StoredPassAsync(a));
        Assert.Equal(2, await StoredPassAsync(b));
    }
}
