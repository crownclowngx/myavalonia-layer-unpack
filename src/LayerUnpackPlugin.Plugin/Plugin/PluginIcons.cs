using MyAvaloniaManagement.PluginSdk.UI;

namespace LayerUnpackPlugin.Plugin;

/// <summary>本插件专属矢量。保持纯不可变数据，不保存 Host 服务、控件或主题画刷。</summary>
internal static class PluginIcons
{
    /// <summary>归档盒与向外箭头。公共八图标无法准确表达该语义，使用原创 20×20 填充路径。</summary>
    internal static VectorIconDefinition ArchiveUnpack { get; } = new(
        "M2,10h16v8h-16Z M4,12h12v4h-12Z M1,8h18v2h-18Z M10,1l4,4h-3v3H9V5H6Z M8,13h4v2h-4Z", 20, 20);

    /// <summary>归档盒与向内箭头。公共八图标无法准确表达该语义，使用原创 20×20 填充路径。</summary>
    internal static VectorIconDefinition ArchivePack { get; } = new(
        "M2,10h16v8h-16Z M4,12h12v4h-12Z M1,8h18v2h-18Z M9,1h2v3h3l-4,4l-4,-4h3Z M8,13h4v2h-4Z", 20, 20);

}
