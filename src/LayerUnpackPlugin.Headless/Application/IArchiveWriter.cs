using LayerUnpackPlugin.Headless.Contracts;

namespace LayerUnpackPlugin.Headless.Application;

/// <summary>单 ZIP 写入端口，只消费已验证清单并完成归档收尾；目录创建、命名和提交由外层事务负责。</summary>
public interface IArchiveWriter
{
    Task WriteAsync(PackPlan plan, Stream output, IProgress<PackProgress>? progress, CancellationToken cancellationToken, PackSecret? secret = null);
}
