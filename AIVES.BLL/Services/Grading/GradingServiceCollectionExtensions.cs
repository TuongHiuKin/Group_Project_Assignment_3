using AIVES.BLL.Interfaces.Grading;
using AIVES.BLL.Models.Grading;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace AIVES.BLL.Services.Grading;

public static class GradingServiceCollectionExtensions
{
    /// <summary>
    /// Registers the background grading queue, worker, startup recovery and regrade entry point (G4-RT-01..RT-04).
    /// <see cref="IGradingOrchestrator"/> (LEAD-03) is resolved per job; until it is registered, jobs are skipped
    /// safely and sessions remain Pending for recovery.
    /// </summary>
    public static IServiceCollection AddVivaGradingWorker(this IServiceCollection services)
    {
        services.AddOptions<GradingWorkerOptions>().BindConfiguration(GradingWorkerOptions.SectionName);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IGradingQueue, ChannelGradingQueue>();
        services.TryAddSingleton<GradingJobProcessor>();
        services.TryAddScoped<IGradingRecoveryScanner, GradingRecoveryScanner>();
        services.TryAddScoped<IGradingRegradeService, GradingRegradeService>();
        services.AddHostedService<VivaGradingBackgroundService>();
        return services;
    }
}
