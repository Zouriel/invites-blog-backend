using System.Text.Json;
using ClosedXML.Excel;
using InvitesBlog.Application.Phones;
using InvitesBlog.Domain.Entities;

namespace InvitesBlog.Application.Guests;

public sealed record ParsedGuest(
    string? Email,
    string? PhoneE164,
    string? PhoneRaw,
    string Name,
    string? Role,
    string Gender,
    string MetadataJson,
    IReadOnlyList<string>? Roles = null);

public sealed record GuestUploadError(int Row, string Field, string Message);

public sealed record GuestUploadResult(
    int TotalRows,
    int ValidRows,
    int InvalidRows,
    int Duplicates,
    int MissingPhone,
    int MissingEmail,
    IReadOnlyDictionary<string, int> RoleDistribution,
    IReadOnlyDictionary<string, int> GenderDistribution,
    IReadOnlyList<string> Warnings,
    IReadOnlyList<GuestUploadError> Errors,
    IReadOnlyList<ParsedGuest> ValidGuests,
    bool FileRejected = false,
    string? FileRejectionReason = null,
    int RoleErrors = 0)
{
    /// <summary>§4.4.6, plus: nobody left without a role, since roles are required when a campaign has them.</summary>
    public bool CanContinue => !FileRejected && ValidRows >= 1 && RoleErrors == 0;
}

/// <summary>
/// Parses an uploaded guest Excel file per §4.4: header mapping, E.164 normalization with a
/// default country, row-level validation, duplicate detection (E.164 / lowercased email),
/// role/gender distribution, and a downloadable error report feed. Merged cells reject the
/// whole file with a clear message (§4.4.5).
/// </summary>
public sealed class GuestUploadParser
{
    private readonly PhoneNormalizer _phones;
    private static readonly HashSet<string> KnownGenders = new(StringComparer.OrdinalIgnoreCase)
        { "male", "female", "neutral", "unspecified" };

    public GuestUploadParser(PhoneNormalizer phones) => _phones = phones;

