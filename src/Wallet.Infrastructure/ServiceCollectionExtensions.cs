using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Wallet.Core.Models;

namespace Wallet.Infrastructure;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddWalletInfrastructure(this IServiceCollection services, string connectionString)
    {
        services.AddMemoryCache();
        services.AddSingleton(NpgsqlDataSource.Create(connectionString));
        services.AddSingleton<IProviderStore, PgProviderStore>();
        services.AddSingleton<IWalletService, PgWalletService>();
        services.AddSingleton<IAdminService, PgAdminService>();
        services.AddSingleton<IInvariantService, PgInvariantService>();
        return services;
    }
}
