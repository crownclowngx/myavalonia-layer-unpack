using LayerUnpackPlugin.Headless.Contracts;
using LayerUnpackPlugin.Headless.Domain;

namespace LayerUnpackPlugin.Headless.Application;

/// <summary>复合用例的窄写入端口，在原创建事务中增加总账本和提交前回读。
/// 复用同一写入器、密码契约及不覆盖提交，不复制第二套 ZIP 创建逻辑。</summary>
public interface IRepackWriter
{
    Task<PackResult> ExecuteAsync(PackPlan plan, RepackBudget budget, PackSecret? secret,
        IProgress<PackProgress>? progress, Action verifying, CancellationToken cancellationToken);
}
public sealed class RepackWriter(PackPlanner planner, IArchiveWriter writer) : IRepackWriter
{
    public Task<PackResult> ExecuteAsync(PackPlan plan, RepackBudget budget, PackSecret? secret,
        IProgress<PackProgress>? progress, Action verifying, CancellationToken cancellationToken) =>
        new PackService(planner, writer).ExecuteCoreAsync(plan, progress, cancellationToken, secret, budget, verifying);
}