    /// <param name="knownRoles">
    /// The campaign's roles. When given, every row must name at least one of them and nothing else,
    /// because an invitation with roles personalises on them and a guest without one gets a
    /// half-built invitation. Names are matched ignoring case and stored as the campaign spells them.
    /// Null or empty skips the check (a design the customer uploaded has no roles).
    /// </param>
    public GuestUploadResult Parse(
        Stream excelStream, string defaultCountry = "MV", IReadOnlyCollection<string>? knownRoles = null)
    {
        XLWorkbook workbook;
        try
        {
            workbook = new XLWorkbook(excelStream);
        }
        catch (Exception)
        {
            return Rejected("The file could not be read. Please upload a valid .xlsx file.");
        }

        using (workbook)
        {
            var sheet = workbook.Worksheets.FirstOrDefault();
            if (sheet is null)
                return Rejected("The workbook has no sheets.");

            if (sheet.MergedRanges.Any())
                return Rejected("The file contains merged cells. Please unmerge all cells and re-upload. (§4.4.5)");

            var range = sheet.RangeUsed();
            if (range is null)
                return Rejected("The sheet is empty.");

            var rows = range.RowsUsed().ToList();
            if (rows.Count < 2)
                return Rejected("The file has no data rows below the header.");

            // Map headers (first row).
            var headerRow = rows[0];
            var headers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var customColumns = new Dictionary<int, string>();
            var col = 0;
            foreach (var cell in headerRow.Cells())
            {
                col++;
                var name = cell.GetString().Trim();
                if (string.IsNullOrEmpty(name)) continue;
                headers[name] = col;
                if (!IsKnownHeader(name)) customColumns[col] = name;
            }

            int ColOf(string h) => headers.TryGetValue(h, out var c) ? c : -1;
            var emailCol = ColOf("email");
            var phoneCol = ColOf("phone");
            var nameCol = ColOf("name");
            var roleCol = ColOf("role");
            var genderCol = ColOf("gender");

            var valid = new List<ParsedGuest>();
            var errors = new List<GuestUploadError>();
            var warnings = new List<string>();
            var roleDist = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var genderDist = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            var seenEmails = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenPhones = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            int total = 0, invalid = 0, duplicates = 0, missingPhone = 0, missingEmail = 0, sciNoteCount = 0, missingRoles = 0;

            for (var i = 1; i < rows.Count; i++)
            {
                var row = rows[i];
                var excelRowNum = row.RowNumber();

                var email = Read(row, emailCol)?.Trim();
                var phoneRaw = Read(row, phoneCol)?.Trim();
                var name = Read(row, nameCol)?.Trim();
                var role = Read(row, roleCol)?.Trim();
                var gender = Read(row, genderCol)?.Trim();

                // Empty rows are ignored (§4.4.5).
                if (string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(phoneRaw) &&
                    string.IsNullOrWhiteSpace(name))
                    continue;

                total++;

                // Phone normalization.
                string? phoneE164 = null;
                if (!string.IsNullOrWhiteSpace(phoneRaw))
                {
                    var pr = _phones.Normalize(phoneRaw, defaultCountry);
                    if (pr.Outcome == PhoneNormalizationOutcome.Impossible)
                    {
                        errors.Add(new GuestUploadError(excelRowNum, "phone", pr.Warning ?? "Invalid phone."));
                        // keep phoneRaw as-is for the downloadable error report
                    }
                    else
                    {
                        phoneE164 = pr.E164;
                        if (pr.Warning is not null)
                        {
                            if (pr.Warning.Contains("scientific", StringComparison.OrdinalIgnoreCase)) sciNoteCount++;
                            else warnings.Add($"Row {excelRowNum}: {pr.Warning}");
                        }
                    }
                }

                // Email validation.
                if (!string.IsNullOrWhiteSpace(email) && !LooksLikeEmail(email))
                {
                    errors.Add(new GuestUploadError(excelRowNum, "email", "Invalid email address."));
                    email = null;
                }

                // At least one of email/phone required (§4.4.3).
                if (string.IsNullOrWhiteSpace(email) && string.IsNullOrWhiteSpace(phoneE164))
                {
                    errors.Add(new GuestUploadError(excelRowNum, "contact",
                        "Row rejected: at least one of email or phone is required."));
                    invalid++;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(phoneE164)) missingPhone++;
                if (string.IsNullOrWhiteSpace(email)) missingEmail++;

                // Duplicate detection (E.164 / lowercased email).
                var isDup = false;
                if (!string.IsNullOrWhiteSpace(email) && !seenEmails.Add(email)) isDup = true;
                if (!string.IsNullOrWhiteSpace(phoneE164) && !seenPhones.Add(phoneE164)) isDup = true;
                if (isDup)
                {
                    duplicates++;
                    warnings.Add($"Row {excelRowNum}: duplicate contact merged.");
                    continue;
                }

                // Fallbacks / normalization of role & gender.
                var finalName = string.IsNullOrWhiteSpace(name) ? "Guest" : name;
                var finalGender = string.IsNullOrWhiteSpace(gender) ? "unspecified" : gender.ToLowerInvariant();
                if (!KnownGenders.Contains(finalGender)) finalGender = "unspecified";
                // A guest can hold several roles, written in one cell and separated by ; , or |.
                // Kept as typed rather than lowercased, so they match the names on the Roles step.
                var roleList = Guest.NormalizeRoles((role ?? string.Empty).Split(RoleSeparators));

                if (knownRoles is { Count: > 0 })
                {
                    if (roleList.Count == 0)
                    {
                        errors.Add(new GuestUploadError(excelRowNum, "role",
                            "No role. Every guest needs at least one."));
                        invalid++;
                        missingRoles++;
                        continue;
                    }

                    var unknown = roleList.FirstOrDefault(r =>
                        !knownRoles.Any(k => string.Equals(k, r, StringComparison.OrdinalIgnoreCase)));
                    if (unknown is not null)
                    {
                        errors.Add(new GuestUploadError(excelRowNum, "role",
                            $"\"{unknown}\" isn't one of this invitation's roles."));
                        invalid++;
                        missingRoles++;
                        continue;
                    }

                    roleList = roleList
                        .Select(r => knownRoles.First(k => string.Equals(k, r, StringComparison.OrdinalIgnoreCase)))
                        .ToList();
                }

                var finalRole = roleList.FirstOrDefault();

                Increment(genderDist, finalGender);
                if (roleList.Count == 0) Increment(roleDist, "(none)");
                foreach (var r in roleList) Increment(roleDist, r);

                // Custom columns → metadata.
                var metadata = new Dictionary<string, string>();
                foreach (var (cIdx, header) in customColumns)
                {
                    var val = Read(row, cIdx)?.Trim();
                    if (!string.IsNullOrEmpty(val)) metadata[header] = val;
                }

                valid.Add(new ParsedGuest(
                    Email: string.IsNullOrWhiteSpace(email) ? null : email.ToLowerInvariant(),
                    PhoneE164: phoneE164,
                    PhoneRaw: phoneRaw,
                    Name: finalName,
                    Role: finalRole,
                    Gender: finalGender,
                    MetadataJson: JsonSerializer.Serialize(metadata),
                    Roles: roleList));
            }

            if (sciNoteCount > 0)
                warnings.Insert(0, $"{sciNoteCount} phone number(s) may have been stored as scientific notation and were recovered.");

            return new GuestUploadResult(
                TotalRows: total,
                ValidRows: valid.Count,
                InvalidRows: invalid,
                Duplicates: duplicates,
                MissingPhone: missingPhone,
                MissingEmail: missingEmail,
                RoleDistribution: roleDist,
                GenderDistribution: genderDist,
                Warnings: warnings,
                Errors: errors,
                ValidGuests: valid,
                RoleErrors: missingRoles);
        }
    }

    private static readonly char[] RoleSeparators = { ';', ',', '|' };

    private static bool IsKnownHeader(string h) =>
        h.Equals("email", StringComparison.OrdinalIgnoreCase) ||
        h.Equals("phone", StringComparison.OrdinalIgnoreCase) ||
        h.Equals("name", StringComparison.OrdinalIgnoreCase) ||
        h.Equals("role", StringComparison.OrdinalIgnoreCase) ||
        h.Equals("gender", StringComparison.OrdinalIgnoreCase);

    private static string? Read(IXLRangeRow row, int col) =>
        col < 1 ? null : row.Cell(col).GetString();

    private static bool LooksLikeEmail(string s)
    {
        var at = s.IndexOf('@');
        return at > 0 && at < s.Length - 1 && s.IndexOf('.', at) > at && !s.Contains(' ');
    }

    private static void Increment(Dictionary<string, int> d, string key) =>
        d[key] = d.TryGetValue(key, out var c) ? c + 1 : 1;

    private static GuestUploadResult Rejected(string reason) => new(
        0, 0, 0, 0, 0, 0,
        new Dictionary<string, int>(), new Dictionary<string, int>(),
        Array.Empty<string>(), Array.Empty<GuestUploadError>(), Array.Empty<ParsedGuest>(),
        FileRejected: true, FileRejectionReason: reason);
}
