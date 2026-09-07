using LayerUnpackPlugin.Features.Unpack;
using LayerUnpackPlugin.Plugin;
using Microsoft.Extensions.DependencyInjection;
using MyAvaloniaManagement.PluginSdk;

namespace LayerUnpackPlugin.Standalone;

/// <summary>预览的显式组合。窗口生命周期测试链接同一源码，避免引入 Desktop 平台模块干扰无窗口测试进程。</summary>
internal static class StandaloneServices
{
    internal static IServiceCollection Create()
    {
        var services = new ServiceCollection();
        services.AddLayerUnpackPluginServices();
        services.AddScoped<PreviewDocumentLifetime>();
        services.AddScoped<IDocumentLifetime>(p => p.GetRequiredService<PreviewDocumentLifetime>());
        services.AddScoped<UnpackDocument>();
        services.AddTransient<UnpackView>();
        return services;
    }
}
