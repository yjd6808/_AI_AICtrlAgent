using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace AiCtrl.Core.Runtime;

public static class RuntimeServiceCollectionExtensions
{
    public static IServiceCollection AddAcRuntime(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IAcEventBus, ChannelAcEventBus>();
        services.TryAddSingleton(sp => new AcApp(
            sp,
            sp.GetServices<IAcSystem>(),
            sp.GetRequiredService<IAcEventBus>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<Microsoft.Extensions.Logging.ILoggerFactory>()));
        services.AddHostedService(sp => sp.GetRequiredService<AcApp>());
        return services;
    }

    public static IServiceCollection AddAcSystem<T>(this IServiceCollection services) where T : class, IAcSystem
    {
        services.AddSingleton<IAcSystem, T>();
        return services;
    }
}
