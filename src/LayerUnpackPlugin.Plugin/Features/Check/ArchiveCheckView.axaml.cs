using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace LayerUnpackPlugin.Features.Check;

/// <summary>视图只加载绑定；检查、取消和结果语义由子页与 Headless 用例负责。</summary>
public sealed partial class ArchiveCheckView : UserControl
{
    public ArchiveCheckView() => AvaloniaXamlLoader.Load(this);
}
