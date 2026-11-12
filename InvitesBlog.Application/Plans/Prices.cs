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
/// <param name="VenueDiscountPercent">No longer applied anywhere (owner, 2026-11-12: venues pay normal pass
/// prices). Kept so a saved price list and the admin page still read and write the same shape.</param>
public sealed record Prices(
    decimal GoldPass,
    decimal PlatinumPass,
    decimal KeepPhotosYearly,
    decimal PremiumMonthly,
    int VenueDiscountPercent,
    decimal SendingPerBlock,
    decimal MvrPerUsd,
    decimal GoldExtension = PlanCatalog.GoldExtension,
    decimal PlatinumExtension = PlanCatalog.PlatinumExtension,
    decimal VenueMonthly = PlanCatalog.VenueMonthly)
{
    /// <summary>The prices in code, used until an admin saves others.</summary>
    public static Prices Defaults { get; } = new(
        PlanCatalog.GoldPassPrice, PlanCatalog.PlatinumPassPrice, PlanCatalog.KeepPhotosYearly,
        PlanCatalog.PremiumMonthly, PlanCatalog.VenuePassDiscountPercent,
        Pricing.PricingCalculator.PricePerBlock, PlanCatalog.MvrPerUsd,
        PlanCatalog.GoldExtension, PlanCatalog.PlatinumExtension, PlanCatalog.VenueMonthly);

    public decimal PassPrice(EventPassKind kind) => kind switch
    {
        EventPassKind.Gold => GoldPass,
        EventPassKind.Platinum => PlatinumPass,
        _ => 0m,
    };

    /// <summary>Another year of a pass, without invitations.</summary>
    public decimal ExtensionPrice(EventPassKind kind) => kind switch
    {
        EventPassKind.Gold => GoldExtension,
        EventPassKind.Platinum => PlatinumExtension,
        _ => 0m,
    };

    /// <summary>Why these prices can't be saved, or nothing when they can.</summary>
    public IReadOnlyList<string> Problems()
    {
        var problems = new List<string>();
        void Positive(decimal value, string name)
        {
            if (value <= 0) problems.Add($"{name} must be more than 0.");
            if (value > 1_000_000) problems.Add($"{name} is too large.");
        }
        Positive(GoldPass, "The Gold pass");
        Positive(PlatinumPass, "The Platinum pass");
        Positive(KeepPhotosYearly, "Keep your photos");
        Positive(PremiumMonthly, "Premium a month");
        Positive(VenueMonthly, "Venue a month");
        Positive(SendingPerBlock, "Emailed invitations");
        Positive(MvrPerUsd, "The dollar rate");
        Positive(GoldExtension, "Extending a Gold pass");
        Positive(PlatinumExtension, "Extending a Platinum pass");
        if (GoldExtension > GoldPass) problems.Add("Extending a Gold pass can't cost more than the pass.");
        if (PlatinumExtension > PlatinumPass) problems.Add("Extending a Platinum pass can't cost more than the pass.");
        if (VenueDiscountPercent is < 0 or > 90) problems.Add("The venue discount must be between 0 and 90%.");
        if (PlatinumPass < GoldPass) problems.Add("The Platinum pass can't cost less than the Gold pass.");
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
            // Saved before the Party and Wedding passes became Gold and Platinum: their prices carry
            // over, so a price an admin set doesn't reset. The new name wins once a save has written it.
            decimal Renamed(string name, string oldName, decimal fallback) =>
                D(name, D(oldName, D(char.ToLowerInvariant(oldName[0]) + oldName[1..], fallback)));
            var p = Prices.Defaults;
            var read = new Prices(
                Renamed(nameof(Prices.GoldPass), "PartyPass", p.GoldPass),
                Renamed(nameof(Prices.PlatinumPass), "WeddingPass", p.PlatinumPass),
                D(nameof(Prices.KeepPhotosYearly), p.KeepPhotosYearly),
                // Saved before Studio became Premium: its monthly price carries over.
                D(nameof(Prices.PremiumMonthly), D("StudioMonthly", p.PremiumMonthly)),
                (int)D(nameof(Prices.VenueDiscountPercent), p.VenueDiscountPercent),
                D(nameof(Prices.SendingPerBlock), p.SendingPerBlock),
                D(nameof(Prices.MvrPerUsd), p.MvrPerUsd),
                Renamed(nameof(Prices.GoldExtension), "PartyExtension", p.GoldExtension),
                Renamed(nameof(Prices.PlatinumExtension), "WeddingExtension", p.PlatinumExtension),
                D(nameof(Prices.VenueMonthly), p.VenueMonthly));
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
