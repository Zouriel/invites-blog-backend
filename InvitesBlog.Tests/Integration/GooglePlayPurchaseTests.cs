using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using InvitesBlog.Application.Services.Billing;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace InvitesBlog.Tests.Integration;

/// <summary>
/// Google Play purchases through the real API, the real publisher client over a socket to a stub of
/// Google, and real Postgres. Everything is read back in a context of its own.
/// </summary>
[Collection("account-deletion")]
public sealed class GooglePlayPurchaseTests(PostgresApiFixture api)
{
    private const string Url = "/api/billing/google-play/purchases";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private IConfiguration Config => api.Services.GetRequiredService<IConfiguration>();

    private sealed record Person(Guid Id, string Email, string Token);
    private sealed record Envelope<T>(bool Success, string? Message, T? Data, List<ApiErr>? Errors);
    private sealed record ApiErr(string Message, string? Field, string? Code);
    private sealed record Result(string State, string ProductId, Guid? EventId, bool Test);
    private sealed record Offers(bool Available, List<string> Products, string? AccountId, string? ProfileId);

    private static string id6() => Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();

    private async Task<Person> UserAsync(SubscriptionTier tier = SubscriptionTier.None, DateTimeOffset? endsAt = null, bool openEnded = false, bool autoRenew = false)
    {
        var id = Guid.NewGuid();
        var mail = $"p-{id:N}@example.com";
        await api.WriteAsync(async db =>
        {
            var role = await db.Roles.Where(r => r.Name == Roles.Customer).Select(r => r.Id).FirstAsync();
            var user = new AppUser
            {
                Id = id, Email = mail, DisplayName = "Buyer", PasswordHash = "x", IsActive = true, SubscriptionTier = tier,
                SubscriptionEndsAt = openEnded ? null : endsAt, AutoRenewKind = autoRenew ? PaymentKind.PremiumMonthly : null,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            user.UserRoles.Add(new UserRole { RoleId = role });
            db.Users.Add(user);
        });
        return new Person(id, mail, api.TokenFor(id, "email", mail, Roles.Customer));
    }

    private async Task<Guid> EventAsync(Person host, DateTimeOffset? at = null, CampaignKind kind = CampaignKind.Invitation, Guid? venueId = null)
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
                Id = id, TemplateId = templateId, TemplateVersion = "1.0.0", TemplatePackageUrl = "", CreatedByUserId = host.Id, Kind = kind, VenueId = venueId,
                AccessTokenHash = Guid.NewGuid().ToString("N"), Title = "An event", Slug = $"e-{id:N}", Status = CampaignStatus.Draft,
                EventStartAt = at ?? DateTimeOffset.UtcNow.AddDays(10), CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await Task.CompletedTask;
        });
        return id;
    }

    /// <summary>A one-time purchase as Google would answer for it, made by this account for this event.</summary>
    private string Bought(string product, Guid? account, Guid? eventId, int state = 0, int? type = null, string? order = "next", string? reportedProduct = null)
    {
        var token = $"tok-{Guid.NewGuid():N}-with/odd+chars";
        var orderId = order == "next" ? $"GPA.{Guid.NewGuid():N}"[..28] : order;
        var body = new Dictionary<string, object?>
        {
            ["kind"] = "androidpublisher#productPurchase",
            ["purchaseState"] = state,
            ["consumptionState"] = 0,
            ["acknowledgementState"] = 0,
            ["productId"] = reportedProduct ?? product,
        };
        if (orderId is not null) body["orderId"] = orderId;
        if (type is not null) body["purchaseType"] = type;
        if (account is { } a) body["obfuscatedExternalAccountId"] = a.ToString("N");
        if (eventId is { } e) body["obfuscatedExternalProfileId"] = e.ToString("N");
        api.Google.Products[$"{product}/{Uri.EscapeDataString(token)}"] = body;
        api.Google.Products[$"{product}/{token}"] = body;
        if (orderId is not null)
            api.Google.Orders[orderId] = new { orderId, state = "PROCESSED", total = new { currencyCode = "USD", units = "12", nanos = 990000000 } };
        return token;
    }

    private Dictionary<string, object?> Product(string product, string token) =>
        (Dictionary<string, object?>)api.Google.Products[$"{product}/{token}"];

    private bool Consumed(string product, string token) => Equals(Product(product, token)["consumptionState"], 1);

    private string Subscribed(Guid? account, DateTimeOffset expiry, string state = "SUBSCRIPTION_STATE_ACTIVE", string? order = null, bool test = false, bool autoRenew = true)
    {
        var token = $"sub-{Guid.NewGuid():N}";
        api.Google.Subscriptions[token] = SubBody(account, expiry, state, order ?? $"GPA.{Guid.NewGuid():N}"[..28], test, autoRenew);
        return token;
    }

    private static Dictionary<string, object?> SubBody(Guid? account, DateTimeOffset expiry, string state, string order, bool test, bool autoRenew)
    {
        var body = new Dictionary<string, object?>
        {
            ["subscriptionState"] = state,
            ["acknowledgementState"] = "ACKNOWLEDGEMENT_STATE_PENDING",
            ["lineItems"] = new object[]
            {
                new
                {
                    productId = "premium_monthly",
                    expiryTime = expiry.UtcDateTime.ToString("yyyy-MM-ddTHH:mm:ss.fffZ"),
                    latestSuccessfulOrderId = order,
                    autoRenewingPlan = new { autoRenewEnabled = autoRenew },
                },
            },
        };
        if (account is { } a) body["externalAccountIdentifiers"] = new { obfuscatedExternalAccountId = a.ToString("N") };
        if (test) body["testPurchase"] = new { };
        return body;
    }

    private void UpdateSub(string token, Guid account, DateTimeOffset expiry, string state, string order, bool autoRenew = true)
    {
        var previous = (Dictionary<string, object?>)api.Google.Subscriptions[token];
        var next = SubBody(account, expiry, state, order, false, autoRenew);
        next["acknowledgementState"] = previous["acknowledgementState"];
        api.Google.Subscriptions[token] = next;
    }

    private async Task<(HttpStatusCode Status, Envelope<Result> Body)> RedeemAsync(Person who, string product, string token, Guid? eventId = null)
    {
        var res = await api.Client(who.Token).PostAsJsonAsync(Url, new { productId = product, purchaseToken = token, eventId });
        return (res.StatusCode, (await res.Content.ReadFromJsonAsync<Envelope<Result>>(Web))!);
    }

    private Task<Campaign> CampaignAsync(Guid id) => api.ReadAsync(db => db.Campaigns.AsNoTracking().FirstAsync(c => c.Id == id));
    private Task<AppUser> AccountAsync(Guid id) => api.ReadAsync(db => db.Users.AsNoTracking().FirstAsync(u => u.Id == id));
    private Task<List<Payment>> PaymentsAsync(Guid userId) => api.ReadAsync(db => db.Payments.AsNoTracking().Where(p => p.UserId == userId).OrderBy(p => p.CreatedAt).ToListAsync());
    private Task<GooglePlayPurchase?> RowAsync(string token) => api.ReadAsync(db => db.GooglePlayPurchases.AsNoTracking().FirstOrDefaultAsync(p => p.PurchaseToken == token));

    private async Task SweepAsync(bool full = true)
    {
        using var scope = api.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IGooglePlayPurchases>().SweepAsync(DateTimeOffset.UtcNow, full);
    }

    private async Task<HttpResponseMessage> NotifyAsync(object notification, string? bearer = "good")
    {
        var data = Convert.ToBase64String(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(notification)));
        var client = api.Client();
        if (bearer is not null)
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearer == "good" ? api.Google.PubSubToken() : bearer);
        return await client.PostAsJsonAsync("/api/billing/google-play/notifications", new { message = new { data, messageId = "1" }, subscription = "projects/p/subscriptions/s" });
    }

    private async Task Unconfigured(Func<Task> act)
    {
        var saved = Config["GooglePlay:PackageName"];
        Config["GooglePlay:PackageName"] = "";
        try { await act(); } finally { Config["GooglePlay:PackageName"] = saved; }
    }

    private async Task AllowingTests(Func<Task> act)
    {
        Config["GooglePlay:AllowTestPurchases"] = "true";
        try { await act(); } finally { Config["GooglePlay:AllowTestPurchases"] = "false"; }
    }

    // ----- The main path ------------------------------------------------------------------------

    [PostgresFact]
    public async Task A_verified_gold_pass_is_applied_recorded_and_only_then_consumed()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var token = Bought("gold_pass", host.Id, ev);

        var (status, body) = await RedeemAsync(host, "gold_pass", token, ev);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("applied", body.Data!.State);
        var c = await CampaignAsync(ev);
        Assert.Equal(EventPassKind.Gold, c.EventPass);
        Assert.True(c.EventPassUntil > DateTimeOffset.UtcNow.AddMonths(11));

        var p = Assert.Single(await PaymentsAsync(host.Id));
        Assert.Equal("google_play", p.Provider);
        Assert.Equal(PaymentStatus.Paid, p.Status);
        Assert.NotNull(p.FulfilledAt);
        Assert.Equal(PaymentKind.GoldPass, p.Kind);
        Assert.Equal(ev, p.CampaignId);
        // Google's figures, never ours: this is not MVR.
        Assert.Equal("USD", p.Currency);
        Assert.Equal(12.99m, p.Amount);
        Assert.Equal(Product("gold_pass", token)["orderId"], p.ProviderPaymentId);
        Assert.False(p.IsTest);
        Assert.Null(p.TermsAcceptedAt);

        Assert.True(Consumed("gold_pass", token));
        Assert.NotNull((await RowAsync(token))!.SettledAt);
    }

    [PostgresFact]
    public async Task The_client_uses_Googles_paths_and_signs_in_with_the_service_account()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var token = Bought("platinum_pass", host.Id, ev);
        var escaped = Uri.EscapeDataString(token);
        var refusedBefore = api.Google.AssertionsRefused;

        await RedeemAsync(host, "platinum_pass", token, ev);

        var seen = api.Google.Requests.Where(r => r.Path.Contains(escaped)).ToList();
        Assert.Contains(("GET", $"/androidpublisher/v3/applications/{GooglePlayStub.Package}/purchases/products/platinum_pass/tokens/{escaped}"), seen);
        Assert.Contains(("POST", $"/androidpublisher/v3/applications/{GooglePlayStub.Package}/purchases/products/platinum_pass/tokens/{escaped}:consume"), seen);
        Assert.True(api.Google.TokensIssued >= 1);
        Assert.Equal(refusedBefore, api.Google.AssertionsRefused);
    }

    [PostgresTheory]
    [InlineData("gold_pass_extend")]
    [InlineData("platinum_pass_extend")]
    public async Task An_extension_adds_a_year_to_the_pass_the_event_has(string product)
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var kind = product.StartsWith("platinum") ? EventPassKind.Platinum : EventPassKind.Gold;
        var until = DateTimeOffset.UtcNow.AddMonths(2);
        await api.WriteAsync(async db =>
        {
            var c = await db.Campaigns.FirstAsync(x => x.Id == ev);
            c.EventPass = kind;
            c.EventPassUntil = until;
        });

        var (status, _) = await RedeemAsync(host, product, Bought(product, host.Id, ev), ev);

        Assert.Equal(HttpStatusCode.OK, status);
        var after = await CampaignAsync(ev);
        Assert.True(after.EventPassUntil > until.AddMonths(11), $"{after.EventPassUntil}");
    }

    [PostgresFact]
    public async Task An_extension_for_a_pass_the_event_doesnt_have_is_refused_and_stays_open()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var token = Bought("gold_pass_extend", host.Id, ev);

        var (status, body) = await RedeemAsync(host, "gold_pass_extend", token, ev);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("Extending", body.Message);
        Assert.False(Consumed("gold_pass_extend", token));
        Assert.Null(await RowAsync(token));
    }

    [PostgresFact]
    public async Task Keeping_photos_and_more_emails_do_what_the_same_card_payment_does()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);

        await RedeemAsync(host, "keep_photos_year", Bought("keep_photos_year", host.Id, ev), ev);
        await RedeemAsync(host, "extra_invites_100", Bought("extra_invites_100", host.Id, ev), ev);

        var c = await CampaignAsync(ev);
        Assert.True(c.KeepPhotosUntil > DateTimeOffset.UtcNow.AddMonths(11));
        Assert.Equal(100, c.PaidInviteCapacity);
        Assert.Equal(2, (await PaymentsAsync(host.Id)).Count);
    }

    // ----- Refusals -----------------------------------------------------------------------------

    private static string? Code(Envelope<Result> body) => body.Errors?.FirstOrDefault()?.Code;

    [PostgresFact]
    public async Task Unconfigured_answers_503_and_leaves_the_purchase_open_for_later()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var token = Bought("gold_pass", host.Id, ev);
        await Unconfigured(async () =>
        {
            var (status, body) = await RedeemAsync(host, "gold_pass", token, ev);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
            Assert.Equal("google_play_unavailable", Code(body));
            var offers = await api.Client(host.Token).GetFromJsonAsync<Envelope<Offers>>($"/api/billing/google-play/offers?eventId={ev}", Web);
            Assert.False(offers!.Data!.Available);
            Assert.Empty(offers.Data.Products);
        });
        Assert.False(Consumed("gold_pass", token));
        Assert.Equal(EventPassKind.None, (await CampaignAsync(ev)).EventPass);

        // Once it's set up, the same purchase goes through: nothing was lost.
        var (later, _) = await RedeemAsync(host, "gold_pass", token, ev);
        Assert.Equal(HttpStatusCode.OK, later);
        Assert.Equal(EventPassKind.Gold, (await CampaignAsync(ev)).EventPass);
    }

    [PostgresFact]
    public async Task A_purchase_from_another_package_isnt_known_and_unlocks_nothing()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var token = Bought("gold_pass", host.Id, ev);
        var saved = Config["GooglePlay:PackageName"];
        Config["GooglePlay:PackageName"] = "blog.someone.else";
        try
        {
            var (status, body) = await RedeemAsync(host, "gold_pass", token, ev);
            Assert.Equal(HttpStatusCode.NotFound, status);
            Assert.Equal("google_play_not_found", Code(body));
        }
        finally { Config["GooglePlay:PackageName"] = saved; }
        Assert.Equal(EventPassKind.None, (await CampaignAsync(ev)).EventPass);
    }

    [PostgresFact]
    public async Task Another_accounts_token_cant_be_redeemed()
    {
        var buyer = await UserAsync();
        var thief = await UserAsync();
        var ev = await EventAsync(thief);
        var token = Bought("gold_pass", buyer.Id, ev);

        var (status, body) = await RedeemAsync(thief, "gold_pass", token, ev);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("google_play_wrong_account", Code(body));
        Assert.Equal(EventPassKind.None, (await CampaignAsync(ev)).EventPass);
        Assert.False(Consumed("gold_pass", token));
    }

    [PostgresFact]
    public async Task A_token_with_no_account_on_it_is_refused()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var (status, body) = await RedeemAsync(host, "gold_pass", Bought("gold_pass", null, ev), ev);
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("google_play_wrong_account", Code(body));
    }

    [PostgresFact]
    public async Task A_token_cant_be_moved_to_another_event()
    {
        var host = await UserAsync();
        var bought = await EventAsync(host);
        var other = await EventAsync(host);
        var token = Bought("gold_pass", host.Id, bought);

        var (status, body) = await RedeemAsync(host, "gold_pass", token, other);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("google_play_wrong_event", Code(body));
        Assert.Equal(EventPassKind.None, (await CampaignAsync(other)).EventPass);
        Assert.Equal(EventPassKind.None, (await CampaignAsync(bought)).EventPass);

        // Named for no event (a recovery after a restart), Google's word for the event decides.
        var (recovered, r) = await RedeemAsync(host, "gold_pass", token);
        Assert.Equal(HttpStatusCode.OK, recovered);
        Assert.Equal(bought, r.Data!.EventId);
        Assert.Equal(EventPassKind.Gold, (await CampaignAsync(bought)).EventPass);
        Assert.Equal(EventPassKind.None, (await CampaignAsync(other)).EventPass);
    }

    [PostgresFact]
    public async Task A_purchase_for_an_event_with_no_event_on_it_is_refused()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var (status, body) = await RedeemAsync(host, "gold_pass", Bought("gold_pass", host.Id, null), ev);
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("google_play_wrong_event", Code(body));
    }

    [PostgresFact]
    public async Task An_event_that_isnt_theirs_gets_nothing_even_if_the_token_names_it()
    {
        var host = await UserAsync();
        var stranger = await UserAsync();
        var ev = await EventAsync(host);
        var token = Bought("gold_pass", stranger.Id, ev);

        var (status, body) = await RedeemAsync(stranger, "gold_pass", token, ev);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("google_play_not_host", Code(body));
        Assert.Equal(EventPassKind.None, (await CampaignAsync(ev)).EventPass);
        Assert.False(Consumed("gold_pass", token));
    }

    [PostgresFact]
    public async Task Claiming_a_different_product_than_Google_reports_is_refused()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        // The token was really for a Gold pass; the request says Platinum, and Google says otherwise.
        var token = Bought("platinum_pass", host.Id, ev, reportedProduct: "gold_pass");
        var (status, body) = await RedeemAsync(host, "platinum_pass", token, ev);
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("google_play_wrong_product", Code(body));
        Assert.Equal(EventPassKind.None, (await CampaignAsync(ev)).EventPass);
    }

    [PostgresFact]
    public async Task Something_we_dont_sell_is_refused_without_asking_Google()
    {
        var host = await UserAsync();
        var before = api.Google.Requests.Count;
        var (status, body) = await RedeemAsync(host, "venue_monthly", "whatever");
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("google_play_wrong_product", Code(body));
        Assert.Equal(before, api.Google.Requests.Count);
    }

    [PostgresFact]
    public async Task Signed_out_is_refused()
    {
        var res = await api.Client().PostAsJsonAsync(Url, new { productId = "gold_pass", purchaseToken = "x" });
        Assert.Equal(HttpStatusCode.Forbidden, res.StatusCode);
    }

    [PostgresFact]
    public async Task A_cancelled_event_refuses_the_purchase_and_leaves_it_for_Google_to_refund()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var token = Bought("gold_pass", host.Id, ev);
        await api.WriteAsync(async db => (await db.Campaigns.FirstAsync(c => c.Id == ev)).Status = CampaignStatus.Cancelled);

        var (status, body) = await RedeemAsync(host, "gold_pass", token, ev);

        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Contains("cancelled", body.Message);
        Assert.False(Consumed("gold_pass", token));
        Assert.Empty(await PaymentsAsync(host.Id));
    }

    // ----- Once ---------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Handing_it_in_twice_gives_the_same_answer_and_one_pass()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var token = Bought("gold_pass", host.Id, ev);

        var (first, a) = await RedeemAsync(host, "gold_pass", token, ev);
        var until = (await CampaignAsync(ev)).EventPassUntil;
        var (second, b) = await RedeemAsync(host, "gold_pass", token, ev);

        Assert.Equal(HttpStatusCode.OK, first);
        Assert.Equal(HttpStatusCode.OK, second);
        Assert.Equal(a.Data, b.Data);
        Assert.Equal(until, (await CampaignAsync(ev)).EventPassUntil);
        Assert.Single(await PaymentsAsync(host.Id));
    }

    [PostgresFact]
    public async Task A_replay_still_answers_after_the_pass_rules_would_refuse_a_new_one()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var token = Bought("gold_pass", host.Id, ev);
        await RedeemAsync(host, "gold_pass", token, ev);
        // Moved up since: a new Gold pass would be refused, this old one is simply already done.
        await RedeemAsync(host, "platinum_pass", Bought("platinum_pass", host.Id, ev), ev);

        var (status, body) = await RedeemAsync(host, "gold_pass", token, ev);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("applied", body.Data!.State);
    }

    [PostgresFact]
    public async Task Racing_redeems_really_overlap_and_still_give_one_pass()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var token = Bought("gold_pass", host.Id, ev);
        api.Google.ReadDelayMs = 150;
        try
        {
            var all = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => RedeemAsync(host, "gold_pass", token, ev)));
            Assert.All(all, r => Assert.Equal(HttpStatusCode.OK, r.Status));
            Assert.All(all, r => Assert.Equal("applied", r.Body.Data!.State));
        }
        finally { api.Google.ReadDelayMs = 0; }

        // Every one of them asked Google before any finished, so they all arrived at the same row.
        Assert.True(api.Google.Count(Uri.EscapeDataString(token)) >= 12);
        Assert.Single(await PaymentsAsync(host.Id));
        var c = await CampaignAsync(ev);
        Assert.Equal(EventPassKind.Gold, c.EventPass);
        Assert.True(c.EventPassUntil < DateTimeOffset.UtcNow.AddMonths(15), $"more than one year added: {c.EventPassUntil}");
        Assert.Equal(1, await api.ReadAsync(db => db.GooglePlayPurchases.CountAsync(p => p.PurchaseToken == token)));
    }

    [PostgresFact]
    public async Task The_token_is_unique_in_the_database_itself()
    {
        var token = $"dup-{Guid.NewGuid():N}";
        var row = () => new GooglePlayPurchase { Id = Guid.NewGuid(), PurchaseToken = token, ProductId = "gold_pass", CreatedAt = DateTimeOffset.UtcNow };
        await api.WriteAsync(async db => { db.GooglePlayPurchases.Add(row()); await Task.CompletedTask; });
        await Assert.ThrowsAsync<DbUpdateException>(() => api.WriteAsync(async db => { db.GooglePlayPurchases.Add(row()); await Task.CompletedTask; }));
    }

    // ----- Test purchases -----------------------------------------------------------------------

    [PostgresFact]
    public async Task A_test_purchase_is_refused_unless_allowed()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var token = Bought("gold_pass", host.Id, ev, type: 0);

        var (status, body) = await RedeemAsync(host, "gold_pass", token, ev);

        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("google_play_test_refused", Code(body));
        Assert.Equal(EventPassKind.None, (await CampaignAsync(ev)).EventPass);
        Assert.Empty(await PaymentsAsync(host.Id));
        Assert.False(Consumed("gold_pass", token));
    }

    [PostgresFact]
    public async Task An_allowed_test_purchase_applies_and_is_recorded_as_a_test()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var token = Bought("gold_pass", host.Id, ev, type: 0);
        await AllowingTests(async () =>
        {
            var (status, body) = await RedeemAsync(host, "gold_pass", token, ev);
            Assert.Equal(HttpStatusCode.OK, status);
            Assert.True(body.Data!.Test);
        });
        var p = Assert.Single(await PaymentsAsync(host.Id));
        Assert.True(p.IsTest);
        Assert.Contains("test", p.Description);
        Assert.True((await RowAsync(token))!.IsTest);
    }

    // ----- Pending and cancelled ----------------------------------------------------------------

    [PostgresFact]
    public async Task A_pending_payment_unlocks_nothing_until_Google_says_purchased()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var token = Bought("gold_pass", host.Id, ev, state: 2);

        var (status, body) = await RedeemAsync(host, "gold_pass", token, ev);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("pending", body.Data!.State);
        Assert.Equal(EventPassKind.None, (await CampaignAsync(ev)).EventPass);
        Assert.False(Consumed("gold_pass", token));
        Assert.Empty(await PaymentsAsync(host.Id));

        Product("gold_pass", token)["purchaseState"] = 0;
        var (_, later) = await RedeemAsync(host, "gold_pass", token, ev);
        Assert.Equal("applied", later.Data!.State);
        Assert.Equal(EventPassKind.Gold, (await CampaignAsync(ev)).EventPass);
    }

    [PostgresFact]
    public async Task A_purchase_Google_says_was_cancelled_unlocks_nothing()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var (status, body) = await RedeemAsync(host, "gold_pass", Bought("gold_pass", host.Id, ev, state: 1), ev);
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("google_play_not_purchased", Code(body));
        Assert.Equal(EventPassKind.None, (await CampaignAsync(ev)).EventPass);
    }

    // ----- Google's failures --------------------------------------------------------------------

    [PostgresFact]
    public async Task Google_failing_to_answer_is_not_an_answer()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var token = Bought("gold_pass", host.Id, ev);
        api.Google.FailNext[Uri.EscapeDataString(token)] = 500;

        var (status, body) = await RedeemAsync(host, "gold_pass", token, ev);

        Assert.Equal(HttpStatusCode.ServiceUnavailable, status);
        Assert.Equal("google_play_unreachable", Code(body));
        Assert.Equal(EventPassKind.None, (await CampaignAsync(ev)).EventPass);
        Assert.False(Consumed("gold_pass", token));
    }

    [PostgresFact]
    public async Task If_consuming_fails_the_buyer_still_has_it_and_the_sweep_finishes_the_job()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var token = Bought("gold_pass", host.Id, ev);
        api.Google.FailNext[$"{Uri.EscapeDataString(token)}:consume"] = 503;

        var (status, _) = await RedeemAsync(host, "gold_pass", token, ev);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal(EventPassKind.Gold, (await CampaignAsync(ev)).EventPass);
        Assert.False(Consumed("gold_pass", token));
        Assert.Null((await RowAsync(token))!.SettledAt);

        await SweepAsync(full: false);

        Assert.True(Consumed("gold_pass", token));
        Assert.NotNull((await RowAsync(token))!.SettledAt);
        Assert.Single(await PaymentsAsync(host.Id));
    }

    [PostgresFact]
    public async Task If_the_app_consumed_first_it_still_counts_as_settled()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var token = Bought("gold_pass", host.Id, ev);
        Product("gold_pass", token)["consumptionState"] = 1;
        await RedeemAsync(host, "gold_pass", token, ev);
        Assert.NotNull((await RowAsync(token))!.SettledAt);
    }

    [PostgresFact]
    public async Task An_order_Google_wont_show_is_recorded_as_unknown_not_as_MVR_and_filled_in_later()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var token = Bought("gold_pass", host.Id, ev);
        var order = (string)Product("gold_pass", token)["orderId"]!;
        api.Google.FailNext[$"orders/{order}"] = 403;

        await RedeemAsync(host, "gold_pass", token, ev);

        var p = Assert.Single(await PaymentsAsync(host.Id));
        Assert.Equal("XXX", p.Currency);
        Assert.Equal(0m, p.Amount);
        Assert.Equal(EventPassKind.Gold, (await CampaignAsync(ev)).EventPass);

        await SweepAsync();
        p = Assert.Single(await PaymentsAsync(host.Id));
        Assert.Equal("USD", p.Currency);
        Assert.Equal(12.99m, p.Amount);
    }

    // ----- Offers -------------------------------------------------------------------------------

    private async Task<List<string>> OffersAsync(Person who, Guid? ev = null) =>
        (await api.Client(who.Token).GetFromJsonAsync<Envelope<Offers>>(
            "/api/billing/google-play/offers" + (ev is null ? "" : $"?eventId={ev}"), Web))!.Data!.Products;

    [PostgresFact]
    public async Task What_is_offered_follows_what_the_event_has()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        Assert.Equal(["gold_pass", "platinum_pass", "keep_photos_year", "extra_invites_100"], await OffersAsync(host, ev));

        await RedeemAsync(host, "gold_pass", Bought("gold_pass", host.Id, ev), ev);
        Assert.Equal(["platinum_pass", "gold_pass_extend", "extra_invites_100"], await OffersAsync(host, ev));

        await RedeemAsync(host, "platinum_pass", Bought("platinum_pass", host.Id, ev), ev);
        Assert.Equal(["platinum_pass_extend", "extra_invites_100"], await OffersAsync(host, ev));
    }

    [PostgresFact]
    public async Task The_ids_going_into_a_purchase_hold_nothing_personal()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var offers = (await api.Client(host.Token).GetFromJsonAsync<Envelope<Offers>>($"/api/billing/google-play/offers?eventId={ev}", Web))!.Data!;
        Assert.Equal(host.Id.ToString("N"), offers.AccountId);
        Assert.Equal(ev.ToString("N"), offers.ProfileId);
        Assert.True(offers.AccountId!.Length <= 64);
        Assert.DoesNotContain("@", offers.AccountId);
    }

    [PostgresFact]
    public async Task A_save_the_date_isnt_offered_keeping_photos()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host, kind: CampaignKind.SaveTheDate);
        Assert.DoesNotContain("keep_photos_year", await OffersAsync(host, ev));
    }

    [PostgresFact]
    public async Task An_event_at_a_venue_isnt_offered_passes_at_full_price()
    {
        var owner = await UserAsync(SubscriptionTier.Venue, DateTimeOffset.UtcNow.AddDays(20));
        var venueId = Guid.NewGuid();
        await api.WriteAsync(async db =>
        {
            db.Venues.Add(new Venue { Id = venueId, OwnerUserId = owner.Id, Name = "Crown Hall", Code = id6(), CreatedAt = DateTimeOffset.UtcNow });
            await Task.CompletedTask;
        });
        var ev = await EventAsync(owner, venueId: venueId);
        var offered = await OffersAsync(owner, ev);
        Assert.DoesNotContain("gold_pass", offered);
        Assert.DoesNotContain("platinum_pass", offered);
        Assert.DoesNotContain("gold_pass_extend", offered);
        Assert.DoesNotContain("platinum_pass_extend", offered);
        Assert.Contains("extra_invites_100", offered);
        Assert.Empty(await OffersAsync(owner));   // nor Premium, on the Venue plan
    }

    [PostgresFact]
    public async Task Someone_elses_event_offers_nothing()
    {
        var host = await UserAsync();
        var other = await UserAsync();
        var ev = await EventAsync(host);
        Assert.Empty(await OffersAsync(other, ev));
    }

    [PostgresFact]
    public async Task Premium_is_offered_only_to_an_account_without_a_plan()
    {
        Assert.Equal(["premium_monthly"], await OffersAsync(await UserAsync()));
        Assert.Empty(await OffersAsync(await UserAsync(SubscriptionTier.Premium, DateTimeOffset.UtcNow.AddDays(3))));
        Assert.Equal(["premium_monthly"], await OffersAsync(await UserAsync(SubscriptionTier.Premium, DateTimeOffset.UtcNow.AddDays(-3))));
    }

    // ----- Subscription -------------------------------------------------------------------------

    [PostgresFact]
    public async Task A_subscription_gives_Premium_until_Googles_expiry_and_is_acknowledged_after()
    {
        var user = await UserAsync();
        var expiry = DateTimeOffset.UtcNow.AddDays(30).AddMilliseconds(123);
        var token = Subscribed(user.Id, expiry);

        var (status, body) = await RedeemAsync(user, "premium_monthly", token);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("applied", body.Data!.State);
        var a = await AccountAsync(user.Id);
        Assert.Equal(SubscriptionTier.Premium, a.SubscriptionTier);
        Assert.Equal(new DateTimeOffset(expiry.UtcDateTime.AddTicks(-(expiry.UtcTicks % TimeSpan.TicksPerMillisecond)), TimeSpan.Zero), a.SubscriptionEndsAt);
        Assert.Null(a.AutoRenewKind);
        Assert.Equal("ACKNOWLEDGEMENT_STATE_ACKNOWLEDGED", ((Dictionary<string, object?>)api.Google.Subscriptions[token])["acknowledgementState"]);
        var row = (await RowAsync(token))!;
        Assert.NotNull(row.SettledAt);
        Assert.True(row.AutoRenewing);
        var p = Assert.Single(await PaymentsAsync(user.Id));
        Assert.Equal("google_play", p.Provider);
        Assert.False(p.AutoRenew);
        Assert.Equal(PaymentKind.PremiumMonthly, p.Kind);
    }

    [PostgresFact]
    public async Task A_saved_card_set_to_renew_isnt_charged_as_well()
    {
        var user = await UserAsync(SubscriptionTier.Premium, DateTimeOffset.UtcNow.AddDays(-2), autoRenew: true);
        await RedeemAsync(user, "premium_monthly", Subscribed(user.Id, DateTimeOffset.UtcNow.AddDays(30)));
        Assert.Null((await AccountAsync(user.Id)).AutoRenewKind);
    }

    [PostgresFact]
    public async Task Another_accounts_subscription_cant_be_redeemed()
    {
        var buyer = await UserAsync();
        var thief = await UserAsync();
        var (status, body) = await RedeemAsync(thief, "premium_monthly", Subscribed(buyer.Id, DateTimeOffset.UtcNow.AddDays(30)));
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("google_play_wrong_account", Code(body));
        Assert.Equal(SubscriptionTier.None, (await AccountAsync(thief.Id)).SubscriptionTier);
        Assert.Equal(SubscriptionTier.None, (await AccountAsync(buyer.Id)).SubscriptionTier);
    }

    [PostgresFact]
    public async Task A_pending_subscription_unlocks_nothing_and_a_test_one_is_refused()
    {
        var user = await UserAsync();
        var (_, pending) = await RedeemAsync(user, "premium_monthly", Subscribed(user.Id, DateTimeOffset.UtcNow.AddDays(30), "SUBSCRIPTION_STATE_PENDING"));
        Assert.Equal("pending", pending.Data!.State);
        var (status, test) = await RedeemAsync(user, "premium_monthly", Subscribed(user.Id, DateTimeOffset.UtcNow.AddDays(30), test: true));
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("google_play_test_refused", Code(test));
        Assert.Equal(SubscriptionTier.None, (await AccountAsync(user.Id)).SubscriptionTier);
    }

    [PostgresFact]
    public async Task An_ended_subscription_cant_be_redeemed()
    {
        var user = await UserAsync();
        var (status, body) = await RedeemAsync(user, "premium_monthly", Subscribed(user.Id, DateTimeOffset.UtcNow.AddDays(-1), "SUBSCRIPTION_STATE_EXPIRED"));
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("google_play_not_purchased", Code(body));
        Assert.Equal(SubscriptionTier.None, (await AccountAsync(user.Id)).SubscriptionTier);
    }

    private static object SubscriptionNote(string token, int type) => new
    {
        version = "1.0", packageName = GooglePlayStub.Package, eventTimeMillis = "1",
        subscriptionNotification = new { version = "1.0", notificationType = type, purchaseToken = token },
    };

    [PostgresFact]
    public async Task A_renewal_notification_adds_one_paid_period_however_often_it_arrives()
    {
        var user = await UserAsync();
        var token = Subscribed(user.Id, DateTimeOffset.UtcNow.AddDays(2));
        await RedeemAsync(user, "premium_monthly", token);

        var next = DateTimeOffset.UtcNow.AddDays(32);
        UpdateSub(token, user.Id, next, "SUBSCRIPTION_STATE_ACTIVE", $"GPA.{Guid.NewGuid():N}"[..28]);
        for (var i = 0; i < 3; i++)
            Assert.True((await NotifyAsync(SubscriptionNote(token, 2))).IsSuccessStatusCode);

        var payments = await PaymentsAsync(user.Id);
        Assert.Equal(2, payments.Count);
        Assert.Contains("renewed", payments[1].Description);
        Assert.InRange((await AccountAsync(user.Id)).SubscriptionEndsAt!.Value, next.AddSeconds(-1), next.AddSeconds(1));
    }

    [PostgresFact]
    public async Task Racing_renewal_notifications_add_one_period_and_all_succeed()
    {
        var user = await UserAsync();
        var token = Subscribed(user.Id, DateTimeOffset.UtcNow.AddDays(2));
        await RedeemAsync(user, "premium_monthly", token);

        // The row already exists here, so only its lock keeps these apart.
        var next = DateTimeOffset.UtcNow.AddDays(32);
        UpdateSub(token, user.Id, next, "SUBSCRIPTION_STATE_ACTIVE", $"GPA.{Guid.NewGuid():N}"[..28]);
        api.Google.ReadDelayMs = 100;
        try
        {
            var all = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => NotifyAsync(SubscriptionNote(token, 2))));
            Assert.All(all, r => Assert.True(r.IsSuccessStatusCode, r.StatusCode.ToString()));
        }
        finally { api.Google.ReadDelayMs = 0; }
        Assert.Equal(2, (await PaymentsAsync(user.Id)).Count);
    }

    [PostgresFact]
    public async Task With_no_notification_at_all_the_sweep_notices_a_renewal_and_a_lapse()
    {
        var user = await UserAsync();
        var token = Subscribed(user.Id, DateTimeOffset.UtcNow.AddHours(5));
        await RedeemAsync(user, "premium_monthly", token);
        await api.WriteAsync(async db => (await db.GooglePlayPurchases.FirstAsync(p => p.PurchaseToken == token)).LastCheckedAt = DateTimeOffset.UtcNow.AddHours(-7));

        var next = DateTimeOffset.UtcNow.AddDays(30);
        UpdateSub(token, user.Id, next, "SUBSCRIPTION_STATE_ACTIVE", $"GPA.{Guid.NewGuid():N}"[..28]);
        await SweepAsync();
        Assert.Equal(2, (await PaymentsAsync(user.Id)).Count);
        Assert.InRange((await AccountAsync(user.Id)).SubscriptionEndsAt!.Value, next.AddSeconds(-1), next.AddSeconds(1));

        // Now it ran out, and Google says so.
        var ended = DateTimeOffset.UtcNow.AddHours(-1);
        await api.WriteAsync(async db =>
        {
            var row = await db.GooglePlayPurchases.FirstAsync(p => p.PurchaseToken == token);
            row.ExpiresAt = DateTimeOffset.UtcNow.AddHours(1);
            row.LastCheckedAt = DateTimeOffset.UtcNow.AddHours(-7);
            (await db.Users.FirstAsync(u => u.Id == user.Id)).SubscriptionEndsAt = row.ExpiresAt;
        });
        UpdateSub(token, user.Id, ended, "SUBSCRIPTION_STATE_EXPIRED", (string)(await PaymentsAsync(user.Id))[1].ProviderPaymentId!);
        await SweepAsync();
        Assert.True((await AccountAsync(user.Id)).SubscriptionEndsAt <= DateTimeOffset.UtcNow);
    }

    [PostgresFact]
    public async Task Cancelling_keeps_what_is_paid_and_stops_saying_it_renews()
    {
        var user = await UserAsync();
        var expiry = DateTimeOffset.UtcNow.AddDays(10);
        var token = Subscribed(user.Id, expiry, order: "GPA.cancel-" + Guid.NewGuid().ToString("N")[..12]);
        await RedeemAsync(user, "premium_monthly", token);
        var order = (await RowAsync(token))!.OrderId!;

        UpdateSub(token, user.Id, expiry, "SUBSCRIPTION_STATE_CANCELED", order, autoRenew: false);
        Assert.True((await NotifyAsync(SubscriptionNote(token, 3))).IsSuccessStatusCode);

        var a = await AccountAsync(user.Id);
        Assert.True(a.SubscriptionEndsAt > DateTimeOffset.UtcNow.AddDays(9));
        Assert.False((await RowAsync(token))!.AutoRenewing);
        Assert.Single(await PaymentsAsync(user.Id));
    }

    [PostgresFact]
    public async Task A_revoked_subscription_ends_the_plan_at_once()
    {
        var user = await UserAsync();
        var token = Subscribed(user.Id, DateTimeOffset.UtcNow.AddDays(20));
        await RedeemAsync(user, "premium_monthly", token);
        var order = (await RowAsync(token))!.OrderId!;

        UpdateSub(token, user.Id, DateTimeOffset.UtcNow.AddMinutes(-1), "SUBSCRIPTION_STATE_EXPIRED", order, autoRenew: false);
        Assert.True((await NotifyAsync(SubscriptionNote(token, 12))).IsSuccessStatusCode);

        Assert.True((await AccountAsync(user.Id)).SubscriptionEndsAt <= DateTimeOffset.UtcNow);
    }

    [PostgresFact]
    public async Task A_plan_from_elsewhere_outlives_a_store_subscription_ending()
    {
        var user = await UserAsync(SubscriptionTier.Premium, openEnded: true);
        var token = Subscribed(user.Id, DateTimeOffset.UtcNow.AddDays(20));
        await RedeemAsync(user, "premium_monthly", token);
        Assert.Null((await AccountAsync(user.Id)).SubscriptionEndsAt);

        var order = (await RowAsync(token))!.OrderId!;
        UpdateSub(token, user.Id, DateTimeOffset.UtcNow.AddMinutes(-1), "SUBSCRIPTION_STATE_EXPIRED", order);
        await NotifyAsync(SubscriptionNote(token, 13));

        var a = await AccountAsync(user.Id);
        Assert.Equal(SubscriptionTier.Premium, a.SubscriptionTier);
        Assert.Null(a.SubscriptionEndsAt);
    }

    [PostgresFact]
    public async Task A_venue_account_cant_turn_into_Premium_through_the_store()
    {
        var user = await UserAsync(SubscriptionTier.Venue, DateTimeOffset.UtcNow.AddDays(20));
        var token = Subscribed(user.Id, DateTimeOffset.UtcNow.AddDays(30));
        var (status, _) = await RedeemAsync(user, "premium_monthly", token);
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal(SubscriptionTier.Venue, (await AccountAsync(user.Id)).SubscriptionTier);
        Assert.Equal("ACKNOWLEDGEMENT_STATE_PENDING", ((Dictionary<string, object?>)api.Google.Subscriptions[token])["acknowledgementState"]);
    }

    [PostgresFact]
    public async Task Billing_says_the_plan_is_managed_in_the_store()
    {
        var user = await UserAsync();
        await RedeemAsync(user, "premium_monthly", Subscribed(user.Id, DateTimeOffset.UtcNow.AddDays(30)));
        var body = await api.Client(user.Token).GetStringAsync("/api/billing");
        using var doc = JsonDocument.Parse(body);
        var account = doc.RootElement.GetProperty("data").GetProperty("account");
        Assert.Equal("google_play", account.GetProperty("store").GetString());
        Assert.True(account.GetProperty("storeAutoRenew").GetBoolean());
        Assert.Equal(JsonValueKind.Null, account.GetProperty("autoRenew").ValueKind);
    }

    // ----- Notifications ------------------------------------------------------------------------

    private static object OneTimeNote(string token, string sku, int type = 1) => new
    {
        version = "1.0", packageName = GooglePlayStub.Package, eventTimeMillis = "1",
        oneTimeProductNotification = new { version = "1.0", notificationType = type, purchaseToken = token, sku },
    };

    [PostgresFact]
    public async Task A_push_without_a_real_token_from_our_subscription_does_nothing()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var token = Bought("gold_pass", host.Id, ev);
        var note = OneTimeNote(token, "gold_pass");
        using var stranger = RSA.Create(2048);

        Assert.Equal(HttpStatusCode.Unauthorized, (await NotifyAsync(note, bearer: null)).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await NotifyAsync(note, "not-a-jwt")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await NotifyAsync(note, api.Google.PubSubToken(audience: "https://someone.else"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await NotifyAsync(note, api.Google.PubSubToken(email: "attacker@gmail.com"))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await NotifyAsync(note, api.Google.PubSubToken(verified: false))).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await NotifyAsync(note, api.Google.PubSubToken(signWith: stranger))).StatusCode);
        Assert.Equal(EventPassKind.None, (await CampaignAsync(ev)).EventPass);
    }

    [PostgresFact]
    public async Task A_notification_finishes_a_purchase_when_the_app_never_came_back()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var token = Bought("gold_pass", host.Id, ev);

        var res = await NotifyAsync(OneTimeNote(token, "gold_pass"));

        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        Assert.Equal(EventPassKind.Gold, (await CampaignAsync(ev)).EventPass);
        Assert.True(Consumed("gold_pass", token));

        // The app turning up afterwards finds it done.
        var (status, body) = await RedeemAsync(host, "gold_pass", token, ev);
        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("applied", body.Data!.State);
        Assert.Single(await PaymentsAsync(host.Id));
    }

    [PostgresFact]
    public async Task A_notification_is_only_a_hint_what_Google_answers_decides()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var res = await NotifyAsync(OneTimeNote(Bought("gold_pass", host.Id, ev, state: 2), "gold_pass"));
        Assert.True(res.IsSuccessStatusCode);
        Assert.Equal(EventPassKind.None, (await CampaignAsync(ev)).EventPass);
    }

    [PostgresFact]
    public async Task Google_being_down_during_a_notification_makes_PubSub_try_again()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var token = Bought("gold_pass", host.Id, ev);
        api.Google.FailNext[Uri.EscapeDataString(token)] = 500;
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await NotifyAsync(OneTimeNote(token, "gold_pass"))).StatusCode);
        Assert.True((await NotifyAsync(OneTimeNote(token, "gold_pass"))).IsSuccessStatusCode);
        Assert.Equal(EventPassKind.Gold, (await CampaignAsync(ev)).EventPass);
    }

    [PostgresFact]
    public async Task Rubbish_a_test_ping_and_another_app_are_acknowledged_and_change_nothing()
    {
        Assert.True((await NotifyAsync(new { version = "1.0", packageName = GooglePlayStub.Package, testNotification = new { version = "1.0" } })).IsSuccessStatusCode);
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var other = await NotifyAsync(new { packageName = "blog.other", oneTimeProductNotification = new { purchaseToken = Bought("gold_pass", host.Id, ev), sku = "gold_pass" } });
        Assert.True(other.IsSuccessStatusCode);
        Assert.Equal(EventPassKind.None, (await CampaignAsync(ev)).EventPass);
    }

    [PostgresFact]
    public async Task Notifications_answer_503_while_they_arent_set_up_on_this_server()
    {
        var saved = Config["GooglePlay:Notifications:Audience"];
        Config["GooglePlay:Notifications:Audience"] = "";
        try
        {
            var res = await NotifyAsync(new { testNotification = new { version = "1.0" } });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, res.StatusCode);
        }
        finally { Config["GooglePlay:Notifications:Audience"] = saved; }
    }

    // ----- Voided --------------------------------------------------------------------------------

    private static object VoidedNote(string token, string orderId, int productType = 2) => new
    {
        version = "1.0", packageName = GooglePlayStub.Package, eventTimeMillis = "1",
        voidedPurchaseNotification = new { purchaseToken = token, orderId, productType, refundType = 1 },
    };

    [PostgresFact]
    public async Task A_refund_takes_the_pass_back_once_and_the_token_cant_bring_it_back()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var token = Bought("gold_pass", host.Id, ev);
        await RedeemAsync(host, "gold_pass", token, ev);
        var order = (string)Product("gold_pass", token)["orderId"]!;

        Assert.True((await NotifyAsync(VoidedNote(token, order))).IsSuccessStatusCode);

        var c = await CampaignAsync(ev);
        Assert.Equal(EventPassKind.None, c.EventPass);
        Assert.True(c.EventPassUntil is null || c.EventPassUntil <= DateTimeOffset.UtcNow);
        Assert.Equal(PaymentStatus.Refunded, Assert.Single(await PaymentsAsync(host.Id)).Status);
        Assert.NotNull((await RowAsync(token))!.VoidedAt);

        var (status, body) = await RedeemAsync(host, "gold_pass", token, ev);
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("google_play_voided", Code(body));
        Assert.Equal(EventPassKind.None, (await CampaignAsync(ev)).EventPass);

        // A repeat of the notification changes nothing more.
        Assert.True((await NotifyAsync(VoidedNote(token, order))).IsSuccessStatusCode);
        Assert.Single(await PaymentsAsync(host.Id));
    }

    [PostgresFact]
    public async Task The_voided_list_catches_a_refund_with_no_notification()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var token = Bought("extra_invites_100", host.Id, ev);
        await RedeemAsync(host, "extra_invites_100", token, ev);
        Assert.Equal(100, (await CampaignAsync(ev)).PaidInviteCapacity);

        api.Google.Voided.Add(new { purchaseToken = token, orderId = Product("extra_invites_100", token)["orderId"], voidedTimeMillis = "1" });
        await SweepAsync();
        await SweepAsync();

        Assert.Equal(0, (await CampaignAsync(ev)).PaidInviteCapacity);
        Assert.Equal(PaymentStatus.Refunded, Assert.Single(await PaymentsAsync(host.Id)).Status);
    }

    [PostgresFact]
    public async Task Refunding_a_gold_pass_under_a_platinum_pass_leaves_the_platinum_pass()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var gold = Bought("gold_pass", host.Id, ev);
        await RedeemAsync(host, "gold_pass", gold, ev);
        await RedeemAsync(host, "platinum_pass", Bought("platinum_pass", host.Id, ev), ev);

        await NotifyAsync(VoidedNote(gold, (string)Product("gold_pass", gold)["orderId"]!));

        var c = await CampaignAsync(ev);
        Assert.Equal(EventPassKind.Platinum, c.EventPass);
        Assert.True(c.EventPassUntil > DateTimeOffset.UtcNow);
    }

    [PostgresFact]
    public async Task Refunding_the_platinum_pass_falls_back_to_the_gold_pass_still_paid_for()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        await RedeemAsync(host, "gold_pass", Bought("gold_pass", host.Id, ev), ev);
        var platinum = Bought("platinum_pass", host.Id, ev);
        await RedeemAsync(host, "platinum_pass", platinum, ev);

        await NotifyAsync(VoidedNote(platinum, (string)Product("platinum_pass", platinum)["orderId"]!));

        Assert.Equal(EventPassKind.Gold, (await CampaignAsync(ev)).EventPass);
    }

    [PostgresFact]
    public async Task Refunding_keep_photos_takes_the_year_back()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var token = Bought("keep_photos_year", host.Id, ev);
        await RedeemAsync(host, "keep_photos_year", token, ev);
        await NotifyAsync(VoidedNote(token, (string)Product("keep_photos_year", token)["orderId"]!));
        Assert.Null((await CampaignAsync(ev)).KeepPhotosUntil);
    }

    [PostgresFact]
    public async Task A_refunded_renewal_is_marked_and_Googles_state_decides_the_plan()
    {
        var user = await UserAsync();
        var token = Subscribed(user.Id, DateTimeOffset.UtcNow.AddDays(20));
        await RedeemAsync(user, "premium_monthly", token);
        var order = (await RowAsync(token))!.OrderId!;

        // Refunded, but Google hasn't ended it: the buyer keeps it to the date Google says.
        await NotifyAsync(VoidedNote(token, order, 1));
        Assert.Equal(PaymentStatus.Refunded, Assert.Single(await PaymentsAsync(user.Id)).Status);
        Assert.True((await AccountAsync(user.Id)).SubscriptionEndsAt > DateTimeOffset.UtcNow.AddDays(19));
    }

    // ----- What it touches -----------------------------------------------------------------------

    [PostgresFact]
    public async Task Cancelling_an_event_doesnt_send_a_store_payment_to_the_card_gateway()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        await RedeemAsync(host, "gold_pass", Bought("gold_pass", host.Id, ev), ev);

        var res = await api.Client(host.Token).PostAsync($"/api/campaigns/{ev}/cancel", null);

        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        var p = Assert.Single(await PaymentsAsync(host.Id));
        Assert.Equal(PaymentStatus.Paid, p.Status);
        Assert.Empty(await api.ReadAsync(db => db.Refunds.Where(r => r.PaymentId == p.Id).ToListAsync()));
    }

    [PostgresFact]
    public async Task No_purchase_token_or_key_ever_reaches_the_logs()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var token = Bought("gold_pass", host.Id, ev);
        api.Google.FailNext[$"{Uri.EscapeDataString(token)}:consume"] = 500;
        await RedeemAsync(host, "gold_pass", token, ev);
        var refused = Bought("gold_pass", host.Id, ev, type: 0);
        await RedeemAsync(host, "gold_pass", refused, ev);
        var sub = Subscribed(host.Id, DateTimeOffset.UtcNow.AddDays(3));
        api.Google.FailNext[sub] = 500;
        await RedeemAsync(host, "premium_monthly", sub);
        await NotifyAsync(OneTimeNote(Bought("gold_pass", host.Id, ev), "gold_pass"));
        await SweepAsync();

        // The scan must have had something to read.
        Assert.Contains(api.Logs.Lines, l => l.Contains("Google Play", StringComparison.Ordinal));
        foreach (var line in api.Logs.Lines)
        {
            Assert.DoesNotContain("tok-", line);
            Assert.DoesNotContain(token, line);
            Assert.DoesNotContain(sub, line);
            Assert.DoesNotContain("PRIVATE KEY", line);
            Assert.DoesNotContain("stub-access", line);
        }
    }

    [Fact]
    public void Every_product_is_a_billing_item_and_venue_isnt_sold()
    {
        Assert.Equal(7, GooglePlayCatalog.All.Count);
        foreach (var p in GooglePlayCatalog.All)
            Assert.NotEqual("other", BillingService.ItemName(p.Kind));
        Assert.DoesNotContain(GooglePlayCatalog.All, p => p.Kind is PaymentKind.VenueMonthly or PaymentKind.StudioYearly or PaymentKind.StudioGoldCredits or PaymentKind.StudioPlatinumCredits);
        Assert.Single(GooglePlayCatalog.All, p => p.IsSubscription);
    }

    // ----- Account deletion ----------------------------------------------------------------------

    private async Task DeleteAccountAsync(Person person)
    {
        var client = api.Client(person.Token);
        var sent = await (await client.PostAsync("/api/auth/me/deletion/request", null)).Content.ReadFromJsonAsync<JsonElement>(Web);
        var id = sent.GetProperty("data").GetProperty("challengeId").GetString();
        var done = await client.PostAsJsonAsync("/api/auth/me/deletion/confirm", new { challengeId = id, code = api.Codes.CodeFor(person.Email) });
        Assert.True(done.IsSuccessStatusCode, await done.Content.ReadAsStringAsync());
    }

    private async Task<JsonElement> SummaryAsync(Person person) =>
        (await api.Client(person.Token).GetFromJsonAsync<JsonElement>("/api/auth/me/deletion/summary", Web)).GetProperty("data");

    [PostgresFact]
    public async Task Deleting_the_account_cancels_the_subscription_at_Google_and_says_so()
    {
        var user = await UserAsync();
        var token = Subscribed(user.Id, DateTimeOffset.UtcNow.AddDays(20));
        await RedeemAsync(user, "premium_monthly", token);

        var summary = await SummaryAsync(user);
        Assert.True(summary.GetProperty("autoRenews").GetBoolean());
        Assert.Contains("stops renewing on Google Play", summary.GetProperty("storeNote").GetString());

        await DeleteAccountAsync(user);
        await api.RunRemovalsAsync();

        Assert.Equal("SUBSCRIPTION_STATE_CANCELED", ((Dictionary<string, object?>)api.Google.Subscriptions[token])["subscriptionState"]);
        var row = (await RowAsync(token))!;
        Assert.Null(row.UserId);
        Assert.False(row.AutoRenewing);
        Assert.Null(Assert.Single(await api.ReadAsync(db => db.Payments.AsNoTracking().Where(p => p.ProviderPaymentId == row.OrderId).ToListAsync())).UserId);
    }

    [PostgresFact]
    public async Task If_Google_is_down_the_cancellation_waits_on_the_list_and_retries()
    {
        var user = await UserAsync();
        var token = Subscribed(user.Id, DateTimeOffset.UtcNow.AddDays(20));
        await RedeemAsync(user, "premium_monthly", token);
        api.Google.FailNext[$"{token}:cancel"] = 503;

        await DeleteAccountAsync(user);
        await api.RunRemovalsAsync();

        Assert.Equal("SUBSCRIPTION_STATE_ACTIVE", ((Dictionary<string, object?>)api.Google.Subscriptions[token])["subscriptionState"]);
        var waiting = await api.ReadAsync(db => db.PendingRemovals.FirstAsync(r => r.Target == token));
        Assert.Equal(PendingRemovalKinds.PlaySubscription, waiting.Kind);
        Assert.Equal(1, waiting.Attempts);

        await api.WriteAsync(async db => (await db.PendingRemovals.FirstAsync(r => r.Target == token)).NextAttemptAt = DateTimeOffset.UtcNow.AddMinutes(-1));
        await api.RunRemovalsAsync();
        Assert.Equal("SUBSCRIPTION_STATE_CANCELED", ((Dictionary<string, object?>)api.Google.Subscriptions[token])["subscriptionState"]);
    }

    [PostgresFact]
    public async Task With_no_way_to_cancel_the_summary_tells_them_to_do_it_themselves()
    {
        var user = await UserAsync();
        await RedeemAsync(user, "premium_monthly", Subscribed(user.Id, DateTimeOffset.UtcNow.AddDays(20)));
        await Unconfigured(async () =>
        {
            var note = (await SummaryAsync(user)).GetProperty("storeNote").GetString();
            Assert.Contains("Cancel it there", note);
        });
    }

    [PostgresFact]
    public async Task A_subscription_already_cancelled_isnt_cancelled_again_or_mentioned()
    {
        var user = await UserAsync();
        var expiry = DateTimeOffset.UtcNow.AddDays(10);
        var token = Subscribed(user.Id, expiry);
        await RedeemAsync(user, "premium_monthly", token);
        UpdateSub(token, user.Id, expiry, "SUBSCRIPTION_STATE_CANCELED", (await RowAsync(token))!.OrderId!, autoRenew: false);
        await NotifyAsync(SubscriptionNote(token, 3));

        Assert.Equal(JsonValueKind.Null, (await SummaryAsync(user)).GetProperty("storeNote").ValueKind);
        await DeleteAccountAsync(user);
        await api.RunRemovalsAsync();
        Assert.Equal(0, api.Google.Count($"{token}:cancel"));
    }

    [PostgresFact]
    public async Task After_deletion_a_token_stays_spent_and_late_notifications_change_nothing()
    {
        var user = await UserAsync();
        var ev = await EventAsync(user);
        var pass = Bought("gold_pass", user.Id, ev);
        await RedeemAsync(user, "gold_pass", pass, ev);
        var sub = Subscribed(user.Id, DateTimeOffset.UtcNow.AddDays(20));
        await RedeemAsync(user, "premium_monthly", sub);

        await DeleteAccountAsync(user);
        await api.RunRemovalsAsync();

        Assert.Null((await RowAsync(pass))!.UserId);
        Assert.Null((await RowAsync(pass))!.CampaignId);
        Assert.True((await NotifyAsync(SubscriptionNote(sub, 2))).IsSuccessStatusCode);
        Assert.True((await NotifyAsync(OneTimeNote(pass, "gold_pass"))).IsSuccessStatusCode);
        Assert.False(await api.ReadAsync(db => db.Users.AnyAsync(u => u.Id == user.Id)));
        Assert.Equal(0, await api.ReadAsync(db => db.Payments.CountAsync(p => p.UserId == user.Id)));
    }
}
