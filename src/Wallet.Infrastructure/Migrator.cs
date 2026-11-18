using System.Reflection;
using Dapper;
using Npgsql;

namespace Wallet.Infrastructure;

/// <summary>套用內嵌的 db/migrations/*.sql（依檔名排序），以 schema_migrations 追蹤，並用 advisory lock 避免多實例同時執行。</summary>
public static class Migrator
{
    private const long LockKey = 7_261_001;

    public static async Task ApplyAsync(string connectionString, CancellationToken ct = default)
    {
        var asm = typeof(Migrator).Assembly;
        var scripts = asm.GetManifestResourceNames()
            .Where(n => n.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        await using var conn = new NpgsqlConnection(connectionString);
        await conn.OpenAsync(ct);
        await conn.ExecuteAsync("SELECT pg_advisory_lock(@k)", new { k = LockKey });
        try
        {
            await conn.ExecuteAsync(
                "CREATE TABLE IF NOT EXISTS schema_migrations (name text PRIMARY KEY, applied_at timestamptz NOT NULL DEFAULT now())");
            var applied = (await conn.QueryAsync<string>("SELECT name FROM schema_migrations")).ToHashSet();
            foreach (var res in scripts)
            {
                var name = res[(res.LastIndexOf("migrations.", StringComparison.Ordinal) + "migrations.".Length)..];
                if (applied.Contains(name)) continue;
                await using var s = asm.GetManifestResourceStream(res)!;
                using var r = new StreamReader(s);
                var sql = await r.ReadToEndAsync(ct);
                await using var tx = await conn.BeginTransactionAsync(ct);
                await conn.ExecuteAsync(sql, transaction: tx);
                await conn.ExecuteAsync("INSERT INTO schema_migrations(name) VALUES (@name)", new { name }, tx);
                await tx.CommitAsync(ct);
            }
        }
        finally
        {
            await conn.ExecuteAsync("SELECT pg_advisory_unlock(@k)", new { k = LockKey });
        }
    }
}
