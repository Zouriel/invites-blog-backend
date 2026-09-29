using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using InvitesBlog.Application.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace InvitesBlog.Infrastructure.Payments;

/// <summary>
/// Bank of Maldives' card gateway, BML Connect (Payments__Provider=Bml). Docs:
/// https://bankofmaldives.stoplight.io/docs/bml-connect
///
/// <para><b>Taking a payment</b> is BML's Redirect method: create a transaction with our payment id as
/// its <c>localId</c>, send the buyer to the URL BML returns, and BML sends them back to our return
/// address when they're done.</para>
///
/// <para><b>Learning the result</b> is the webhook, as BML's merchant rules require. A webhook is only
/// a hint, though: its signature covers a nonce and a timestamp, not the body, so anything it claims
/// is confirmed by asking BML's API for the transaction before anything is recorded. The same query
/// answers <see cref="GetStatusAsync"/>, which finishes payments whose webhook never came.</para>
///
/// <para><b>Saved cards</b> (card-on-file) renew Premium and Venue: the first payment is made with
/// <c>tokenize</c> for a BML customer, and each renewal creates a transaction for that customer and
/// charges their default card.</para>
///
/// <para>Nothing here retries a POST: creating a transaction or charging a card twice would charge
/// twice. A failed call surfaces; the buyer or the renewal sweep tries again.</para>
/// </summary>
public sealed class BmlPaymentProvider(
    IHttpClientFactory http, IConfiguration config, ILogger<BmlPaymentProvider> logger)
    : IPaymentProvider, IRecurringPaymentProvider
{
    public const string ClientName = "bml";
    public const string UatBaseUrl = "https://api.uat.merchants.bankofmaldives.com.mv";

    public string Name => "Bml";

    private string ApiKey => config["Payments:Bml:ApiKey"]
        ?? throw new InvalidOperationException("Payments:Bml:ApiKey is not set.");

    /// <summary>Where BML posts transaction updates: this API's webhook.</summary>
    private string WebhookUrl => config["Payments:Bml:WebhookUrl"]
        ?? $"{(config["Urls:ApiBase"] ?? "http://localhost:8080").TrimEnd('/')}/api/payments/webhook";

    /// <summary>Refuse webhooks without a valid signature. On by default; missed ones are caught by the status sweep.</summary>
    private bool RequireSignature => config.GetValue("Payments:Bml:RequireSignature", true);

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    // ----- taking a payment -------------------------------------------------------------------

    public async Task<CheckoutSessionResult> CreateCheckoutSessionAsync(CreateCheckoutSessionRequest r, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["amount"] = ToCents(r.Amount),
            ["currency"] = r.Currency,
            ["redirectUrl"] = r.SuccessUrl,
            ["webhook"] = WebhookUrl,
        };
        if (r.PaymentId is { } id) body["localId"] = id.ToString();
        if (!string.IsNullOrWhiteSpace(r.Description)) body["customerReference"] = Trim(r.Description, 140);
        if (r.SaveCard && !string.IsNullOrWhiteSpace(r.CustomerId))
        {
            body["customerId"] = r.CustomerId;
            // Pre-fills BML's form with the customer's details.
            body["customerAsPayer"] = true;
            // Card-on-file: the card this payment is made with is kept for later, merchant-initiated
            // charges. UNSCHEDULED/UNSCHEDULED is what BML's card-on-file guide fixes these to.
            body["tokenizationDetails"] = new JsonObject
            {
                ["tokenize"] = true,
                ["paymentType"] = "UNSCHEDULED",
                ["recurringFrequency"] = "UNSCHEDULED",
            };
        }

        var tx = await SendAsync(HttpMethod.Post, "/public/v2/transactions", body, ct)
                 ?? throw new InvalidOperationException("BML returned no transaction.");
        var txId = Str(tx, "id") ?? throw new InvalidOperationException("BML's transaction has no id.");
        var url = Str(tx, "url") ?? Str(tx, "shortUrl")
                  ?? throw new InvalidOperationException("BML's transaction has no payment page.");
        return new CheckoutSessionResult(txId, url);
    }

    public async Task<PaymentWebhookResult> GetStatusAsync(string providerSessionId, CancellationToken ct)
    {
        var tx = await GetTransactionAsync(providerSessionId, ct);
        return tx is null
            ? new PaymentWebhookResult(WebhookEventKind.Ignored, providerSessionId, null, null, null)
            : FromTransaction(tx);
    }

    /// <summary>BML has no refund API: refunds are requested in the merchant dashboard and reviewed by BML.</summary>
    public Task<RefundResult> RefundAsync(RefundRequest request, CancellationToken ct) =>
        Task.FromResult(new RefundResult(false, null, "Refunds are made in the BML merchant dashboard."));

    // ----- webhooks ---------------------------------------------------------------------------

    public async Task<PaymentWebhookResult> HandleWebhookAsync(
        string rawBody, IReadOnlyDictionary<string, string> headers, CancellationToken ct)
    {
        if (!SignatureIsValid(headers))
        {
            if (RequireSignature)
            {
                logger.LogWarning("BML webhook refused: missing or invalid signature.");
                return Unknown();
            }
            logger.LogWarning("BML webhook without a valid signature; checking it against BML's API.");
        }

        JsonNode? evt;
        try { evt = JsonNode.Parse(rawBody); }
        catch (JsonException) { return Unknown(); }
        if (evt is null) return Unknown();

        var type = Str(evt, "eventType");
        var txId = Str(evt, "transactionId");
        switch (type)
        {
            case "NOTIFY_TRANSACTION_CHANGE" when txId is not null:
                // The body's state is a claim; BML's API is the answer.
                return await GetStatusAsync(txId, ct);
            case "NOTIFY_TOKENISATION_STATUS":
                // A card saved (or not). Nothing to record: renewals ask BML for the card when they need it.
                logger.LogInformation("BML tokenisation {Status} for customer {Customer}.",
                    Str(evt, "tokenisationStatus"), Str(evt, "customerId"));
                return new PaymentWebhookResult(WebhookEventKind.Ignored, txId, null, null, null);
            default:
                logger.LogInformation("BML webhook {Type} ignored.", type);
                return new PaymentWebhookResult(WebhookEventKind.Ignored, txId, null, null, null);
        }
    }

    /// <summary>
    /// BML's scheme: <c>X-Signature</c> is the hex SHA-256 of <c>X-Signature-Nonce</c> +
    /// <c>X-Signature-Timestamp</c> + the API key. Compared in constant time.
    /// </summary>
    internal bool SignatureIsValid(IReadOnlyDictionary<string, string> headers)
    {
        if (!headers.TryGetValue("X-Signature-Nonce", out var nonce)
            || !headers.TryGetValue("X-Signature-Timestamp", out var timestamp)
            || !headers.TryGetValue("X-Signature", out var signature)
            || string.IsNullOrEmpty(nonce) || string.IsNullOrEmpty(timestamp) || string.IsNullOrEmpty(signature))
            return false;
        var expected = Sign(nonce, timestamp, ApiKey);
        return CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(signature.Trim().ToLowerInvariant()));
    }

    internal static string Sign(string nonce, string timestamp, string apiKey) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(nonce + timestamp + apiKey)));

    // ----- saved cards --------------------------------------------------------------------------

    public async Task<string> CreateCustomerAsync(string name, string email, CancellationToken ct)
    {
        var customer = await SendAsync(HttpMethod.Post, "/public-customers",
            new JsonObject { ["name"] = Trim(name, 100), ["email"] = email }, ct);
        return Str(customer, "id") ?? throw new InvalidOperationException("BML returned no customer id.");
    }

    public async Task<bool> HasSavedCardAsync(string customerId, CancellationToken ct) =>
        await SavedCardTokenAsync(customerId, ct) is not null;

    /// <summary>
    /// The customer's card to charge: the one BML marks as default, else the newest live card. BML's API
    /// reference requires the token's id on a charge (the guide calls it optional), so it's always sent.
    /// </summary>
    private async Task<string?> SavedCardTokenAsync(string customerId, CancellationToken ct)
    {
        var list = await SendAsync(HttpMethod.Get, $"/public-customers/{Uri.EscapeDataString(customerId)}/tokens", null, ct);
        if (list?["items"] is not JsonArray items) return null;
        var live = items.OfType<JsonNode>()
            .Where(t => t["deleted"] is not JsonValue d || !d.TryGetValue<bool>(out var gone) || !gone)
            .Where(t => string.Equals(Str(t, "tokenType") ?? "CARD", "CARD", StringComparison.OrdinalIgnoreCase))
            .ToList();
        var chosen = live.FirstOrDefault(t => t["defaultToken"] is JsonValue v && v.TryGetValue<bool>(out var isDefault) && isDefault)
                     ?? live.LastOrDefault();
        return chosen is null ? null : Str(chosen, "id") ?? Str(chosen, "_id");
    }

    public async Task<PaymentWebhookResult> ChargeSavedCardAsync(SavedCardCharge charge, CancellationToken ct)
    {
        var tokenId = await SavedCardTokenAsync(charge.CustomerId, ct);
        if (tokenId is null)
            return new PaymentWebhookResult(WebhookEventKind.PaymentFailed, null, null, null, null);

        // A merchant-initiated charge: a transaction for the customer, then "charge" with their default card.
        var tx = await SendAsync(HttpMethod.Post, "/public/v2/transactions", new JsonObject
        {
            ["amount"] = ToCents(charge.Amount),
            ["currency"] = charge.Currency,
            ["customerId"] = charge.CustomerId,
            ["localId"] = charge.PaymentId.ToString(),
            ["customerReference"] = Trim(charge.Description, 140),
            ["webhook"] = WebhookUrl,
            ["tokenizationDetails"] = new JsonObject
            {
                ["tokenize"] = false,
                ["paymentType"] = "UNSCHEDULED",
                ["recurringFrequency"] = "UNSCHEDULED",
            },
        }, ct);
        var txId = Str(tx, "id") ?? throw new InvalidOperationException("BML returned no transaction.");

        var charged = await SendAsync(HttpMethod.Post, "/public-customers/charge",
            new JsonObject { ["customerId"] = charge.CustomerId, ["transactionId"] = txId, ["tokenId"] = tokenId }, ct);

        // The charge's answer says what happened; if it can't be read, the transaction is asked.
        var result = charged is not null && Str(charged, "state") is not null
            ? FromTransaction(charged)
            : await GetStatusAsync(txId, ct);
        return result with { ProviderSessionId = txId, NextActionUrl = Str(charged, "nextAction") };
    }

    // ----- the API --------------------------------------------------------------------------------

    /// <summary>
    /// The transaction, from BML. The docs give two paths for it: <c>/public/transactions/{id}</c> in the
    /// API reference (asked first: it's the one BML's own mock server answers) and
    /// <c>/public/v2/transactions/{id}</c> in the guides, tried when the first isn't there. Null when
    /// neither knows it.
    /// </summary>
    private async Task<JsonNode?> GetTransactionAsync(string id, CancellationToken ct)
    {
        var escaped = Uri.EscapeDataString(id);
        foreach (var path in new[] { $"/public/transactions/{escaped}", $"/public/v2/transactions/{escaped}" })
        {
            using var req = Request(HttpMethod.Get, path, null);
            using var res = await Client().SendAsync(req, ct);
            // "Not this path" (not found, not allowed, or a route that doesn't take this): try the other.
            if (res.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.MethodNotAllowed
                or HttpStatusCode.UnprocessableEntity or HttpStatusCode.BadRequest) continue;
            await EnsureOkAsync(res, path, ct);
            return await res.Content.ReadFromJsonAsync<JsonNode>(Json, ct);
        }
        logger.LogWarning("BML has no transaction {TransactionId}.", id);
        return null;
    }

    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct)
    {
        using var req = Request(method, path, body);
        using var res = await Client().SendAsync(req, ct);
        await EnsureOkAsync(res, path, ct);
        if (res.StatusCode == HttpStatusCode.NoContent) return null;
        var text = await res.Content.ReadAsStringAsync(ct);
        return string.IsNullOrWhiteSpace(text) ? null : JsonNode.Parse(text);
    }

    private HttpRequestMessage Request(HttpMethod method, string path, JsonNode? body)
    {
        // Relative to the base address, never from its root: a leading "/" would drop any path the base
        // has (BML's mock server lives under one), so it goes, and the base always ends in "/".
        var req = new HttpRequestMessage(method, path.TrimStart('/'));
        // BML takes the key as it is, with no "Bearer" in front.
        req.Headers.TryAddWithoutValidation("Authorization", ApiKey);
        req.Headers.Accept.ParseAdd("application/json");
        if (body is not null) req.Content = JsonContent.Create(body, options: Json);
        return req;
    }

    private HttpClient Client() => http.CreateClient(ClientName);

    private async Task EnsureOkAsync(HttpResponseMessage res, string path, CancellationToken ct)
    {
        if (res.IsSuccessStatusCode) return;
        var text = await res.Content.ReadAsStringAsync(ct);
        // BML's error body is { message, code, extraInfo }: safe to log, and nothing of ours is in it.
        logger.LogError("BML {Path} answered {Status}: {Body}", path, (int)res.StatusCode, Trim(text, 500));
        throw new HttpRequestException($"BML {path} answered {(int)res.StatusCode}.", null, res.StatusCode);
    }

    // ----- reading BML's transaction -----------------------------------------------------------

    /// <summary>
    /// A BML transaction as a result: CONFIRMED is paid; CANCELLED and FAILED are over; everything else
    /// (INITIATED, QR_CODE_GENERATED, AUTHORIZED, …) is still going, so there's nothing to record yet.
    /// A refund leaves the original CONFIRMED and is a transaction of its own, so it never undoes a payment here.
    /// </summary>
    internal static PaymentWebhookResult FromTransaction(JsonNode tx)
    {
        var state = Str(tx, "state")?.ToUpperInvariant();
        var kind = state switch
        {
            "CONFIRMED" => WebhookEventKind.PaymentSucceeded,
            "CANCELLED" or "FAILED" => WebhookEventKind.PaymentFailed,
            _ => WebhookEventKind.Ignored,
        };
        var id = Str(tx, "id");
        var cents = tx["amount"] is JsonValue a && a.TryGetValue<decimal>(out var amt) ? amt : (decimal?)null;
        return new PaymentWebhookResult(kind, id, id, null, id is null ? null : $"bml_{id}_{state}")
        {
            Amount = cents / 100m,
            Currency = Str(tx, "currency"),
            LocalId = Str(tx, "localId"),
        };
    }

    /// <summary>Rufiyaa to laari: BML takes whole cents.</summary>
    internal static long ToCents(decimal amount) => (long)Math.Round(amount * 100m, MidpointRounding.AwayFromZero);

    private static string? Str(JsonNode? node, string name) =>
        node?[name] is JsonValue v && v.TryGetValue<string>(out var s) && !string.IsNullOrEmpty(s) ? s : null;

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max];

    private static PaymentWebhookResult Unknown() => new(WebhookEventKind.Unknown, null, null, null, null);
}
