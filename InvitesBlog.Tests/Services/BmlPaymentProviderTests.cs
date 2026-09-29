using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using InvitesBlog.Application.Abstractions;
using InvitesBlog.Infrastructure.Payments;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Xunit;

namespace InvitesBlog.Tests.Services;

/// <summary>
/// BML Connect, against a stand-in for its API: what we send (amount in laari, our payment id as
/// localId, the key as it is in Authorization, card-on-file only when asked) and how we read what
/// comes back. A webhook is trusted only with a valid signature, and even then only what BML's API
/// says about the transaction counts.
/// </summary>
public class BmlPaymentProviderTests
{
    private const string Key = "sk_test_key";
    private readonly StubBml _bml = new();

    private BmlPaymentProvider Provider(bool requireSignature = true)
    {
        var factory = Substitute.For<IHttpClientFactory>();
        factory.CreateClient(BmlPaymentProvider.ClientName)
            .Returns(_ => new HttpClient(_bml) { BaseAddress = new Uri("https://bml.test/base/path/") });
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Payments:Bml:ApiKey"] = Key,
            ["Payments:Bml:RequireSignature"] = requireSignature ? "true" : "false",
            ["Urls:ApiBase"] = "https://invites.blog",
        }).Build();
        return new BmlPaymentProvider(factory, config, NullLogger<BmlPaymentProvider>.Instance);
    }

    private static Dictionary<string, string> Signed(string key = Key)
    {
        var nonce = "n-123";
        var ts = "1700000000";
        var sig = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(nonce + ts + key)));
        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["X-Signature-Nonce"] = nonce, ["X-Signature-Timestamp"] = ts, ["X-Signature"] = sig,
        };
    }

    private static string Tx(string id, string state, int cents = 69900, string currency = "MVR", string? localId = null) =>
        new JsonObject
        {
            ["id"] = id, ["state"] = state, ["amount"] = cents, ["currency"] = currency,
            ["localId"] = localId, ["url"] = $"https://pay.bml.test/{id}",
        }.ToJsonString();

    // ----- taking a payment -------------------------------------------------------------------

    [Fact]
    public async Task A_checkout_creates_a_transaction_in_laari_with_our_payment_id_and_returns_its_page()
    {
        var paymentId = Guid.NewGuid();
        _bml.On(HttpMethod.Post, "/public/v2/transactions", HttpStatusCode.Created, Tx("tx1", "INITIATED"));

        var session = await Provider().CreateCheckoutSessionAsync(new CreateCheckoutSessionRequest(
            Guid.Empty, "PartyPass", 699.00m, "MVR", 0, "https://invites.blog/billing?paid=x", "https://invites.blog/billing")
        {
            PaymentId = paymentId, Description = "Party pass · Ali's birthday",
        }, default);

        Assert.Equal("tx1", session.SessionId);
        Assert.Equal("https://pay.bml.test/tx1", session.CheckoutUrl);
        var sent = _bml.Requests.Single();
        Assert.Equal(Key, sent.Authorization);
        var body = JsonNode.Parse(sent.Body)!;
        Assert.Equal(69900, body["amount"]!.GetValue<long>());
        Assert.Equal("MVR", body["currency"]!.GetValue<string>());
        Assert.Equal(paymentId.ToString(), body["localId"]!.GetValue<string>());
        Assert.Equal("Party pass · Ali's birthday", body["customerReference"]!.GetValue<string>());
        Assert.Equal("https://invites.blog/billing?paid=x", body["redirectUrl"]!.GetValue<string>());
        Assert.Equal("https://invites.blog/api/payments/webhook", body["webhook"]!.GetValue<string>());
        Assert.Null(body["tokenizationDetails"]);
    }

    [Fact]
    public async Task A_plan_checkout_saves_the_card_for_the_customer()
    {
        _bml.On(HttpMethod.Post, "/public/v2/transactions", HttpStatusCode.Created, Tx("tx2", "INITIATED"));

        await Provider().CreateCheckoutSessionAsync(new CreateCheckoutSessionRequest(
            Guid.Empty, "PremiumMonthly", 450m, "MVR", 0, "https://invites.blog/billing", "https://invites.blog/billing")
        {
            PaymentId = Guid.NewGuid(), CustomerId = "cus_1", SaveCard = true,
        }, default);

        var body = JsonNode.Parse(_bml.Requests.Single().Body)!;
        Assert.Equal("cus_1", body["customerId"]!.GetValue<string>());
        Assert.True(body["tokenizationDetails"]!["tokenize"]!.GetValue<bool>());
        Assert.Equal("UNSCHEDULED", body["tokenizationDetails"]!["paymentType"]!.GetValue<string>());
        Assert.Equal("UNSCHEDULED", body["tokenizationDetails"]!["recurringFrequency"]!.GetValue<string>());
    }

    [Theory]
    [InlineData(0.01, 1)]
    [InlineData(199, 19900)]
    [InlineData(12.345, 1235)]
    public async Task Amounts_are_sent_as_whole_laari(decimal mvr, long laari)
    {
        _bml.On(HttpMethod.Post, "/public/v2/transactions", HttpStatusCode.Created, Tx("tx", "INITIATED"));
        await Provider().CreateCheckoutSessionAsync(new CreateCheckoutSessionRequest(
            Guid.Empty, "x", mvr, "MVR", 0, "https://a/b", "https://a/b"), default);
        Assert.Equal(laari, JsonNode.Parse(_bml.Requests.Single().Body)!["amount"]!.GetValue<long>());
    }

    [Fact]
    public async Task A_refused_checkout_surfaces_and_is_never_retried()
    {
        _bml.On(HttpMethod.Post, "/public/v2/transactions", HttpStatusCode.BadRequest,
            """{"message":"Invalid amount","code":"PP-G-400"}""");

        await Assert.ThrowsAsync<HttpRequestException>(() => Provider().CreateCheckoutSessionAsync(
            new CreateCheckoutSessionRequest(Guid.Empty, "x", 1m, "MVR", 0, "https://a/b", "https://a/b"), default));
        Assert.Single(_bml.Requests);
    }

    // ----- the result -------------------------------------------------------------------------

    [Theory]
    [InlineData("CONFIRMED", WebhookEventKind.PaymentSucceeded)]
    [InlineData("CANCELLED", WebhookEventKind.PaymentFailed)]
    [InlineData("FAILED", WebhookEventKind.PaymentFailed)]
    [InlineData("INITIATED", WebhookEventKind.Ignored)]
    [InlineData("QR_CODE_GENERATED", WebhookEventKind.Ignored)]
    [InlineData("AUTHORIZED", WebhookEventKind.Ignored)]
    [InlineData("REFUND_REQUESTED", WebhookEventKind.Ignored)]
    public async Task The_status_is_what_BMLs_transaction_says(string state, WebhookEventKind kind)
    {
        _bml.On(HttpMethod.Get, "/public/transactions/tx9", HttpStatusCode.OK, Tx("tx9", state, 19900, "MVR", "local-1"));

        var result = await Provider().GetStatusAsync("tx9", default);

        Assert.Equal(kind, result.Kind);
        Assert.Equal("tx9", result.ProviderSessionId);
        Assert.Equal(199m, result.Amount);
        Assert.Equal("MVR", result.Currency);
        Assert.Equal("local-1", result.LocalId);
    }

    [Fact]
    public async Task The_transaction_is_found_on_the_guides_path_when_the_reference_path_isnt_there()
    {
        _bml.On(HttpMethod.Get, "/public/transactions/tx8", HttpStatusCode.NotFound, "{}");
        _bml.On(HttpMethod.Get, "/public/v2/transactions/tx8", HttpStatusCode.OK, Tx("tx8", "CONFIRMED"));

        var result = await Provider().GetStatusAsync("tx8", default);

        Assert.Equal(WebhookEventKind.PaymentSucceeded, result.Kind);
    }

    // ----- webhooks ---------------------------------------------------------------------------

    private static string Webhook(string txId, string claimedState) => new JsonObject
    {
        ["eventType"] = "NOTIFY_TRANSACTION_CHANGE", ["transactionId"] = txId, ["state"] = claimedState,
        ["amount"] = 69900, ["currency"] = "MVR",
    }.ToJsonString();

    [Fact]
    public async Task A_signed_webhook_is_confirmed_with_BMLs_API_and_its_answer_is_what_counts()
    {
        // The body claims CONFIRMED; BML's API says the transaction is still waiting.
        _bml.On(HttpMethod.Get, "/public/transactions/tx5", HttpStatusCode.OK, Tx("tx5", "QR_CODE_GENERATED"));

        var result = await Provider().HandleWebhookAsync(Webhook("tx5", "CONFIRMED"), Signed(), default);

        Assert.Equal(WebhookEventKind.Ignored, result.Kind);
        Assert.Contains(_bml.Requests, r => r.Path == "/public/transactions/tx5");
    }

    [Fact]
    public async Task A_signed_webhook_for_a_confirmed_transaction_is_a_success()
    {
        _bml.On(HttpMethod.Get, "/public/transactions/tx6", HttpStatusCode.OK, Tx("tx6", "CONFIRMED", 69900, "MVR", "pay-6"));

        var result = await Provider().HandleWebhookAsync(Webhook("tx6", "CONFIRMED"), Signed(), default);

        Assert.Equal(WebhookEventKind.PaymentSucceeded, result.Kind);
        Assert.Equal("tx6", result.ProviderSessionId);
        Assert.Equal(699m, result.Amount);
        Assert.Equal("pay-6", result.LocalId);
    }

    [Fact]
    public async Task A_webhook_signed_with_the_wrong_key_is_refused_without_asking_BML()
    {
        var result = await Provider().HandleWebhookAsync(Webhook("tx7", "CONFIRMED"), Signed("someone-elses-key"), default);

        Assert.Equal(WebhookEventKind.Unknown, result.Kind);
        Assert.Empty(_bml.Requests);
    }

    [Fact]
    public async Task An_unsigned_webhook_is_refused()
    {
        var result = await Provider().HandleWebhookAsync(Webhook("tx7", "CONFIRMED"), new Dictionary<string, string>(), default);

        Assert.Equal(WebhookEventKind.Unknown, result.Kind);
        Assert.Empty(_bml.Requests);
    }

    [Fact]
    public async Task With_signatures_not_required_an_unsigned_webhook_is_still_only_a_hint()
    {
        _bml.On(HttpMethod.Get, "/public/transactions/tx4", HttpStatusCode.OK, Tx("tx4", "FAILED"));

        var result = await Provider(requireSignature: false)
            .HandleWebhookAsync(Webhook("tx4", "CONFIRMED"), new Dictionary<string, string>(), default);

        Assert.Equal(WebhookEventKind.PaymentFailed, result.Kind);
    }

    [Fact]
    public async Task A_card_saved_notice_is_acknowledged_and_changes_nothing()
    {
        var body = """{"eventType":"NOTIFY_TOKENISATION_STATUS","customerId":"cus_1","transactionId":"tx3","tokenisationStatus":"TOKENISATION_SUCCESS"}""";

        var result = await Provider().HandleWebhookAsync(body, Signed(), default);

        Assert.Equal(WebhookEventKind.Ignored, result.Kind);
        Assert.Empty(_bml.Requests);
    }

    [Fact]
    public async Task Garbage_is_not_a_webhook()
    {
        var result = await Provider().HandleWebhookAsync("not json", Signed(), default);
        Assert.Equal(WebhookEventKind.Unknown, result.Kind);
    }

    // ----- saved cards ------------------------------------------------------------------------

    [Fact]
    public async Task A_customer_is_created_with_name_and_email()
    {
        _bml.On(HttpMethod.Post, "/public-customers", HttpStatusCode.Created, """{"id":"cus_9","name":"A","email":"a@b.c"}""");

        var id = await Provider().CreateCustomerAsync("Aisha", "a@b.c", default);

        Assert.Equal("cus_9", id);
        var body = JsonNode.Parse(_bml.Requests.Single().Body)!;
        Assert.Equal("Aisha", body["name"]!.GetValue<string>());
        Assert.Equal("a@b.c", body["email"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{"items":[{"id":"t1","tokenType":"CARD","deleted":false}],"count":1}""", true)]
    [InlineData("""{"items":[{"id":"t1","tokenType":"CARD","deleted":true}],"count":1}""", false)]
    [InlineData("""{"items":[],"count":0}""", false)]
    public async Task A_customer_has_a_card_only_if_one_is_live(string tokens, bool expected)
    {
        _bml.On(HttpMethod.Get, "/public-customers/cus_1/tokens", HttpStatusCode.OK, tokens);
        Assert.Equal(expected, await Provider().HasSavedCardAsync("cus_1", default));
    }

    [Fact]
    public async Task A_saved_card_charge_makes_a_transaction_for_the_customer_then_charges_it()
    {
        var paymentId = Guid.NewGuid();
        _bml.On(HttpMethod.Get, "/public-customers/cus_1/tokens", HttpStatusCode.OK,
            """{"items":[{"id":"tok_old","tokenType":"CARD","deleted":false,"defaultToken":false},{"id":"tok_def","tokenType":"CARD","deleted":false,"defaultToken":true}],"count":2}""");
        _bml.On(HttpMethod.Post, "/public/v2/transactions", HttpStatusCode.Created, Tx("tx10", "INITIATED"));
        _bml.On(HttpMethod.Post, "/public-customers/charge", HttpStatusCode.OK, Tx("tx10", "CONFIRMED", 45000, "MVR", paymentId.ToString()));

        var result = await Provider().ChargeSavedCardAsync(
            new SavedCardCharge("cus_1", 450m, "MVR", paymentId, "Premium pass, a month"), default);

        Assert.Equal(WebhookEventKind.PaymentSucceeded, result.Kind);
        Assert.Equal("tx10", result.ProviderSessionId);
        Assert.Equal(450m, result.Amount);
        var create = JsonNode.Parse(_bml.Requests.Single(r => r.Path == "/public/v2/transactions").Body)!;
        Assert.Equal("cus_1", create["customerId"]!.GetValue<string>());
        Assert.False(create["tokenizationDetails"]!["tokenize"]!.GetValue<bool>());
        Assert.Equal(paymentId.ToString(), create["localId"]!.GetValue<string>());
        var charge = JsonNode.Parse(_bml.Requests.Single(r => r.Path == "/public-customers/charge").Body)!;
        Assert.Equal("cus_1", charge["customerId"]!.GetValue<string>());
        Assert.Equal("tok_def", charge["tokenId"]!.GetValue<string>());
        Assert.Equal("tx10", charge["transactionId"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_charge_the_bank_wants_confirmed_carries_the_page_to_confirm_it()
    {
        _bml.On(HttpMethod.Get, "/public-customers/cus_1/tokens", HttpStatusCode.OK,
            """{"items":[{"id":"tok_old","tokenType":"CARD","deleted":false,"defaultToken":false},{"id":"tok_def","tokenType":"CARD","deleted":false,"defaultToken":true}],"count":2}""");
        _bml.On(HttpMethod.Post, "/public/v2/transactions", HttpStatusCode.Created, Tx("tx11", "INITIATED"));
        _bml.On(HttpMethod.Post, "/public-customers/charge", HttpStatusCode.OK,
            """{"id":"tx11","state":"QR_CODE_GENERATED","amount":45000,"currency":"MVR","nextAction":"https://3ds.bank/x"}""");

        var result = await Provider().ChargeSavedCardAsync(
            new SavedCardCharge("cus_1", 450m, "MVR", Guid.NewGuid(), "Premium pass, a month"), default);

        Assert.Equal(WebhookEventKind.Ignored, result.Kind);
        Assert.Equal("https://3ds.bank/x", result.NextActionUrl);
    }

    [Fact]
    public async Task A_customer_without_a_live_card_is_not_charged()
    {
        _bml.On(HttpMethod.Get, "/public-customers/cus_1/tokens", HttpStatusCode.OK, """{"items":[],"count":0}""");

        var result = await Provider().ChargeSavedCardAsync(new SavedCardCharge("cus_1", 450m, "MVR", Guid.NewGuid(), "Premium pass"), default);

        Assert.Equal(WebhookEventKind.PaymentFailed, result.Kind);
        Assert.DoesNotContain(_bml.Requests, r => r.Method == "POST");
    }

    [Fact]
    public async Task Refunds_are_not_offered_by_BMLs_API()
    {
        var r = await Provider().RefundAsync(new RefundRequest("tx", 10m), default);
        Assert.False(r.Success);
    }

    /// <summary>A stand-in for BML's API: canned answers by method and path, and a record of every request.</summary>
    private sealed class StubBml : HttpMessageHandler
    {
        private readonly Dictionary<(string, string), (HttpStatusCode, string)> _answers = new();
        public List<(string Method, string Path, string Body, string? Authorization)> Requests { get; } = [];

        public void On(HttpMethod method, string path, HttpStatusCode status, string body) =>
            _answers[(method.Method, path)] = (status, body);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var body = request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct);
            // Requests must stay under the base address's path, as they must under BML's mock server.
            Assert.StartsWith("/base/path/", request.RequestUri!.AbsolutePath);
            var path = request.RequestUri.AbsolutePath["/base/path".Length..];
            Requests.Add((request.Method.Method, path, body,
                request.Headers.TryGetValues("Authorization", out var a) ? a.Single() : null));
            var (status, answer) = _answers.TryGetValue((request.Method.Method, path), out var found)
                ? found : (HttpStatusCode.NotFound, "{}");
            return new HttpResponseMessage(status) { Content = new StringContent(answer, Encoding.UTF8, "application/json") };
        }
    }
}
