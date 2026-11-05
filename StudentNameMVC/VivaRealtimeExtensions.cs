using System.Text.Json.Serialization;
using AIVES.BLL.Interfaces.VivaInterview;
using AIVES.BLL.Services.Grading;
using AIVES.BLL.Services.VivaInterview;
using Microsoft.Extensions.DependencyInjection.Extensions;
using StudentNameMVC.Hubs;
using StudentNameMVC.Services;

namespace StudentNameMVC;

public static class VivaRealtimeExtensions
{
    /// <summary>Register transport only. Lead must also supply real Viva repositories and AI adapters.</summary>
    public static IServiceCollection AddVivaRealtime(this IServiceCollection services)
    {
        services.AddSignalR(options => options.EnableDetailedErrors = false)
            .AddJsonProtocol(options => options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<VivaTimerManager>();
        services.TryAddScoped<IVivaRealtimeSessionService, VivaRealtimeSessionService>();
        services.TryAddScoped<IVivaRealtimePublisher, VivaRealtimePublisher>();
        services.AddHostedService<VivaTimerWorker>();
        services.AddVivaGradingWorker(); // G4-RT-01..RT-04: background grading queue + recovery
        return services;
    }

    public static void MapVivaRealtime(this IEndpointRouteBuilder endpoints) =>
        endpoints.MapHub<VivaHub>("/hubs/viva").RequireAuthorization();
}
