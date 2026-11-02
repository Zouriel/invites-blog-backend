using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using InvitesBlog.Application.Services.Billing;
using InvitesBlog.Domain.Authorization;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using InvitesBlog.Infrastructure.Payments.AppStore;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace InvitesBlog.Tests.Integration;

/// <summary>
/// App Store purchases through the real API, the real Server API client over a socket to a stub of Apple
/// signing with a chain shaped like Apple's, and real Postgres. Read back in a context of its own.
/// </summary>
[Collection("account-deletion")]
public sealed class AppStorePurchaseTests(PostgresApiFixture api)
{
    private const string Url = "/api/billing/app-store/transactions";
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    private IConfiguration Config => api.Services.GetRequiredService<IConfiguration>();
    private static long _next = 2000000000 + Random.Shared.Next(1, 1_000_000) * 1000L;

    private sealed record Person(Guid Id, string Email, string Token);
    private sealed record Envelope<T>(bool Success, string? Message, T? Data, List<ApiErr>? Errors);
    private sealed record ApiErr(string Message, string? Field, string? Code);
    private sealed record Result(string State, string ProductId, Guid? EventId, bool Test);
    private sealed record Intent(Guid AppAccountToken, string ProductId, Guid? EventId);
    private sealed record Offers(bool Available, List<string> Products);

    private static string NextId() => Interlocked.Increment(ref _next).ToString();
    private static long Ms(DateTimeOffset t) => t.ToUnixTimeMilliseconds();

    private async Task<Person> UserAsync(SubscriptionTier tier = SubscriptionTier.None, DateTimeOffset? endsAt = null)
    {
        var id = Guid.NewGuid();
        var mail = $"a-{id:N}@example.com";
        await api.WriteAsync(async db =>
        {
            var role = await db.Roles.Where(r => r.Name == Roles.Customer).Select(r => r.Id).FirstAsync();
            var user = new AppUser
            {
                Id = id, Email = mail, DisplayName = "Buyer", PasswordHash = "x", IsActive = true, SubscriptionTier = tier,
                SubscriptionEndsAt = endsAt, CreatedAt = DateTimeOffset.UtcNow,
            };
            user.UserRoles.Add(new UserRole { RoleId = role });
            db.Users.Add(user);
        });
        return new Person(id, mail, api.TokenFor(id, "email", mail, Roles.Customer));
    }

