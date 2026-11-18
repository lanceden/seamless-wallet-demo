using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Wallet.Reconciliation;

/// <summary>每日排程：到了 RunAtUtc 就對帳「前一個 UTC 日」。</summary>
public sealed class ReconciliationJob(ReconciliationService service, IOptions<ReconciliationOptions> options, ILogger<ReconciliationJob> log)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        if (!options.Value.JobEnabled)
        {
            log.LogInformation("Reconciliation job disabled");
            return;
        }
        var runAt = TimeOnly.Parse(options.Value.RunAtUtc, System.Globalization.CultureInfo.InvariantCulture);
        while (!ct.IsCancellationRequested)
        {
            var now = DateTime.UtcNow;
            var next = ReconciliationSchedule.NextRunUtc(now, runAt);
            log.LogInformation("Next reconciliation at {Next:u}", next);
            try { await Task.Delay(next - now, ct); }
            catch (OperationCanceledException) { return; }

            var date = ReconciliationSchedule.DateToReconcile(DateTime.UtcNow);
            try { await service.RunAllActiveAsync(date, ct); }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                log.LogError(e, "Reconciliation for {Date} crashed", date);
            }
        }
    }
}

public static class ReconciliationServiceCollectionExtensions
{
    public static IServiceCollection AddReconciliation(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration config)
    {
        services.Configure<ReconciliationOptions>(config.GetSection(ReconciliationOptions.Section));
        services.AddSingleton<ReconciliationService>();
        services.AddHostedService<ReconciliationJob>();
        return services;
    }
}
