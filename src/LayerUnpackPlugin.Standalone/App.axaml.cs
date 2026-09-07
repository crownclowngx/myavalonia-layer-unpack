using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using LayerUnpackPlugin.Features.Unpack;
using LayerUnpackPlugin.Plugin;
using Microsoft.Extensions.DependencyInjection;
using MyAvaloniaManagement.PluginSdk;

namespace LayerUnpackPlugin.Standalone;

public sealed partial class App : Application
{
    private ServiceProvider? _provider;
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = StandaloneServices.Create();
            _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true, ValidateScopes = true });
            desktop.MainWindow = new MainWindow(_provider);
            desktop.Exit += (_, _) => { _provider?.Dispose(); _provider = null; };
        }

        base.OnFrameworkInitializationCompleted();
    }

}
