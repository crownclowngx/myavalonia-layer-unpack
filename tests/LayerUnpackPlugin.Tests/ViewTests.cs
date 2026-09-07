using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using LayerUnpackPlugin.Features.Unpack;
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Tests;
using Xunit;

namespace LayerUnpackPlugin.Tests;

public sealed class ViewTests
{
    [AvaloniaTheory]
    [InlineData(false, 1180, 760)]
    [InlineData(true, 1180, 760)]
    [InlineData(false, 1000, 600)]
    [InlineData(true, 800, 600)]
    [InlineData(false, 640, 520)]
    [InlineData(true, 640, 520)]
    public async Task 真实View在主题和窗口尺寸下可渲染且操作绑定有效(bool dark, int width, int height)
    {
        using var w = new TestWorkspace();
        using var lifetime = new TestLifetime();
        await using var document = new UnpackDocument(new UnpackService(), lifetime) { OutputDirectory = w.Output, MaxDepth = 2 };
        var inner = w.Zip("内层.zip", ("资料.txt", "内容"u8.ToArray()));
        await document.AddPathsAsync([w.Zip("示例.zip", ("内层.zip", File.ReadAllBytes(inner)), ("待检查.rar", "invalid"u8.ToArray()))]);
        var view = new UnpackView { DataContext = document };
        var window = new Window { Content = view, Width = width, Height = height, RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light };
        try
        {
            window.Show();
            await document.StartCommand.ExecuteAsync(null);
            document.SelectedNode = document.Roots[0].Children[1];
            Dispatcher.UIThread.RunJobs();
            var start = view.FindControl<Button>("StartButton")!;
            Assert.Same(document.StartCommand, start.Command);
            Assert.True(start.IsEnabled);
            var depth = view.FindControl<NumericUpDown>("DepthEditor")!;
            Assert.Equal(2, depth.Value);
            depth.Value = 3;
            Assert.Equal(3, document.MaxDepth);
            var output = view.FindControl<TextBox>("OutputEditor")!;
            Assert.True(output.Focus());
            window.KeyPress(Avalonia.Input.Key.Tab, Avalonia.Input.RawInputModifiers.None, Avalonia.Input.PhysicalKey.Tab, null);
            Assert.NotSame(output, window.FocusManager!.GetFocusedElement());
            Assert.Contains(view.GetVisualDescendants().OfType<TextBlock>(), t => t.Text?.Contains("已发现 3 项", StringComparison.Ordinal) == true);
            var startOrigin = start.TranslatePoint(default, window)!.Value;
            Assert.InRange(startOrigin.Y, 0, window.ClientSize.Height - start.Bounds.Height);
            Assert.InRange(startOrigin.X, 0, window.ClientSize.Width - start.Bounds.Width);
            using var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            Assert.True(frame.PixelSize.Width >= width);
            var destination = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/ui/G0007"));
            Directory.CreateDirectory(destination);
            frame.Save(Path.Combine(destination, $"document-{(dark ? "dark" : "light")}-{width}x{height}.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        }
        finally { window.Close(); }
    }
}
