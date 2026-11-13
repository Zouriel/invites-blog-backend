using InvitesBlog.Application.Plans;
using InvitesBlog.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Xunit;

namespace InvitesBlog.Tests.Integration;

/// <summary>
/// The pricing migration (2026-11-13) on a database with accounts in it, as production has: Premium subscribers
/// keep the AI designer until their period ends, not renewing; nobody else gets it; a saved price list moves to
/// the new prices; every account has a list of add-ons afterwards (the column is NOT NULL).
/// </summary>
public sealed class PricingMigrationTests
{
    private static readonly Guid PremiumRunning = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid PremiumOpen = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid PremiumEnded = Guid.Parse("33333333-3333-4333-8333-333333333333");
    private static readonly Guid Studio = Guid.Parse("44444444-4444-4444-8444-444444444444");
    private static readonly Guid Free = Guid.Parse("55555555-5555-4555-8555-555555555555");

    [PostgresFact]
    public async Task Premium_keeps_the_ai_designer_to_its_period_end_and_a_saved_price_list_moves_to_77()
    {
        var server = Environment.GetEnvironmentVariable(PostgresApiFixture.ServerVariable)!;
        var name = $"ib_price_{Guid.NewGuid():N}";
        await using (var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(server) { Database = "postgres" }.ToString()))
        {
            await admin.OpenAsync();
            await new NpgsqlCommand($"CREATE DATABASE {name}", admin).ExecuteNonQueryAsync();
        }
        var conn = new NpgsqlConnectionStringBuilder(server) { Database = name }.ToString();
        var periodEnd = new DateTimeOffset(2099, 1, 2, 3, 4, 5, TimeSpan.Zero);
        try
        {
            await using (var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(conn).Options))
            {
                var migrator = db.GetService<IMigrator>();
                var all = db.Database.GetMigrations().ToList();
                var pricing = all.Single(m => m.EndsWith("_AddPlanAddOns"));
                await migrator.MigrateAsync(all[all.IndexOf(pricing) - 1]);

                await InsertUserAsync(db, PremiumRunning, 2, "'2099-01-02T03:04:05Z'");
                await InsertUserAsync(db, PremiumOpen, 2, "NULL");
                await InsertUserAsync(db, PremiumEnded, 2, "'2001-01-01T00:00:00Z'");
                await InsertUserAsync(db, Studio, 3, "'2099-01-01T00:00:00Z'");
                await InsertUserAsync(db, Free, 0, "NULL");
                await db.Database.ExecuteSqlRawAsync("""
                    INSERT INTO app_settings (key, value_json, updated_at)
                    VALUES ('prices', '{{"GoldPass":249,"PlatinumPass":499,"KeepPhotosYearly":150,"PremiumMonthly":200,"VenueDiscountPercent":0,"SendingPerBlock":50,"MvrPerUsd":15.42,"VenueMonthly":775}}', now());
                    """);

                await migrator.MigrateAsync(pricing);
            }

            await using (var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(conn).Options))
            {
                var users = await db.Users.AsNoTracking().ToDictionaryAsync(u => u.Id);

                var running = Assert.Single(users[PremiumRunning].AddOns);
                Assert.Equal(("designer-ai", false, "carried-over"), (running.Key, running.Renews, running.Source));
                Assert.Equal(periodEnd, running.EndsAt);

                var open = Assert.Single(users[PremiumOpen].AddOns);
                Assert.Null(open.EndsAt);

                Assert.Empty(users[PremiumEnded].AddOns);
                Assert.Empty(users[Studio].AddOns);
                Assert.Empty(users[Free].AddOns);

                PriceBook.ClearCache();
                var prices = await new PriceBook(
                    new Infrastructure.Repositories.BaseRepository<Domain.Entities.AppSetting>(db),
                    new Infrastructure.Repositories.BaseRepository<Domain.Entities.AuditLog>(db),
                    new UnitOfWorkStub()).CurrentAsync();
                Assert.Equal((77m, 77m, 249m), (prices.PremiumMonthly, prices.DesignerAiMonthly, prices.GoldPass));
                PriceBook.ClearCache();
            }
        }
        finally
        {
            NpgsqlConnection.ClearAllPools();
            await using var admin = new NpgsqlConnection(new NpgsqlConnectionStringBuilder(server) { Database = "postgres" }.ToString());
            await admin.OpenAsync();
            await new NpgsqlCommand($"DROP DATABASE IF EXISTS {name} WITH (FORCE)", admin).ExecuteNonQueryAsync();
        }
    }

    /// <summary>
    /// An account row as the schema before the migration takes it: every NOT NULL column without a default gets a
    /// plain value of its type, so the test doesn't break each time the users table grows a column.
    /// </summary>
    private static async Task InsertUserAsync(AppDbContext db, Guid id, int tier, string endsAtSql)
    {
        var required = new List<(string Column, string Type)>();
        var c = (NpgsqlConnection)db.Database.GetDbConnection();
        if (c.State != System.Data.ConnectionState.Open) await c.OpenAsync();
        await using (var cmd = new NpgsqlCommand("""
            SELECT column_name, data_type FROM information_schema.columns
            WHERE table_name = 'users' AND is_nullable = 'NO' AND column_default IS NULL
            """, c))
        await using (var r = await cmd.ExecuteReaderAsync())
            while (await r.ReadAsync()) required.Add((r.GetString(0), r.GetString(1)));

        var columns = new List<string> { "id", "subscription_tier", "subscription_ends_at" };
        var values = new List<string> { $"'{id}'", tier.ToString(), endsAtSql };
        foreach (var (column, type) in required.Where(x => !columns.Contains(x.Column)))
        {
            columns.Add(column);
            values.Add(type switch
            {
                "boolean" => column == "is_active" ? "true" : "false",
                "integer" or "bigint" or "smallint" or "numeric" => "0",
                "timestamp with time zone" => "now()",
                "uuid" => $"'{Guid.NewGuid()}'",
                "ARRAY" => "'{}'",
                "jsonb" => "'{}'::jsonb",
                _ => $"'u-{id:N}'",
            });
        }
        await using var insert = new NpgsqlCommand($"INSERT INTO users ({string.Join(',', columns)}) VALUES ({string.Join(',', values)})", c);
        await insert.ExecuteNonQueryAsync();
    }

    private sealed class UnitOfWorkStub : Application.Abstractions.Persistence.IUnitOfWork
    {
        public Task<int> SaveChangesAsync(CancellationToken ct = default) => Task.FromResult(0);
    }
}
