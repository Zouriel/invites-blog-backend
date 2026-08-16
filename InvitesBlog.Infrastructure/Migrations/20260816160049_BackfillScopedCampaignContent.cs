using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InvitesBlog.Infrastructure.Migrations
{
    /// <summary>
    /// Data-only migration for the Phase 6 wizard. Two campaign JSON blobs change shape:
    /// <list type="bullet">
    /// <item><c>custom_content_json.fields</c> / <c>.imageSlots</c> go from a flat
    /// <c>{ path: value }</c> map to <c>{ path: { value, roles } }</c>, so a value can be scoped to
    /// specific roles. Existing values become <c>roles: []</c> — "applies to all roles" — which is
    /// exactly what they meant before scoping existed.</item>
    /// <item><c>theme_overrides_json</c> goes from a flat override map to
    /// <c>{ shared, roles }</c>, with everything already saved treated as shared.</item>
    /// </list>
    /// Both transforms are idempotent (they skip anything already in the new shape), and the renderer
    /// reads both shapes regardless — so this is a tidy-up, not a load-bearing step.
    /// </summary>
    public partial class BackfillScopedCampaignContent : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // fields / imageSlots: wrap each bare value as { value, roles: [] }, leaving anything
            // already wrapped untouched.
            foreach (var key in new[] { "fields", "imageSlots" })
            {
                migrationBuilder.Sql($@"
UPDATE campaigns c
SET custom_content_json = c.custom_content_json || jsonb_build_object('{key}', (
    SELECT jsonb_object_agg(
        e.k,
        CASE WHEN jsonb_typeof(e.v) = 'object' AND e.v ? 'value'
             THEN e.v
             ELSE jsonb_build_object('value', e.v, 'roles', '[]'::jsonb)
        END)
    FROM jsonb_each(c.custom_content_json -> '{key}') AS e(k, v)
))
WHERE jsonb_typeof(c.custom_content_json -> '{key}') = 'object'
  AND EXISTS (
      SELECT 1 FROM jsonb_each(c.custom_content_json -> '{key}') AS e(k, v)
      WHERE NOT (jsonb_typeof(e.v) = 'object' AND e.v ? 'value')
  );");
            }

            // theme overrides: everything saved so far applied to every role, so it becomes `shared`.
            migrationBuilder.Sql(@"
UPDATE campaigns
SET theme_overrides_json = jsonb_build_object('shared', theme_overrides_json, 'roles', '{}'::jsonb)
WHERE jsonb_typeof(theme_overrides_json) = 'object'
  AND NOT (theme_overrides_json ? 'shared')
  AND NOT (theme_overrides_json ? 'roles');");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Unwrap { value, roles } back to the bare value. Any role scoping the inviter set is lost
            // — the old shape simply cannot express it.
            foreach (var key in new[] { "fields", "imageSlots" })
            {
                migrationBuilder.Sql($@"
UPDATE campaigns c
SET custom_content_json = c.custom_content_json || jsonb_build_object('{key}', (
    SELECT jsonb_object_agg(
        e.k,
        CASE WHEN jsonb_typeof(e.v) = 'object' AND e.v ? 'value' THEN e.v -> 'value' ELSE e.v END)
    FROM jsonb_each(c.custom_content_json -> '{key}') AS e(k, v)
))
WHERE jsonb_typeof(c.custom_content_json -> '{key}') = 'object'
  AND c.custom_content_json -> '{key}' <> '{{}}'::jsonb;");
            }

            migrationBuilder.Sql(@"
UPDATE campaigns
SET theme_overrides_json = COALESCE(theme_overrides_json -> 'shared', '{}'::jsonb)
WHERE jsonb_typeof(theme_overrides_json) = 'object'
  AND theme_overrides_json ? 'shared';");
        }
    }
}
