using LayerUnpackPlugin.Constants;
using LayerUnpackPlugin.Features.Unpack;
using LayerUnpackPlugin.Features.Pack;
using LayerUnpackPlugin.Features.Browse;
using LayerUnpackPlugin.Workflow;
using MyAvaloniaManagement.PluginSdk.UI;

namespace LayerUnpackPlugin.Plugin;

/// <summary>登记解压、压缩与浏览三类普通任务；操作保留在各自页面，无 Tool、全局命令或历史持久化。</summary>
public sealed class LayerUnpackPluginModule : IPluginModule
{
    public void Configure(IPluginRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        registration.Services.AddLayerUnpackPluginServices();
        registration.AddWorkflowAction<UnpackWorkflowAction>(ArchiveWorkflowActions.Unpack);
        registration.AddWorkflowAction<CreateArchiveWorkflowAction>(ArchiveWorkflowActions.Create);
        registration.AddDocument<UnpackDocument, UnpackView>(new DocumentDescriptor(
            PluginIds.UnpackDocument, "解压任务", "批量递归解压与本批次密码共享", "文件工具"));
        registration.AddDocument<PackDocument, PackView>(new DocumentDescriptor(
            PluginIds.PackDocument, "压缩任务", "将文件和文件夹合成一个 ZIP", "文件工具"));
        registration.AddDocument<BrowseDocument, BrowseView>(new DocumentDescriptor(
            PluginIds.BrowseDocument, "浏览任务", "查看 ZIP 目录、搜索并提取所选内容", "文件工具"));
    }
}
