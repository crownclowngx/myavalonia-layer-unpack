using MyAvaloniaManagement.PluginSdk;

namespace LayerUnpackPlugin.Constants;

/// <summary>稳定身份属于外部兼容契约，不随显示名或内部类名变化。</summary>
public static class PluginIds
{
    public static readonly PluginId Plugin = new("myavalonia.plugin.layer.unpack");
    public static readonly DocumentTypeId UnpackDocument = new("myavalonia.plugin.layer.unpack.document.main");
}
