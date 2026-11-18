using System.Reflection;
using Dapper;
using Npgsql;

namespace Slot.GameServer;

/// <summary>套用內嵌的 db/slot/*.sql 到 <c>slot</c> schema；用自己的 slot.schema_migrations 追蹤，與錢包的 migration 互不影響。</summary>
public static class SlotMigrator
{
    private const long LockKey = 7_261_002; // 與錢包的 7_261_001 不同

    public static async Task ApplyAsync(string connectionString, CancellationToken ct = default)
    {
        var asm = typeof(SlotMigrator).Assembly;
        var scripts = asm.GetManifestResourceNames()
            .Where(n => n.StartsWith("slotmigrations.", StringComparison.Ordinal) && n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal).ToList();

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync("SELECT pg_advisory_lock(@k)", new { k = LockKey });
        try
        {
            await conn.ExecuteAsync("CREATE SCHEMA IF NOT EXISTS slot");
            await conn.ExecuteAsync("CREATE TABLE IF NOT EXISTS slot.schema_migrations (name text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())");
            var applied = (await conn.QueryAsync<string>("SELECT name FROM slot.schema_migrations")).ToHashSet();
            foreach (var res in scripts)
            {
                var name = res["slotmigrations.".Length..];
                if (applied.Contains(name)) continue;
                await using var s = asm.GetManifestResourceStream(res)!;
                using var r = new StreamReader(s);
                var sql = await r.ReadToEndAsync(ct);
                await using var tx = await conn.BeginTransactionAsync(ct);
                await conn.ExecuteAsync(sql, transaction: tx);
                await conn.ExecuteAsync("INSERT INTO slot.schema_migrations(name) VALUES (@name)", new { name }, tx);
                await tx.CommitAsync(ct);
            }
        }
        finally
        {
            await conn.ExecuteAsync("SELECT pg_advisory_unlock(@k)", new { k = LockKey });
        }
    }
}
