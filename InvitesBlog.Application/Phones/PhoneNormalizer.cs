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
/// delivery, and dedupe all compare only <c>phone_e164</c>. A default country lets local-format
/// numbers ("7777777") normalize; numbers starting with "+" keep their own country code.
/// Scientific-notation values ("7.77e6") are recovered where possible.
/// </summary>
public sealed class PhoneNormalizer
{
    private readonly PhoneNumberUtil _util = PhoneNumberUtil.GetInstance();

    public PhoneNormalizationResult Normalize(string? raw, string defaultCountry = "MV")
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

        try
        {
            var region = candidate.StartsWith('+') ? null : defaultCountry;
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
