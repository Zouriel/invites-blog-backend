using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using InvitesBlog.Application.Moderation;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace InvitesBlog.Tests.Integration;

/// <summary>
/// Owner, 2026-11-12: every account can change its own name. PUT /api/auth/me/name through the real API
/// on real Postgres, its refusals, and the new name showing where other people see it.
/// </summary>
[Collection("venue-plan")]
public sealed class AccountNameTests(PostgresApiFixture api)
{
    private const string Url = "/api/auth/me/name";

    [PostgresFact]
    public async Task A_name_is_cleaned_saved_and_answered_with_the_account()
    {
        foreach (var tier in new[] { SubscriptionTier.None, SubscriptionTier.Premium, SubscriptionTier.Venue })
        {
            var (id, token) = await UserAsync(tier);
            var res = await api.Client(token).PutAsJsonAsync(Url, new { displayName = "  Aisha \u0007 \t  Ibrahim\u200B  " });
            Assert.Equal(HttpStatusCode.OK, res.StatusCode);
            using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
            var account = doc.RootElement.GetProperty("data");
            Assert.Equal("Aisha Ibrahim", account.GetProperty("displayName").GetString());
            Assert.Equal(id, account.GetProperty("id").GetGuid());
            Assert.Equal("Aisha Ibrahim", await api.ReadAsync(db => db.Users.Where(u => u.Id == id).Select(u => u.DisplayName).SingleAsync()));

            // GET me says the same.
            using var me = JsonDocument.Parse(await api.Client(token).GetStringAsync("/api/auth/me"));
            Assert.Equal("Aisha Ibrahim", me.RootElement.GetProperty("data").GetProperty("displayName").GetString());
        }
    }

    [PostgresFact]
    public async Task An_empty_name_is_refused_with_name_required()
    {
        var (id, token) = await UserAsync();
        foreach (var empty in new[] { "", "   ", "\u0007\u200B", null })
            await RefusedAsync(token, empty, "name_required");
        Assert.Equal("Person", await api.ReadAsync(db => db.Users.Where(u => u.Id == id).Select(u => u.DisplayName).SingleAsync()));
    }

    [PostgresFact]
    public async Task A_name_over_80_characters_is_refused_with_name_too_long()
    {
        var (_, token) = await UserAsync();
        await RefusedAsync(token, new string('a', 81), "name_too_long");
        var ok = await api.Client(token).PutAsJsonAsync(Url, new { displayName = new string('a', 80) });
        Assert.Equal(HttpStatusCode.OK, ok.StatusCode);
    }

    [PostgresFact]
    public async Task A_name_with_a_filtered_word_is_refused()
    {
        var (id, token) = await UserAsync();
        await RefusedAsync(token, $"Ali {ObjectionableTerms.Words[0]}", ObjectionableText.Code);
        Assert.Equal("Person", await api.ReadAsync(db => db.Users.Where(u => u.Id == id).Select(u => u.DisplayName).SingleAsync()));
    }

    [PostgresFact]
    public async Task Without_signing_in_it_is_401()
    {
        var res = await api.Client().PutAsJsonAsync(Url, new { displayName = "Somebody" });
        Assert.Equal(HttpStatusCode.Unauthorized, res.StatusCode);
    }

    [PostgresFact]
    public async Task The_new_name_shows_on_the_person_s_comments()
    {
        var (id, token) = await UserAsync();
        var ev = await EventAsync(id);
        var posted = await api.Client(token).PostAsJsonAsync($"/api/me/feed/{ev}/comments", new { body = "What a night" });
        Assert.True(posted.IsSuccessStatusCode, await posted.Content.ReadAsStringAsync());

        Assert.Equal(HttpStatusCode.OK, (await api.Client(token).PutAsJsonAsync(Url, new { displayName = "Mariyam Shafeeg" })).StatusCode);

        using var doc = JsonDocument.Parse(await api.Client(token).GetStringAsync($"/api/me/feed/{ev}/comments"));
        var comment = Assert.Single(doc.RootElement.GetProperty("data").EnumerateArray());
        Assert.Equal("Mariyam Shafeeg", comment.GetProperty("authorName").GetString());
    }

    private async Task RefusedAsync(string token, string? name, string code)
    {
        var res = await api.Client(token).PutAsJsonAsync(Url, new { displayName = name });
        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var error = doc.RootElement.GetProperty("errors")[0];
        Assert.Equal(code, error.GetProperty("code").GetString());
        Assert.Equal("displayName", error.GetProperty("field").GetString());
    }

    private async Task<(Guid Id, string Token)> UserAsync(SubscriptionTier tier = SubscriptionTier.None)
    {
        var id = Guid.NewGuid();
        var mail = $"name-{id:N}@example.com";
        await api.WriteAsync(async db =>
        {
            var roles = await db.Roles.Where(r => r.Name == Roles.Customer).Select(r => r.Id).ToListAsync();
            var user = new AppUser
            {
                Id = id, Email = mail, DisplayName = "Person", PasswordHash = "x", IsActive = true, SubscriptionTier = tier,
                SubscriptionEndsAt = tier == SubscriptionTier.None ? null : DateTimeOffset.UtcNow.AddDays(20), CreatedAt = DateTimeOffset.UtcNow,
            };
            foreach (var r in roles) user.UserRoles.Add(new UserRole { RoleId = r });
            db.Users.Add(user);
        });
        return (id, api.TokenFor(id, "email", mail, [Roles.Customer]));
    }

    private async Task<Guid> EventAsync(Guid host)
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
                CreatedByUserId = host, AccessTokenHash = Guid.NewGuid().ToString("N"),
                Title = "Name test", Slug = $"e-{id:N}", Status = CampaignStatus.Dispatched,
                EventStartAt = DateTimeOffset.UtcNow.AddDays(-1), CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await Task.CompletedTask;
        });
        return id;
    }
}
