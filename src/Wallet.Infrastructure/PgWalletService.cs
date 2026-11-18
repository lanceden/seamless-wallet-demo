using Dapper;
using Npgsql;
using Wallet.Core.Models;

namespace Wallet.Infrastructure;

public sealed class PgWalletService(NpgsqlDataSource ds) : IWalletService
{
    public async Task<WalletResponse> GetBalanceAsync(int providerId, string playerId, CancellationToken ct = default)
    {
        await using var conn = await ds.OpenConnectionAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<(string Status, long Balance)?>(
            @"SELECT p.status, w.balance FROM players p JOIN wallets w ON w.player_id = p.id WHERE p.external_id = @playerId",
            new { playerId });
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (row is null) return new WalletResponse(ErrorCodes.PlayerNotFound, null, null, now);
        if (row.Value.Status != "ACTIVE") return new WalletResponse(ErrorCodes.PlayerLocked, null, null, now);
        return new WalletResponse(ErrorCodes.Ok, row.Value.Balance, null, now);
    }
}
