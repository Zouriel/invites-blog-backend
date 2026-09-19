using System.Text.Json;
using InvitesBlog.Application.Abstractions.Persistence;
using InvitesBlog.Application.Exceptions;
using InvitesBlog.Domain.Entities;
using InvitesBlog.Domain.Enums;
using Microsoft.EntityFrameworkCore;

namespace InvitesBlog.Application.Plans;

/// <summary>
/// What everything costs, in rufiyaa. Limits (space, albums, days, included emails) stay in
/// <see cref="PlanCatalog"/> because the code enforces them; prices are what an admin may change
/// without a release, so they live here and are read through <see cref="IPriceBook"/>.
/// </summary>
public sealed record Prices(
    decimal PartyPass,
    decimal WeddingPass,
    decimal KeepPhotosYearly,
    decimal StudioMonthly,
    decimal StudioYearly,
    decimal VenueMonthlyFrom,
    decimal SendingPerBlock,
    int StudioDiscountPercent,
    decimal MvrPerUsd)
{
    /// <summary>The prices in code, used until an admin saves others.</summary>
    public static Prices Defaults { get; } = new(
        PlanCatalog.PartyPassPrice, PlanCatalog.WeddingPassPrice, PlanCatalog.KeepPhotosYearly,
        PlanCatalog.StudioMonthly, PlanCatalog.StudioYearly, PlanCatalog.VenueMonthlyFrom,
        Pricing.PricingCalculator.PricePerBlock, PlanCatalog.StudioPassDiscountPercent, PlanCatalog.MvrPerUsd);

    public decimal PassPrice(EventPassKind kind) => kind switch
    {
        EventPassKind.Party => PartyPass,
        EventPassKind.Wedding => WeddingPass,
        _ => 0m,
    };

    /// <summary>What a Studio account pays for a pass to give a client.</summary>
    public decimal StudioPassPrice(EventPassKind kind) =>
        Math.Round(PassPrice(kind) * (100 - StudioDiscountPercent) / 100m, 0, MidpointRounding.AwayFromZero);

    /// <summary>Why these prices can't be saved, or nothing when they can.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        void Positive(decimal value, string name)
        {
            if (value <= 0) problems.Add($"{name} must be more than 0.");
            if (value > 1_000_000) problems.Add($"{name} is too large.");
        }
        Positive(PartyPass, "The Party pass");
        Positive(WeddingPass, "The Wedding pass");
        Positive(KeepPhotosYearly, "Keep your photos");
        Positive(StudioMonthly, "Studio a month");
        Positive(StudioYearly, "Studio a year");
        Positive(VenueMonthlyFrom, "Venue");
        Positive(SendingPerBlock, "Emailed invitations");
        Positive(MvrPerUsd, "The dollar rate");
        if (StudioDiscountPercent is < 0 or > 90) problems.Add("The Studio discount must be between 0 and 90%.");
        if (WeddingPass < PartyPass) problems.Add("The Wedding pass can't cost less than the Party pass.");
        if (StudioYearly < StudioMonthly) problems.Add("Studio a year can't cost less than a month.");
        return problems;
    }
}

public interface IPriceBook
{
    /// <summary>The prices in force: an admin's, or the defaults in code.</summary>
    Task<Prices> CurrentAsync(CancellationToken ct = default);

    /// <summary>Saves new prices. Anything charged from now on uses them; what was paid is unchanged.</summary>
    Task<Prices> SetAsync(Prices prices, Guid? actor, CancellationToken ct = default);

    /// <summary>Back to the prices in code.</summary>
    Task<Prices> ResetAsync(Guid? actor, CancellationToken ct = default);
}

public sealed class PriceBook(
    IRepository<AppSetting> settings,
    IRepository<AuditLog> auditLogs,
    IUnitOfWork uow) : IPriceBook
{
    public const string Key = "prices";

    // Read on every page that shows a price, changed a few times a year: cached for a minute, and
    // dropped the moment this process saves new ones.
    private static readonly TimeSpan Ttl = TimeSpan.FromMinutes(1);
    private static (Prices Value, DateTimeOffset At)? _cache;

    public async Task<Prices> CurrentAsync(CancellationToken ct = default)
    {
        if (_cache is { } c && DateTimeOffset.UtcNow - c.At < Ttl) return c.Value;
        var row = await settings.Query().FirstOrDefaultAsync(s => s.Key == Key, ct);
        var value = Read(row?.ValueJson);
        _cache = (value, DateTimeOffset.UtcNow);
        return value;
    }

    public async Task<Prices> SetAsync(Prices prices, Guid? actor, CancellationToken ct = default)
    {
        var problems = prices.Problems();
        if (problems.Count > 0) throw new BusinessRuleException(string.Join(" ", problems), "prices_invalid");

        var before = await CurrentAsync(ct);
        var now = DateTimeOffset.UtcNow;
        var row = await settings.Query(tracking: true).FirstOrDefaultAsync(s => s.Key == Key, ct);
        var json = JsonSerializer.Serialize(prices);
        if (row is null)
            await settings.AddAsync(new AppSetting { Key = Key, ValueJson = json, UpdatedAt = now, UpdatedByUserId = actor }, ct);
        else
        {
            row.ValueJson = json;
            row.UpdatedAt = now;
            row.UpdatedByUserId = actor;
            settings.Update(row);
        }

        await auditLogs.AddAsync(new AuditLog
        {
            Id = Guid.NewGuid(),
            Action = "prices.set",
            Actor = actor?.ToString() ?? "system",
            DataJson = JsonSerializer.Serialize(new { before, after = prices }),
            CreatedAt = now,
        }, ct);
        await uow.SaveChangesAsync(ct);
        _cache = (prices, now);
        return prices;
    }

    public Task<Prices> ResetAsync(Guid? actor, CancellationToken ct = default) => SetAsync(Prices.Defaults, actor, ct);

    /// <summary>A saved row, falling back field by field to the defaults — a price added later still has one.</summary>
    private static Prices Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return Prices.Defaults;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            decimal D(string name, decimal fallback) =>
                r.TryGetProperty(name, out var v) && v.TryGetDecimal(out var d) ? d : fallback;
            var p = Prices.Defaults;
            var read = new Prices(
                D(nameof(Prices.PartyPass), p.PartyPass),
                D(nameof(Prices.WeddingPass), p.WeddingPass),
                D(nameof(Prices.KeepPhotosYearly), p.KeepPhotosYearly),
                D(nameof(Prices.StudioMonthly), p.StudioMonthly),
                D(nameof(Prices.StudioYearly), p.StudioYearly),
                D(nameof(Prices.VenueMonthlyFrom), p.VenueMonthlyFrom),
                D(nameof(Prices.SendingPerBlock), p.SendingPerBlock),
                (int)D(nameof(Prices.StudioDiscountPercent), p.StudioDiscountPercent),
                D(nameof(Prices.MvrPerUsd), p.MvrPerUsd));
            return read.Problems().Count == 0 ? read : p;
        }
        catch (JsonException)
        {
            return Prices.Defaults;
        }
    }

    /// <summary>For tests: forget what was cached.</summary>
    public static void ClearCache() => _cache = null;
}
