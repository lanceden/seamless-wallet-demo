using Dapper;
using Microsoft.Extensions.Caching.Memory;
using Npgsql;
using Wallet.Core.Models;

namespace Wallet.Infrastructure;

/// <summary>讀取 providers；成功結果快取 30 秒（停用 provider 最多延遲 30 秒生效）。</summary>
public sealed class PgProviderStore(NpgsqlDataSource ds, IMemoryCache cache) : IProviderStore
{
    public async Task<ProviderInfo?> GetByCodeAsync(string code, CancellationToken ct = default)
    {
        if (cache.TryGetValue(code, out ProviderInfo? hit)) return hit;
        await using var conn = await ds.OpenConnectionAsync(ct);
        var p = await conn.QuerySingleOrDefaultAsync<ProviderInfo>(
            "SELECT id AS Id, code AS Code, secret AS Secret, status AS Status FROM providers WHERE code=@code", new { code });
        if (p is not null) cache.Set(code, p, TimeSpan.FromSeconds(30));
        return p;
    }
}
