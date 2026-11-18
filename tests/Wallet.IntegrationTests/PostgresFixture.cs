using Testcontainers.PostgreSql;
using Wallet.Infrastructure;

namespace Wallet.IntegrationTests;

public sealed class PostgresFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder().WithImage("postgres:16-alpine").Build();
    public string ConnectionString => _container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        await Migrator.ApplyAsync(ConnectionString);
    }

    public Task DisposeAsync() => _container.DisposeAsync().AsTask();
}
