using Microsoft.Extensions.DependencyInjection;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Infrastructure;

namespace LayerUnpackPlugin.Plugin;

public static class LayerUnpackPluginServices
{
    /// <summary>登记插件自己的业务服务；Standalone 可以复用同一个组合入口。</summary>
    public static IServiceCollection AddLayerUnpackPluginServices(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IArchiveExtractor, ArchiveExtractor>();
        services.AddScoped<IUnpackService, UnpackService>();
        services.AddSingleton<PackPlanner>();
        services.AddSingleton<IArchiveWriter, ZipArchiveWriter>();
        services.AddScoped<IPackService, PackService>();
        return services;
    }
}
