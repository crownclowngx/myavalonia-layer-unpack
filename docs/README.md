# 开发与使用快速开始

## 开始一个解压批次

1. 打开“解压任务”Document，或用下面的命令启动 Standalone。
2. 添加压缩包、添加文件夹扫描，或将本地文件拖入页面。重复路径只添加一次；目录链接和当前输出目录不会被扫描。
3. 选择输出目录，设置深度 1–16。需要时展开候选密码，一行一个；这是明文编辑区。
4. 点击“开始解压”。输入框中的密码随开始清空，候选池仅保留在当前批次内存。运行期间参数和输入编辑禁用。
5. 查看结果树和节点详情。普通失败继续处理其他独立包；预算或不安全路径会停止批次后续调度。
6. 缺少密码时补充候选，选择失败节点并点击“重试所选失败项”。编码、源内容、格式或预算需要改变时应开始新批次。
7. 点击“打开输出目录”访问已提交文件；清空批次或关闭页面会释放会话，不删除成功输出和源压缩包。

深度 2 表示先解外层，再解所有允许的第二层分支；不是“总共只调用解压两次”。`.tar.gz/.tar.bz2/.tar.xz` 解到文件只占一层；GZip 中的 ZIP 再解需要第二层。

旧 ZIP 默认采用 GB18030 解读未标记 Unicode 的文件名，可选择 UTF-8、CP437 或 CP866；带 Unicode 标志的名称遵循包内标志。TAR 目前要求 UTF-8，无法解码的名称会明确失败，不提交乱码文件。ZipCrypto 密码按 UTF-8 处理。

## 本地开发

依赖：.NET SDK 10，当前验证环境为 Windows x64、Avalonia 12.1.0、Plugin SDK 3.3.0。Python 仅在重新生成标准样本时需要，正常构建和测试不依赖 Python。

```powershell
./tools/verify-local.ps1
dotnet run --project src/LayerUnpackPlugin.Standalone -c Debug
```

统一脚本执行 locked restore、Debug 零警告构建、两个测试项目、格式及 Markdown 路径检查；测试必须非零且全部通过，不允许跳过。日志和 TRX 位于 `artifacts/local-verification/`，渲染图位于 `artifacts/ui/`，性能原始数据位于 `artifacts/performance/`。

这些是本地开发检查。Release、正式 ZIP、安装部署、真实 Host 加载与发布门禁本轮均未执行，也没有新增 CI。

## 在无界面环境调用

只引用 `LayerUnpackPlugin.Headless`，不需要初始化 Avalonia 或 Plugin SDK：

```csharp
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;

var request = new UnpackRequest(
    inputs: [@"D:\资料\外层.zip", @"D:\资料\附件.rar"],
    outputDirectory: @"D:\解压结果",
    maxDepth: 2,
    passwords: ["由调用方明确提供的候选"]);

await using var session = new UnpackService().CreateSession(request);
var result = await session.ExecuteAsync(cancellationToken: cancellationToken);

var retryIds = result.Nodes.Where(node => node.CanRetry).Select(node => node.Id).ToArray();
if (!result.RetryBlocked && retryIds.Length > 0)
    result = await session.RetryAsync(retryIds, ["调用方补充的候选"], cancellationToken: cancellationToken);
```

示例中的 `cancellationToken` 由调用方传入。密码示例只是占位文字，不是真实凭据。请求在创建会话时验证，`ExecuteAsync` 每会话只能开始一次，后续用 `RetryAsync` 或新会话。取消通过结果 `Cancelled` 表达；进入互斥锁之前的取消可能直接抛出 `OperationCanceledException`。完整契约见[执行契约](refactoring/G0002/execution-contract.md)。

## 项目与文档入口

| 项目 | 职责 |
| --- | --- |
| `LayerUnpackPlugin.Headless` | 领域规则、批次用例、引擎适配、输出事务 |
| `LayerUnpackPlugin.Plugin` | 唯一插件入口、Document、真实 View |
| `LayerUnpackPlugin.Standalone` | 独立 Scope 和窗口生命周期预览 |
| `LayerUnpackPlugin.Headless.Tests` | 无 UI 的真实格式、递归、错误、预算与性能测试 |
| `LayerUnpackPlugin.Tests` | 注册、Scope、Document、键盘/绑定/渲染与窗口关闭测试 |

后续阅读：[产品形态](product-shape-and-implementation-plan.md)、[V1 工作项](v1-execution-plan.md)、[文档治理](refactoring/README.md)、[质量基线](refactoring/quality-baseline.md)、[工程职责](project-and-window-responsibilities.md)、[格式矩阵](refactoring/G0003/format-support-matrix.md)、[验收矩阵](refactoring/G0006/acceptance-matrix.md)。

没有自动删除源文件、覆盖已有目录、分卷拼接、压缩修复、密码破解、无限深度或历史恢复。更详细的范围和可信度见格式矩阵与[发布说明](release-notes-v1.md)。

## 后续产品阶段规划

[压缩包工作台路线图](roadmap/README.md)规划 R01–R08：先简化现有解压交互，再增加 ZIP 压缩、批量打包与加密、浏览提取、输出整理、重打包、格式诊断和工作流协作。以上是未来目标，不是当前使用说明中的已实现能力。

生成下一阶段执行计划前，读取[共同产品原则](roadmap/product-principles.md)、所选阶段文档和[执行计划生成模板](roadmap/stage-execution-plan-template.md)。产品阶段 R 编号与实施档案 G 编号分别维护，当前建议从 R01 开始。
