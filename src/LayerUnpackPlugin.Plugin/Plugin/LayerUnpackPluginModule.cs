using LayerUnpackPlugin.Constants;
using LayerUnpackPlugin.Features.Unpack;
using MyAvaloniaManagement.PluginSdk.UI;

namespace LayerUnpackPlugin.Plugin;

/// <summary>只登记服务和一个普通 Document，不贡献 Tool、全局命令或历史持久化。</summary>
public sealed class LayerUnpackPluginModule : IPluginModule
{
    public void Configure(IPluginRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        registration.Services.AddLayerUnpackPluginServices();
        registration.AddDocument<UnpackDocument, UnpackView>(new DocumentDescriptor(
            PluginIds.UnpackDocument, "解压任务", "批量递归解压与本批次密码共享", "文件工具"));
    }
}
