using LayerUnpackPlugin.Constants;
using LayerUnpackPlugin.Features.Unpack;
using LayerUnpackPlugin.Features.Pack;
using MyAvaloniaManagement.PluginSdk.UI;

namespace LayerUnpackPlugin.Plugin;

/// <summary>登记压缩与解压两类普通任务；操作保留在各自页面，无 Tool、全局命令或历史持久化。</summary>
public sealed class LayerUnpackPluginModule : IPluginModule
{
    public void Configure(IPluginRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        registration.Services.AddLayerUnpackPluginServices();
        registration.AddDocument<UnpackDocument, UnpackView>(new DocumentDescriptor(
            PluginIds.UnpackDocument, "解压任务", "批量递归解压与本批次密码共享", "文件工具"));
        registration.AddDocument<PackDocument, PackView>(new DocumentDescriptor(
            PluginIds.PackDocument, "压缩任务", "将文件和文件夹合成一个 ZIP", "文件工具"));
    }
}
