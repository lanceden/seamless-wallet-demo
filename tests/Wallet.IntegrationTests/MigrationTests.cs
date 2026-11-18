using Dapper;
using Npgsql;
using Wallet.Infrastructure;

namespace Wallet.IntegrationTests;

public class MigrationTests : IClassFixture<PostgresFixture>
{
    private readonly PostgresFixture _pg;
    public MigrationTests(PostgresFixture pg) => _pg = pg;

    [Fact]
    public async Task Migration_creates_all_tables_and_seeds_system_provider()
    {
        await using var conn = new NpgsqlConnection(_pg.ConnectionString);
        var tables = (await conn.QueryAsync<string>(
            "SELECT table_name FROM information_schema.tables WHERE table_schema='public'")).ToHashSet();
        foreach (var t in new[] { "providers", "players", "wallets", "wallet_transactions", "bets",
                     "cancelled_markers", "reconciliation_runs", "reconciliation_diffs" })
            Assert.Contains(t, tables);

        var system = await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM providers WHERE code='SYSTEM'");
        Assert.Equal(1, system);
    }

    [Fact]
    public async Task Migration_is_idempotent_when_applied_twice()
    {
        await Migrator.ApplyAsync(_pg.ConnectionString);
        await using var conn = new NpgsqlConnection(_pg.ConnectionString);
        Assert.Equal(1, await conn.ExecuteScalarAsync<int>("SELECT count(*) FROM providers WHERE code='SYSTEM'"));
    }

    [Fact]
    public async Task Wallet_balance_cannot_go_negative_at_db_level()
    {
        await using var conn = new NpgsqlConnection(_pg.ConnectionString);
        var pid = await conn.ExecuteScalarAsync<long>("INSERT INTO players(external_id) VALUES ('mig_neg') RETURNING id");
        var ex = await Assert.ThrowsAsync<PostgresException>(() =>
            conn.ExecuteAsync("INSERT INTO wallets(player_id,balance) VALUES (@pid,-1)", new { pid }));
        Assert.Equal("23514", ex.SqlState);
    }
}