    private async Task<Guid> EventAsync(Person host, CampaignKind kind = CampaignKind.Invitation, Guid? venueId = null)
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
                EventStartAt = DateTimeOffset.UtcNow.AddDays(10), CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow,
            });
            await Task.CompletedTask;
        });
        return id;
    }

    private async Task<(HttpStatusCode Status, Envelope<Intent> Body)> IntentAsync(Person who, string product, Guid? eventId)
    {
        var res = await api.Client(who.Token).PostAsJsonAsync("/api/billing/app-store/intents", new { productId = product, eventId });
        return (res.StatusCode, (await res.Content.ReadFromJsonAsync<Envelope<Intent>>(Web))!);
    }

    private async Task<Guid> TokenAsync(Person who, string product, Guid? eventId)
    {
        var (status, body) = await IntentAsync(who, product, eventId);
        Assert.True(status == HttpStatusCode.OK, $"intent {status}: {body.Message}");
        return body.Data!.AppAccountToken;
    }

    private static Dictionary<string, object?> Tx(string id, string original, string product, Guid? token, string env, string type,
        long? price = 15990, string currency = "USD", DateTimeOffset? expires = null) => new()
    {
        ["transactionId"] = id,
        ["originalTransactionId"] = original,
        ["bundleId"] = AppStoreStub.Bundle,
        ["productId"] = product,
        ["purchaseDate"] = Ms(DateTimeOffset.UtcNow),
        ["originalPurchaseDate"] = Ms(DateTimeOffset.UtcNow),
        ["quantity"] = 1,
        ["type"] = type,
        ["inAppOwnershipType"] = "PURCHASED",
        ["signedDate"] = Ms(DateTimeOffset.UtcNow),
        ["environment"] = env,
        ["transactionReason"] = "PURCHASE",
        ["storefront"] = "MDV",
        ["storefrontId"] = "143488",
        ["price"] = price,
        ["currency"] = currency,
        ["appAccountToken"] = token?.ToString(),
        ["expiresDate"] = expires is { } e ? Ms(e) : null,
    };

    /// <summary>A one-time purchase Apple knows, signed as StoreKit would hand it to the app.</summary>
    private (string Jws, string Id) Bought(string product, Guid? token, string env = "Production", long? price = 15990)
    {
        var id = NextId();
        var t = Tx(id, id, product, token, env, "Consumable", price);
        api.AppStore.Transactions[id] = t;
        return (api.AppStore.Chain.Sign(t), id);
    }

    private (string Jws, string Original) Subscribed(Guid? token, DateTimeOffset expiry, int status = 1, bool autoRenew = true, string env = "Production")
    {
        var id = NextId();
        var t = Tx(id, id, "premium_monthly", token, env, "Auto-Renewable Subscription", 15990, expires: expiry);
        api.AppStore.Transactions[id] = t;
        api.AppStore.Subscriptions[id] = (status, id, Renewal(id, autoRenew));
        return (api.AppStore.Chain.Sign(t), id);
    }

    private static Dictionary<string, object?> Renewal(string original, bool autoRenew, DateTimeOffset? grace = null) => new()
    {
        ["originalTransactionId"] = original,
        ["autoRenewProductId"] = "premium_monthly",
        ["productId"] = "premium_monthly",
        ["autoRenewStatus"] = autoRenew ? 1 : 0,
        ["signedDate"] = Ms(DateTimeOffset.UtcNow),
        ["environment"] = "Production",
        ["gracePeriodExpiresDate"] = grace is { } g ? Ms(g) : null,
    };

    /// <summary>Apple renewed: a new transaction under the same original id.</summary>
    private string Renewed(string original, DateTimeOffset expiry, int status = 1, bool autoRenew = true)
    {
        var id = NextId();
        var first = api.AppStore.Transactions[original];
        var t = Tx(id, original, "premium_monthly", Guid.TryParse((string?)first["appAccountToken"], out var g) ? g : null,
            (string)first["environment"]!, "Auto-Renewable Subscription", 15990, expires: expiry);
        api.AppStore.Transactions[id] = t;
        api.AppStore.Subscriptions[original] = (status, id, Renewal(original, autoRenew));
        return id;
    }

    private async Task<(HttpStatusCode Status, Envelope<Result> Body)> RedeemAsync(Person who, string jws, Guid? eventId = null)
    {
        var res = await api.Client(who.Token).PostAsJsonAsync(Url, new { signedTransaction = jws, eventId });
        return (res.StatusCode, (await res.Content.ReadFromJsonAsync<Envelope<Result>>(Web))!);
    }

    private string Notification(string type, string transactionId, string? subtype = null, string env = "Production", string bundle = AppStoreStub.Bundle,
        StubChain? chain = null) =>
        (chain ?? api.AppStore.Chain).Sign(new Dictionary<string, object?>
        {
            ["notificationType"] = type,
            ["subtype"] = subtype,
            ["notificationUUID"] = Guid.NewGuid().ToString(),
            ["version"] = "2.0",
            ["signedDate"] = Ms(DateTimeOffset.UtcNow),
            ["data"] = new Dictionary<string, object?>
            {
                ["bundleId"] = bundle,
                ["appAppleId"] = AppStoreStub.AppAppleId,
                ["environment"] = env,
                ["signedTransactionInfo"] = api.AppStore.Chain.Sign(api.AppStore.Transactions[transactionId]),
            },
        });

    private Task<HttpResponseMessage> NotifyAsync(string signedPayload) =>
        api.Client().PostAsJsonAsync("/api/billing/app-store/notifications", new { signedPayload });

    private Task<Campaign> CampaignAsync(Guid id) => api.ReadAsync(db => db.Campaigns.AsNoTracking().FirstAsync(c => c.Id == id));
    private Task<AppUser> AccountAsync(Guid id) => api.ReadAsync(db => db.Users.AsNoTracking().FirstAsync(u => u.Id == id));
    private Task<List<Payment>> PaymentsAsync(Guid userId) => api.ReadAsync(db => db.Payments.AsNoTracking().Where(p => p.UserId == userId).OrderBy(p => p.CreatedAt).ToListAsync());
    private Task<AppStorePurchase?> RowAsync(string original) => api.ReadAsync(db => db.AppStorePurchases.AsNoTracking().FirstOrDefaultAsync(p => p.OriginalTransactionId == original));

    private async Task SweepAsync()
    {
        using var scope = api.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<IAppStorePurchases>().SweepAsync(DateTimeOffset.UtcNow);
    }

    /// <summary>Last checked long enough ago for the sweep to look again.</summary>
    private Task StaleAsync(string original) =>
        api.WriteAsync(async db => (await db.AppStorePurchases.FirstAsync(p => p.OriginalTransactionId == original)).LastCheckedAt = DateTimeOffset.UtcNow.AddHours(-7));

    private async Task AllowingTests(Func<Task> act)
    {
        Config["AppStore:AllowTestPurchases"] = "true";
        try { await act(); } finally { Config["AppStore:AllowTestPurchases"] = "false"; }
    }

    private async Task Unconfigured(Func<Task> act)
    {
        var saved = Config["AppStore:KeyId"];
        Config["AppStore:KeyId"] = "";
        try { await act(); } finally { Config["AppStore:KeyId"] = saved; }
    }

    private static string Code<T>(Envelope<T> body) => body.Errors?.FirstOrDefault()?.Code ?? "";

    // ----- The main path ------------------------------------------------------------------------

    [PostgresFact]
    public async Task A_verified_gold_pass_is_applied_and_recorded_with_Apples_price()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var (jws, id) = Bought("gold_pass", await TokenAsync(host, "gold_pass", ev));

        var (status, body) = await RedeemAsync(host, jws, ev);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Equal("applied", body.Data!.State);
        Assert.Equal(ev, body.Data.EventId);
        Assert.False(body.Data.Test);
        var c = await CampaignAsync(ev);
        Assert.Equal(EventPassKind.Gold, c.EventPass);
        Assert.True(c.EventPassUntil > DateTimeOffset.UtcNow.AddMonths(11));
        var p = Assert.Single(await PaymentsAsync(host.Id));
        Assert.Equal(PaymentProviders.AppStore, p.Provider);
        Assert.Equal(id, p.ProviderPaymentId);
        Assert.Equal(15.99m, p.Amount);
        Assert.Equal("USD", p.Currency);
        Assert.Equal(PaymentStatus.Paid, p.Status);
        var row = (await RowAsync(id))!;
        Assert.NotNull(row.FulfilledAt);
        Assert.Equal(ev, row.CampaignId);
    }

    [PostgresFact]
    public async Task The_client_asks_Apples_production_server_with_a_signed_token()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var (jws, id) = Bought("gold_pass", await TokenAsync(host, "gold_pass", ev));
        var refusedBefore = api.AppStore.TokensRefused;

        await RedeemAsync(host, jws, ev);

        Assert.Contains(api.AppStore.Requests, r => r.Method == "GET" && r.Path == $"/prod/inApps/v1/transactions/{id}");
        Assert.DoesNotContain(api.AppStore.Requests, r => r.Path.Contains(id) && r.Path.StartsWith("/sandbox/"));
        Assert.Equal(refusedBefore, api.AppStore.TokensRefused);
    }

    [PostgresFact]
    public async Task Keeping_photos_and_more_invites_do_what_the_same_card_payment_does()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var before = (await CampaignAsync(ev)).PaidInviteCapacity;
        await RedeemAsync(host, Bought("keep_photos_year", await TokenAsync(host, "keep_photos_year", ev)).Jws, ev);
        await RedeemAsync(host, Bought("extra_invites_100", await TokenAsync(host, "extra_invites_100", ev), price: 3990).Jws, ev);

        var c = await CampaignAsync(ev);
        Assert.True(c.KeepPhotosUntil > DateTimeOffset.UtcNow.AddMonths(11));
        Assert.Equal(before + 100, c.PaidInviteCapacity);
        Assert.Contains(await PaymentsAsync(host.Id), p => p.Kind == PaymentKind.Sending && p.Amount == 3.99m && p.InviteCount == 100);
    }

    [PostgresFact]
    public async Task Unconfigured_offers_nothing_and_answers_503()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var (jws, _) = Bought("gold_pass", await TokenAsync(host, "gold_pass", ev));
        await Unconfigured(async () =>
        {
            var offers = await api.Client(host.Token).GetFromJsonAsync<Envelope<Offers>>($"/api/billing/app-store/offers?eventId={ev}", Web);
            Assert.False(offers!.Data!.Available);
            Assert.Empty(offers.Data.Products);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await IntentAsync(host, "gold_pass", ev)).Status);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await RedeemAsync(host, jws, ev)).Status);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, (await NotifyAsync("x.y.z")).StatusCode);
        });
        Assert.Empty(await PaymentsAsync(host.Id));
        // Still open: once configured, the same transaction applies.
        Assert.Equal("applied", (await RedeemAsync(host, jws, ev)).Body.Data!.State);
    }

    [PostgresFact]
    public async Task The_key_works_written_on_one_line_as_an_env_file_holds_it()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var (_, id) = Bought("gold_pass", await TokenAsync(host, "gold_pass", ev));
        var oneLine = api.AppStore.PrivateKeyPem().Replace("\n", "\\n");
        Assert.DoesNotContain("\n", oneLine);
        // A client of its own, so no token signed earlier with the multi-line key is reused.
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AppStore:BundleId"] = AppStoreStub.Bundle, ["AppStore:IssuerId"] = AppStoreStub.Issuer, ["AppStore:KeyId"] = AppStoreStub.KeyId,
            ["AppStore:PrivateKey"] = oneLine, ["AppStore:ApiBaseUrl"] = api.AppStore.ProductionUrl,
        }).Build();
        var client = new AppStoreServerApi(api.Services.GetRequiredService<IHttpClientFactory>(), config,
            api.Services.GetRequiredService<IAppStoreSignedData>(), NullLogger<AppStoreServerApi>.Instance);
        var refused = api.AppStore.TokensRefused;

        Assert.Equal(id, (await client.GetTransactionAsync(id, sandbox: false, CancellationToken.None))!.TransactionId);
        Assert.Equal(refused, api.AppStore.TokensRefused);
    }

    // ----- Signatures ---------------------------------------------------------------------------

    [PostgresFact]
    public async Task A_transaction_signed_by_a_self_made_chain_is_refused_without_asking_Apple()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var token = await TokenAsync(host, "gold_pass", ev);
        using var rogue = new StubChain("Rogue");
        var id = NextId();
        var t = Tx(id, id, "gold_pass", token, "Production", "Consumable");
        api.AppStore.Transactions[id] = t;   // even if Apple knew the id

        // Its own root in x5c, and then the stub's real root in x5c: neither is trusted for being sent.
        foreach (var jws in new[] { rogue.Sign(t), rogue.Sign(t, x5cRoot: api.AppStore.Chain.Root) })
        {
            var (status, body) = await RedeemAsync(host, jws, ev);
            Assert.Equal(HttpStatusCode.Forbidden, status);
            Assert.Equal("app_store_bad_signature", Code(body));
        }
        Assert.Equal(0, api.AppStore.Count($"/transactions/{id}"));
        Assert.Empty(await PaymentsAsync(host.Id));
    }

    [PostgresFact]
    public async Task A_chain_without_Apples_marker_oids_is_refused()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var token = await TokenAsync(host, "gold_pass", ev);
        var id = NextId();
        var t = Tx(id, id, "gold_pass", token, "Production", "Consumable");
        api.AppStore.Transactions[id] = t;

        // Each chains to the trusted root itself; only the marker is missing.
        foreach (var chain in new[] { new StubChain("NoLeafOid", leafOid: false, under: api.AppStore.Chain), new StubChain("NoIntOid", intermediateOid: false, under: api.AppStore.Chain) })
            using (chain)
            {
                // Made the same way with its markers, it gets past the signature (to Apple, who doesn't know
                // this id): what refuses the others is the missing marker alone.
                using var marked = new StubChain("Marked", under: api.AppStore.Chain);
                var unknown = NextId();
                Assert.Equal(HttpStatusCode.NotFound, (await RedeemAsync(host, marked.Sign(Tx(unknown, unknown, "gold_pass", token, "Production", "Consumable")), ev)).Status);

                var (status, body) = await RedeemAsync(host, chain.Sign(t), ev);
                Assert.Equal(HttpStatusCode.Forbidden, status);
                Assert.Equal("app_store_bad_signature", Code(body));
            }
        Assert.Empty(await PaymentsAsync(host.Id));
    }

    [PostgresFact]
    public async Task A_changed_payload_or_another_algorithm_is_refused()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var token = await TokenAsync(host, "gold_pass", ev);
        var (jws, id) = Bought("gold_pass", token);
        var parts = jws.Split('.');
        var swapped = (Dictionary<string, object?>)new Dictionary<string, object?>(api.AppStore.Transactions[id]) { ["productId"] = "platinum_pass" };
        var forged = $"{parts[0]}.{Microsoft.IdentityModel.Tokens.Base64UrlEncoder.Encode(JsonSerializer.SerializeToUtf8Bytes(swapped))}.{parts[2]}";

        var (status, body) = await RedeemAsync(host, forged, ev);
        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("app_store_bad_signature", Code(body));

        var (s2, b2) = await RedeemAsync(host, api.AppStore.Chain.Sign(api.AppStore.Transactions[id], alg: "none"), ev);
        Assert.Equal(HttpStatusCode.BadRequest, s2);
        Assert.Equal("app_store_bad_transaction", Code(b2));
        Assert.Equal(HttpStatusCode.BadRequest, (await RedeemAsync(host, "not a jws", ev)).Status);
        Assert.Empty(await PaymentsAsync(host.Id));
    }

    [Fact]
    public void Apples_root_is_the_one_Apple_publishes()
    {
        using var root = System.Security.Cryptography.X509Certificates.X509Certificate2.CreateFromPem(AppStoreTrust.AppleRootCaG3Pem);
        Assert.Equal("63343ABFB89A6A03EBB57E9B3F5FA7BE7C4F5C756F3017B3A8C488C3653E9179", Convert.ToHexString(SHA256.HashData(root.RawData)));
        Assert.Equal("Apple Root CA - G3", root.GetNameInfo(System.Security.Cryptography.X509Certificates.X509NameType.SimpleName, false));
    }

    [Fact]
    public void With_the_production_trust_a_self_made_chain_is_refused()
    {
        using var chain = new StubChain("Made");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["AppStore:BundleId"] = AppStoreStub.Bundle }).Build();
        var production = new AppStoreSignedData(AppStoreTrust.Apple(), config, NullLogger<AppStoreSignedData>.Instance);
        var withItsRoot = new AppStoreSignedData(new AppStoreTrust([chain.Root], false), config, NullLogger<AppStoreSignedData>.Instance);
        var t = Tx("1", "1", "gold_pass", Guid.NewGuid(), "Production", "Consumable");

        // The same JWS reads when its root is trusted: what refuses it is the trust, nothing else.
        Assert.Equal("gold_pass", withItsRoot.Transaction(chain.Sign(t)).ProductId);
        var ex = Assert.Throws<AppStoreRefusedException>(() => production.Transaction(chain.Sign(t)));
        Assert.Equal("app_store_bad_signature", ex.ErrorCode);
    }

    [Fact]
    public void A_transaction_for_another_app_is_refused()
    {
        using var chain = new StubChain("Other");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["AppStore:BundleId"] = AppStoreStub.Bundle }).Build();
        var signed = new AppStoreSignedData(new AppStoreTrust([chain.Root], false), config, NullLogger<AppStoreSignedData>.Instance);
        var t = Tx("1", "1", "gold_pass", Guid.NewGuid(), "Production", "Consumable");
        t["bundleId"] = "com.someone.else";
        Assert.Equal("app_store_wrong_app", Assert.Throws<AppStoreRefusedException>(() => signed.Transaction(chain.Sign(t))).ErrorCode);
    }

    // ----- Whose and for what -------------------------------------------------------------------

    [PostgresFact]
    public async Task Another_accounts_transaction_cant_be_redeemed()
    {
        var host = await UserAsync();
        var thief = await UserAsync();
        var ev = await EventAsync(host);
        var (jws, _) = Bought("gold_pass", await TokenAsync(host, "gold_pass", ev));

        var (status, body) = await RedeemAsync(thief, jws, ev);

        Assert.Equal(HttpStatusCode.Forbidden, status);
        Assert.Equal("app_store_wrong_account", Code(body));
        Assert.Equal(EventPassKind.None, (await CampaignAsync(ev)).EventPass);
    }

    [PostgresFact]
    public async Task A_transaction_without_our_token_or_with_a_made_up_one_is_refused()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        foreach (var token in new Guid?[] { null, Guid.NewGuid() })
        {
            var (status, body) = await RedeemAsync(host, Bought("gold_pass", token).Jws, ev);
            Assert.Equal(HttpStatusCode.Forbidden, status);
            Assert.Equal("app_store_wrong_account", Code(body));
        }
        Assert.Empty(await PaymentsAsync(host.Id));
    }

    [PostgresFact]
    public async Task A_transaction_cant_be_moved_to_another_event_or_product()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var other = await EventAsync(host);
        var token = await TokenAsync(host, "gold_pass", ev);

        var (s1, b1) = await RedeemAsync(host, Bought("gold_pass", token).Jws, other);
        Assert.Equal(HttpStatusCode.Forbidden, s1);
        Assert.Equal("app_store_wrong_event", Code(b1));

        // The intent was for a Gold pass; a Platinum pass bought with it is refused, not upgraded.
        var (s2, b2) = await RedeemAsync(host, Bought("platinum_pass", token).Jws, ev);
        Assert.Equal(HttpStatusCode.Conflict, s2);
        Assert.Equal("app_store_wrong_product", Code(b2));
        Assert.Equal(EventPassKind.None, (await CampaignAsync(ev)).EventPass);
        Assert.Equal(EventPassKind.None, (await CampaignAsync(other)).EventPass);
    }

    [PostgresFact]
    public async Task An_intent_is_only_given_for_what_is_on_offer()
    {
        var host = await UserAsync();
        var stranger = await UserAsync();
        var ev = await EventAsync(host);

        Assert.Equal("app_store_not_offered", Code((await IntentAsync(stranger, "gold_pass", ev)).Body));
        Assert.Equal("app_store_not_offered", Code((await IntentAsync(host, "gold_pass_extend", ev)).Body));
        Assert.Equal(HttpStatusCode.BadRequest, (await IntentAsync(host, "gold_pass", null)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await IntentAsync(host, "premium_monthly", ev)).Status);
        Assert.Equal(HttpStatusCode.BadRequest, (await IntentAsync(host, "venue_monthly", ev)).Status);
        Assert.Equal(HttpStatusCode.Forbidden, (await api.Client().PostAsJsonAsync("/api/billing/app-store/intents", new { productId = "gold_pass", eventId = ev })).StatusCode);
    }

    [PostgresFact]
    public async Task What_is_offered_is_what_Google_Play_offers()
    {
        var host = await UserAsync();
        var fresh = await EventAsync(host);
        var gold = await EventAsync(host);
        await RedeemAsync(host, Bought("gold_pass", await TokenAsync(host, "gold_pass", gold)).Jws, gold);
        var std = await EventAsync(host, CampaignKind.SaveTheDate);

        foreach (var ev in new Guid?[] { null, fresh, gold, std })
        {
            var q = ev is null ? "" : $"?eventId={ev}";
            var apple = (await api.Client(host.Token).GetFromJsonAsync<Envelope<Offers>>($"/api/billing/app-store/offers{q}", Web))!.Data!;
            var play = (await api.Client(host.Token).GetFromJsonAsync<Envelope<Offers>>($"/api/billing/google-play/offers{q}", Web))!.Data!;
            Assert.True(apple.Available);
            Assert.Equal(play.Products, apple.Products);
        }
    }

    // ----- Once ---------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Handing_it_in_twice_gives_the_same_answer_and_one_pass()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var (jws, _) = Bought("gold_pass", await TokenAsync(host, "gold_pass", ev));

        var first = await RedeemAsync(host, jws, ev);
        var until = (await CampaignAsync(ev)).EventPassUntil;
        var second = await RedeemAsync(host, jws, ev);

        Assert.Equal("applied", first.Body.Data!.State);
        Assert.Equal("applied", second.Body.Data!.State);
        Assert.Single(await PaymentsAsync(host.Id));
        Assert.Equal(until, (await CampaignAsync(ev)).EventPassUntil);
    }

    [PostgresFact]
    public async Task Racing_redeems_really_overlap_and_still_give_one_pass()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var (jws, id) = Bought("gold_pass", await TokenAsync(host, "gold_pass", ev));
        api.AppStore.ReadDelayMs = 150;
        try
        {
            var all = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => RedeemAsync(host, jws, ev)));
            Assert.All(all, r => Assert.Equal(HttpStatusCode.OK, r.Status));
        }
        finally { api.AppStore.ReadDelayMs = 0; }

        Assert.True(api.AppStore.Count($"/transactions/{id}") >= 12);
        Assert.Single(await PaymentsAsync(host.Id));
        Assert.True((await CampaignAsync(ev)).EventPassUntil < DateTimeOffset.UtcNow.AddMonths(15));
        Assert.Equal(1, await api.ReadAsync(db => db.AppStorePurchases.CountAsync(p => p.OriginalTransactionId == id)));
    }

    [PostgresFact]
    public async Task The_original_transaction_id_is_unique_in_the_database_itself()
    {
        var id = NextId();
        var row = () => new AppStorePurchase { Id = Guid.NewGuid(), OriginalTransactionId = id, ProductId = "gold_pass", CreatedAt = DateTimeOffset.UtcNow };
        await api.WriteAsync(async db => { db.AppStorePurchases.Add(row()); await Task.CompletedTask; });
        await Assert.ThrowsAsync<DbUpdateException>(() => api.WriteAsync(async db => { db.AppStorePurchases.Add(row()); await Task.CompletedTask; }));
    }

    // ----- Test purchases and Apple's answers ---------------------------------------------------

    [PostgresFact]
    public async Task A_sandbox_purchase_is_refused_unless_allowed_and_Xcode_ones_never()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var (sandbox, id) = Bought("gold_pass", await TokenAsync(host, "gold_pass", ev), env: "Sandbox");

        var (status, body) = await RedeemAsync(host, sandbox, ev);
        Assert.Equal(HttpStatusCode.Conflict, status);
        Assert.Equal("app_store_test_refused", Code(body));
        Assert.True(api.AppStore.Count($"/transactions/{id}") == 0,
            string.Join(" | ", api.AppStore.Requests.Where(r => r.Path.Contains(id)).Select(r => $"{r.Method} {r.Path}")) + $" allow={Config["AppStore:AllowTestPurchases"]}");

        await AllowingTests(async () =>
        {
            var (s, b) = await RedeemAsync(host, sandbox, ev);
            Assert.Equal(HttpStatusCode.OK, s);
            Assert.True(b.Data!.Test);
            Assert.Contains(api.AppStore.Requests, r => r.Path == $"/sandbox/inApps/v1/transactions/{id}");

            var (xcode, _) = Bought("extra_invites_100", await TokenAsync(host, "extra_invites_100", ev), env: "Xcode");
            Assert.Equal("app_store_test_refused", Code((await RedeemAsync(host, xcode, ev)).Body));
        });
        var p = Assert.Single(await PaymentsAsync(host.Id));
        Assert.True(p.IsTest);
        Assert.EndsWith("· test", p.Description);
    }

    [PostgresFact]
    public async Task A_listed_test_account_App_Reviews_demo_can_buy_in_the_sandbox_and_no_one_else()
    {
        var demo = await UserAsync();
        var other = await UserAsync();
        var ev = await EventAsync(demo);
        var otherEv = await EventAsync(other);
        Config["AppStore:TestAccountIds"] = $"{Guid.NewGuid()}, {demo.Id}";
        try
        {
            var (mine, _) = Bought("gold_pass", await TokenAsync(demo, "gold_pass", ev), env: "Sandbox");
            var (theirs, theirsId) = Bought("gold_pass", await TokenAsync(other, "gold_pass", otherEv), env: "Sandbox");

            var (s1, b1) = await RedeemAsync(demo, mine, ev);
            Assert.Equal(HttpStatusCode.OK, s1);
            Assert.True(b1.Data!.Test);
            Assert.Equal("app_store_test_refused", Code((await RedeemAsync(other, theirs, otherEv)).Body));
            // Nor by a notification, where nobody is signed in: the owner decides.
            Assert.True((await NotifyAsync(Notification("ONE_TIME_CHARGE", theirsId, env: "Sandbox"))).IsSuccessStatusCode);
            Assert.Equal(EventPassKind.None, (await CampaignAsync(otherEv)).EventPass);
            Assert.Equal(EventPassKind.Gold, (await CampaignAsync(ev)).EventPass);

            // A production purchase by the demo account is an ordinary one.
            var (real, _) = Bought("extra_invites_100", await TokenAsync(demo, "extra_invites_100", ev), price: 3990);
            Assert.False((await RedeemAsync(demo, real, ev)).Body.Data!.Test);
        }
        finally { Config["AppStore:TestAccountIds"] = ""; }
    }

    [PostgresFact]
    public async Task Apple_being_down_decides_nothing_and_a_retry_works()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var (jws, id) = Bought("gold_pass", await TokenAsync(host, "gold_pass", ev));
        api.AppStore.FailNext[$"/transactions/{id}"] = 500;

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await RedeemAsync(host, jws, ev)).Status);
        Assert.Empty(await PaymentsAsync(host.Id));
        Assert.Equal("applied", (await RedeemAsync(host, jws, ev)).Body.Data!.State);
    }

    [PostgresFact]
    public async Task A_transaction_Apple_doesnt_know_or_has_refunded_unlocks_nothing()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var token = await TokenAsync(host, "gold_pass", ev);
        var (unknown, unknownId) = Bought("gold_pass", token);
        api.AppStore.Transactions.TryRemove(unknownId, out _);
        Assert.Equal(HttpStatusCode.NotFound, (await RedeemAsync(host, unknown, ev)).Status);

        // The app's copy says nothing of a refund; Apple's answer does.
        var (refunded, id) = Bought("gold_pass", token);
        api.AppStore.Transactions[id]["revocationDate"] = Ms(DateTimeOffset.UtcNow);
        api.AppStore.Transactions[id]["revocationReason"] = 0;
        Assert.Equal("app_store_voided", Code((await RedeemAsync(host, refunded, ev)).Body));
        Assert.Equal(EventPassKind.None, (await CampaignAsync(ev)).EventPass);
        Assert.Empty(await PaymentsAsync(host.Id));
    }

    // ----- Subscription -------------------------------------------------------------------------

    [PostgresFact]
    public async Task A_subscription_gives_Premium_until_Apples_expiry()
    {
        var user = await UserAsync();
        var expiry = DateTimeOffset.UtcNow.AddDays(30);
        var (jws, original) = Subscribed(await TokenAsync(user, "premium_monthly", null), expiry);

        var (status, body) = await RedeemAsync(user, jws);

        Assert.Equal(HttpStatusCode.OK, status);
        Assert.Null(body.Data!.EventId);
        var u = await AccountAsync(user.Id);
        Assert.Equal(SubscriptionTier.Premium, u.SubscriptionTier);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(Ms(expiry)), u.SubscriptionEndsAt);
        Assert.Null(u.AutoRenewKind);
        var row = (await RowAsync(original))!;
        Assert.True(row.AutoRenewing);
        Assert.Equal(1, row.SubscriptionStatus);
        Assert.Contains(api.AppStore.Requests, r => r.Path == $"/prod/inApps/v1/subscriptions/{original}");
    }

    [PostgresFact]
    public async Task Another_accounts_subscription_cant_be_redeemed_and_an_ended_one_isnt_applied()
    {
        var owner = await UserAsync();
        var other = await UserAsync();
        var (jws, _) = Subscribed(await TokenAsync(owner, "premium_monthly", null), DateTimeOffset.UtcNow.AddDays(30));
        Assert.Equal("app_store_wrong_account", Code((await RedeemAsync(other, jws)).Body));

        var late = await UserAsync();
        var (ended, _) = Subscribed(await TokenAsync(late, "premium_monthly", null), DateTimeOffset.UtcNow.AddDays(-1), status: 2);
        Assert.Equal("app_store_not_purchased", Code((await RedeemAsync(late, ended)).Body));
        Assert.Equal(SubscriptionTier.None, (await AccountAsync(late.Id)).SubscriptionTier);
    }

    [PostgresFact]
    public async Task A_renewal_notification_adds_one_paid_period_however_often_it_arrives()
    {
        var user = await UserAsync();
        var (jws, original) = Subscribed(await TokenAsync(user, "premium_monthly", null), DateTimeOffset.UtcNow.AddDays(1));
        await RedeemAsync(user, jws);
        var next = DateTimeOffset.UtcNow.AddDays(31);
        var renewal = Renewed(original, next);

        var note = Notification("DID_RENEW", renewal);
        var all = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => NotifyAsync(note)));

        Assert.All(all, r => Assert.True(r.IsSuccessStatusCode));
        var payments = await PaymentsAsync(user.Id);
        Assert.Equal(2, payments.Count);
        Assert.Contains(payments, p => p.ProviderPaymentId == renewal && p.Description!.Contains("renewed automatically"));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(Ms(next)), (await AccountAsync(user.Id)).SubscriptionEndsAt);
    }

    [PostgresFact]
    public async Task With_no_notification_the_sweep_notices_a_lapse_and_a_plan_from_elsewhere_stays()
    {
        var user = await UserAsync();
        var (jws, original) = Subscribed(await TokenAsync(user, "premium_monthly", null), DateTimeOffset.UtcNow.AddHours(12));
        await RedeemAsync(user, jws);
        var t = api.AppStore.Transactions[original];
        t["expiresDate"] = Ms(DateTimeOffset.UtcNow.AddMinutes(-5));
        api.AppStore.Subscriptions[original] = (2, original, Renewal(original, false));
        await StaleAsync(original);

        await SweepAsync();

        var u = await AccountAsync(user.Id);
        Assert.True(u.SubscriptionEndsAt <= DateTimeOffset.UtcNow);
        Assert.False((await RowAsync(original))!.AutoRenewing);

        // An admin-given year isn't ended by a store subscription lapsing.
        var gifted = await UserAsync();
        var (g, gOriginal) = Subscribed(await TokenAsync(gifted, "premium_monthly", null), DateTimeOffset.UtcNow.AddHours(12));
        await RedeemAsync(gifted, g);
        var year = DateTimeOffset.UtcNow.AddYears(1);
        await api.WriteAsync(async db => (await db.Users.FirstAsync(x => x.Id == gifted.Id)).SubscriptionEndsAt = year);
        api.AppStore.Subscriptions[gOriginal] = (2, gOriginal, Renewal(gOriginal, false));
        await StaleAsync(gOriginal);
        await SweepAsync();
        Assert.Equal(2, (await RowAsync(gOriginal))!.SubscriptionStatus);
        Assert.True((await AccountAsync(gifted.Id)).SubscriptionEndsAt > DateTimeOffset.UtcNow.AddMonths(11));
    }

    [PostgresFact]
    public async Task A_revoked_subscription_ends_the_plan_at_once()
    {
        var user = await UserAsync();
        var (jws, original) = Subscribed(await TokenAsync(user, "premium_monthly", null), DateTimeOffset.UtcNow.AddDays(20));
        await RedeemAsync(user, jws);
        api.AppStore.Transactions[original]["revocationDate"] = Ms(DateTimeOffset.UtcNow);
        api.AppStore.Subscriptions[original] = (5, original, Renewal(original, false));

        Assert.True((await NotifyAsync(Notification("REFUND", original))).IsSuccessStatusCode);

        Assert.True((await AccountAsync(user.Id)).SubscriptionEndsAt <= DateTimeOffset.UtcNow);
        Assert.Equal(PaymentStatus.Refunded, Assert.Single(await PaymentsAsync(user.Id)).Status);
    }

    [PostgresFact]
    public async Task Billing_says_the_plan_is_managed_in_the_App_Store()
    {
        var user = await UserAsync();
        await RedeemAsync(user, Subscribed(await TokenAsync(user, "premium_monthly", null), DateTimeOffset.UtcNow.AddDays(30)).Jws);
        using var doc = JsonDocument.Parse(await api.Client(user.Token).GetStringAsync("/api/billing"));
        var account = doc.RootElement.GetProperty("data").GetProperty("account");
        Assert.Equal("app_store", account.GetProperty("store").GetString());
        Assert.True(account.GetProperty("storeAutoRenew").GetBoolean());
    }

    // ----- Notifications ------------------------------------------------------------------------

    [PostgresFact]
    public async Task A_notification_finishes_a_purchase_when_the_app_never_came_back()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var (_, id) = Bought("gold_pass", await TokenAsync(host, "gold_pass", ev));

        Assert.True((await NotifyAsync(Notification("ONE_TIME_CHARGE", id))).IsSuccessStatusCode);

        Assert.Equal(EventPassKind.Gold, (await CampaignAsync(ev)).EventPass);
        Assert.Contains(api.AppStore.Requests, r => r.Path == $"/prod/inApps/v1/transactions/{id}");
    }

    [PostgresFact]
    public async Task Forged_foreign_and_sandbox_notifications_change_nothing()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var (_, id) = Bought("gold_pass", await TokenAsync(host, "gold_pass", ev));
        using var rogue = new StubChain("Rogue");

        Assert.Equal(HttpStatusCode.Forbidden, (await NotifyAsync(Notification("ONE_TIME_CHARGE", id, chain: rogue))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await NotifyAsync(Notification("ONE_TIME_CHARGE", id, bundle: "com.other.app"))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await NotifyAsync("rubbish")).StatusCode);
        Assert.True((await NotifyAsync(Notification("ONE_TIME_CHARGE", id, env: "Sandbox"))).IsSuccessStatusCode);
        var test = api.AppStore.Chain.Sign(new { notificationType = "TEST", notificationUUID = Guid.NewGuid(), version = "2.0", data = new { bundleId = AppStoreStub.Bundle, environment = "Production" } });
        Assert.True((await NotifyAsync(test)).IsSuccessStatusCode);

        Assert.Equal(EventPassKind.None, (await CampaignAsync(ev)).EventPass);
        Assert.Equal(0, api.AppStore.Count($"/transactions/{id}"));
    }

    [PostgresFact]
    public async Task Apple_being_down_during_a_notification_makes_Apple_try_again()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var (_, id) = Bought("gold_pass", await TokenAsync(host, "gold_pass", ev));
        api.AppStore.FailNext[$"/transactions/{id}"] = 503;

        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await NotifyAsync(Notification("ONE_TIME_CHARGE", id))).StatusCode);
        Assert.True((await NotifyAsync(Notification("ONE_TIME_CHARGE", id))).IsSuccessStatusCode);
        Assert.Equal(EventPassKind.Gold, (await CampaignAsync(ev)).EventPass);
    }

    // ----- Refunds ------------------------------------------------------------------------------

    [PostgresFact]
    public async Task A_refund_takes_the_pass_back_once_and_the_transaction_cant_bring_it_back()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var (jws, id) = Bought("gold_pass", await TokenAsync(host, "gold_pass", ev));
        await RedeemAsync(host, jws, ev);
        api.AppStore.Transactions[id]["revocationDate"] = Ms(DateTimeOffset.UtcNow);

        var note = Notification("REFUND", id);
        Assert.True((await NotifyAsync(note)).IsSuccessStatusCode);
        Assert.True((await NotifyAsync(note)).IsSuccessStatusCode);

        Assert.Equal(EventPassKind.None, (await CampaignAsync(ev)).EventPass);
        Assert.Equal(PaymentStatus.Refunded, Assert.Single(await PaymentsAsync(host.Id)).Status);
        Assert.Equal(1, await api.ReadAsync(db => db.AuditLogs.CountAsync(a => a.Action == "billing.app_store.voided" && a.CampaignId == ev)));
        api.AppStore.Transactions[id].Remove("revocationDate");
        Assert.Equal("app_store_voided", Code((await RedeemAsync(host, jws, ev)).Body));
        Assert.Equal(EventPassKind.None, (await CampaignAsync(ev)).EventPass);
    }

    [PostgresFact]
    public async Task A_notification_saying_refund_for_a_purchase_Apple_hasnt_refunded_takes_nothing()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var (jws, id) = Bought("gold_pass", await TokenAsync(host, "gold_pass", ev));
        await RedeemAsync(host, jws, ev);

        Assert.True((await NotifyAsync(Notification("REFUND", id))).IsSuccessStatusCode);

        Assert.Equal(EventPassKind.Gold, (await CampaignAsync(ev)).EventPass);
        Assert.Null((await RowAsync(id))!.VoidedAt);
    }

    [PostgresFact]
    public async Task Apples_notification_history_catches_a_refund_that_never_reached_us()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var (jws, id) = Bought("keep_photos_year", await TokenAsync(host, "keep_photos_year", ev));
        await RedeemAsync(host, jws, ev);
        api.AppStore.Transactions[id]["revocationDate"] = Ms(DateTimeOffset.UtcNow);
        // Others around it, so the paging is walked to reach it.
        for (var i = 0; i < 3; i++)
            api.AppStore.History.Add((false, api.AppStore.Chain.Sign(new { notificationType = "TEST", notificationUUID = Guid.NewGuid(), version = "2.0" })));
        api.AppStore.History.Add((false, Notification("REFUND", id)));

        await SweepAsync();

        Assert.Null((await CampaignAsync(ev)).KeepPhotosUntil);
        Assert.NotNull((await RowAsync(id))!.VoidedAt);
        Assert.True(api.AppStore.Count("/prod/inApps/v1/notifications/history") >= 2);
    }

    [PostgresFact]
    public async Task Cancelling_an_event_doesnt_send_an_App_Store_payment_to_the_card_gateway()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        await RedeemAsync(host, Bought("gold_pass", await TokenAsync(host, "gold_pass", ev)).Jws, ev);

        var res = await api.Client(host.Token).PostAsync($"/api/campaigns/{ev}/cancel", null);

        Assert.True(res.IsSuccessStatusCode, await res.Content.ReadAsStringAsync());
        var p = Assert.Single(await PaymentsAsync(host.Id));
        Assert.Equal(PaymentStatus.Paid, p.Status);
        Assert.Empty(await api.ReadAsync(db => db.Refunds.Where(r => r.PaymentId == p.Id).ToListAsync()));
    }

    [PostgresFact]
    public async Task No_signed_transaction_or_key_ever_reaches_the_logs()
    {
        var host = await UserAsync();
        var ev = await EventAsync(host);
        var (jws, id) = Bought("gold_pass", await TokenAsync(host, "gold_pass", ev));
        api.AppStore.FailNext[$"/transactions/{id}"] = 500;
        await RedeemAsync(host, jws, ev);
        await RedeemAsync(host, jws, ev);
        using var rogue = new StubChain("Rogue");
        var forged = rogue.Sign(api.AppStore.Transactions[id]);
        await RedeemAsync(host, forged, ev);
        var (sub, original) = Subscribed(await TokenAsync(host, "premium_monthly", null), DateTimeOffset.UtcNow.AddHours(3));
        await RedeemAsync(host, sub);
        api.AppStore.Subscriptions[original] = (2, original, Renewal(original, false));
        var note = Notification("EXPIRED", original);
        await NotifyAsync(note);
        await SweepAsync();

        Assert.Contains(api.Logs.Lines, l => l.Contains("App Store", StringComparison.Ordinal));
        foreach (var line in api.Logs.Lines)
        {
            foreach (var secret in new[] { jws, forged, sub, note })
            {
                Assert.DoesNotContain(secret, line);
                Assert.DoesNotContain(secret.Split('.')[1][..40], line);
            }
            Assert.DoesNotContain("PRIVATE KEY", line);
            Assert.DoesNotContain("inApps/v1", line);
        }
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

    [PostgresFact]
    public async Task Deleting_the_account_tells_them_to_cancel_with_Apple_and_forgets_who_bought()
    {
        var user = await UserAsync();
        var (jws, original) = Subscribed(await TokenAsync(user, "premium_monthly", null), DateTimeOffset.UtcNow.AddDays(20));
        await RedeemAsync(user, jws);

        var summary = (await api.Client(user.Token).GetFromJsonAsync<JsonElement>("/api/auth/me/deletion/summary", Web)).GetProperty("data");
        Assert.True(summary.GetProperty("autoRenews").GetBoolean());
        Assert.Contains("Cancel it in your Apple account", summary.GetProperty("storeNote").GetString());

        await DeleteAccountAsync(user);

        var row = (await RowAsync(original))!;
        Assert.Null(row.UserId);
        Assert.False(row.AutoRenewing);
        Assert.Empty(await api.ReadAsync(db => db.AppStoreIntents.Where(i => i.UserId == user.Id).ToListAsync()));
        // A later renewal for it changes nothing and isn't an error.
        var renewal = Renewed(original, DateTimeOffset.UtcNow.AddDays(50));
        Assert.True((await NotifyAsync(Notification("DID_RENEW", renewal))).IsSuccessStatusCode);
        Assert.Empty(await api.ReadAsync(db => db.Payments.Where(p => p.ProviderPaymentId == renewal).ToListAsync()));
    }
}
