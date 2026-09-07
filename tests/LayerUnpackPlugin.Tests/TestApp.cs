using Avalonia;
using Avalonia.Headless;
using Avalonia.Themes.Fluent;

[assembly: AvaloniaTestApplication(typeof(LayerUnpackPlugin.Tests.TestApp))]

namespace LayerUnpackPlugin.Tests;

public sealed class TestApp : Avalonia.Application
{
    public override void Initialize() => Styles.Add(new FluentTheme());
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<TestApp>().UseSkia()
        .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
