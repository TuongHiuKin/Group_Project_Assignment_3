using Microsoft.Extensions.DependencyInjection;
using AIVES.BLL.Interfaces.VivaInterview;
using AIVES.BLL.Services.VivaInterview;

namespace AIVES.BLL;

public static class DependencyInjection
{
    /// <summary>
    /// Registers BLL services for Group 3 AI Viva Interview Core.
    /// </summary>
    public static IServiceCollection AddVivaInterviewServices(this IServiceCollection services)
    {
        services.AddScoped<IVivaSessionOrchestrator, VivaSessionOrchestrator>();
        return services;
    }
}
