using PhoneNumbers;

namespace InvitesBlog.Application.Phones;

public enum PhoneNormalizationOutcome
{
    Valid,
    Unusual,      // libphonenumber "valid but unusual" — keep, warn (§4.4.4)
    Impossible,   // reject the row
    Empty
}

public sealed record PhoneNormalizationResult(
    PhoneNormalizationOutcome Outcome,
    string? E164,
    string Raw,
    string? Warning)
{
    public bool IsUsable => Outcome is PhoneNormalizationOutcome.Valid or PhoneNormalizationOutcome.Unusual;
}

/// <summary>
/// Normalizes phone numbers to E.164 (§4.4.4). Phone identity is load-bearing — inbox matching,
/// delivery, and dedupe all compare only <c>phone_e164</c>. Numbers starting with "+" or "00" keep
/// their own country code; a local-format number ("07700 900123") needs a region to be read in, and
/// there is NO default one: it comes from the caller (the country picked beside the number), else
/// the signed-in account's country, else the request's Cloudflare country (see
/// <see cref="NormalizeAsync"/>). Without any of them the number is refused with "Add the country
/// code". Scientific-notation values ("7.77e6") are recovered where possible.
/// </summary>
public sealed class PhoneNormalizer(IPhoneRegionSource? regions = null)
{
    private readonly PhoneNumberUtil _util = PhoneNumberUtil.GetInstance();

    /// <summary>What a number typed without its country code, and with no country to read it in, is told.</summary>
    public const string NeedsCountryCode = "Add the country code (for example +44 7700 900123).";

    /// <summary>True when <paramref name="country"/> is an ISO 3166-1 alpha-2 region libphonenumber knows.</summary>
    public static bool IsKnownRegion(string? country) =>
        !string.IsNullOrWhiteSpace(country)
        && PhoneNumberUtil.GetInstance().GetSupportedRegions().Contains(country.Trim().ToUpperInvariant());

    /// <summary>The region as libphonenumber spells it ("GB"), or null when it isn't a known one.</summary>
    public static string? Region(string? country) =>
        IsKnownRegion(country) ? country!.Trim().ToUpperInvariant() : null;

    /// <summary>
    /// Normalizes with the region chosen in order: <paramref name="explicitCountry"/> (sent by the
    /// client beside the number), the signed-in account's country, the request's country. Numbers
    /// with a leading + or 00 ignore all three.
    /// </summary>
    public async Task<PhoneNormalizationResult> NormalizeAsync(string? raw, string? explicitCountry, CancellationToken ct = default)
    {
        var region = Region(explicitCountry);
        if (region is null && NeedsRegion(raw)) region = await ResolveRegionAsync(null, ct);
        return Normalize(raw, region);
    }

    /// <summary>The region for a batch of numbers (an uploaded list): the caller's, else the account's, else the request's.</summary>
    public async Task<string?> ResolveRegionAsync(string? explicitCountry, CancellationToken ct = default) =>
        Region(explicitCountry) ?? (regions is null ? null : Region(await regions.DefaultRegionAsync(ct)));

    /// <summary>Whether a value can only be read with a region: anything not starting with + or 00.</summary>
    private static bool NeedsRegion(string? raw)
    {
        var t = raw?.Trim() ?? "";
        return t.Length > 0 && !t.StartsWith('+') && !t.StartsWith("00", StringComparison.Ordinal);
    }

    /// <param name="defaultCountry">The region to read a local-format number in; null for none.</param>
    public PhoneNormalizationResult Normalize(string? raw, string? defaultCountry)
    {
        var original = raw?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(original))
            return new PhoneNormalizationResult(PhoneNormalizationOutcome.Empty, null, original, null);

        string? warning = null;
        var candidate = original;

        // Recover scientific notation e.g. "9.607777777E9" that Excel produced (§4.4.5).
        if (candidate.Contains('e', StringComparison.OrdinalIgnoreCase) &&
            double.TryParse(candidate, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var asNumber))
        {
            candidate = asNumber.ToString("F0", System.Globalization.CultureInfo.InvariantCulture);
            warning = "Phone number may have been stored as scientific notation; recovered value.";
        }

        // "00" is the international prefix almost everywhere: the same as "+".
        if (candidate.StartsWith("00", StringComparison.Ordinal)) candidate = "+" + candidate[2..];

        try
        {
            var region = candidate.StartsWith('+') ? null : Region(defaultCountry);
            if (region is null && !candidate.StartsWith('+'))
                return new PhoneNormalizationResult(PhoneNormalizationOutcome.Impossible, null, original, NeedsCountryCode);
            var parsed = _util.Parse(candidate, region);

            if (!_util.IsPossibleNumber(parsed))
                return new PhoneNormalizationResult(PhoneNormalizationOutcome.Impossible, null, original,
                    "Impossible phone number.");

            var e164 = _util.Format(parsed, PhoneNumberFormat.E164);

            if (!_util.IsValidNumber(parsed))
                return new PhoneNormalizationResult(PhoneNormalizationOutcome.Unusual, e164, original,
                    warning ?? "Phone number is unusual but was kept.");

            return new PhoneNormalizationResult(PhoneNormalizationOutcome.Valid, e164, original, warning);
        }
        catch (NumberParseException)
        {
            return new PhoneNormalizationResult(PhoneNormalizationOutcome.Impossible, null, original,
                "Could not parse phone number.");
        }
    }
}
