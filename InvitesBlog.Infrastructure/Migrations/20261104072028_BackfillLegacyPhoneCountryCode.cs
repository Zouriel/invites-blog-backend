using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class BackfillLegacyPhoneCountryCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // One-off and idempotent. Phone numbers have always been stored in E.164 ("+9607771234")
            // by PhoneNormalizer, but until 2026-11 it read every number typed without a code as a
            // Maldives one, and the people who typed them were Maldivian. Any value still stored
            // WITHOUT a leading "+" is from then: give it +960, and only when its digits are a
            // Maldives number's shape (7 digits, or 960 + 7). Anything else is left exactly as it
            // is rather than guessed at. New input never gets a default country (PhoneNormalizer).
            // Unique columns are only changed where the result is free, so this can't fail startup.
            foreach (var (table, unique) in new[]
            {
                ("guests", (string?)null),
                ("inviters", null),
                ("campaign_celebrants", null),
                ("otp_challenges", null),
                ("users", "NOT EXISTS (SELECT 1 FROM users o WHERE o.phone_e164 = {0})"),
                ("verified_contact_links", "NOT EXISTS (SELECT 1 FROM verified_contact_links o WHERE o.email = t.email AND o.phone_e164 = {0})"),
            })
            {
                const string Digits = "regexp_replace(t.phone_e164, '[^0-9]', '', 'g')";
                foreach (var (shape, target) in new[]
                {
                    ("^960[0-9]{7}$", $"'+' || {Digits}"),
                    ("^[0-9]{7}$", $"'+960' || {Digits}"),
                })
                {
                    var free = unique is null ? "" : " AND " + string.Format(unique, target);
                    migrationBuilder.Sql(
                        $"UPDATE {table} AS t SET phone_e164 = {target} " +
                        $"WHERE t.phone_e164 IS NOT NULL AND t.phone_e164 <> '' AND left(t.phone_e164, 1) <> '+' " +
                        $"AND {Digits} ~ '{shape}'{free};");
                }
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Nothing to undo: the old values were not E.164 and nothing reads them that way.
        }
    }
}
