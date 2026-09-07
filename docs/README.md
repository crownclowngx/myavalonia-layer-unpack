# 开发与使用快速开始

## 开始一个解压批次

1. 打开“解压任务”Document，或用下面的命令启动 Standalone。
2. 添加压缩包、点击“扫描文件夹”，或将本地文件拖入页面；“查看输入清单”中可移除项目。重复路径只添加一次，目录链接和当前输出目录不会被扫描。
3. 检查输出位置。同一直接父目录的输入建议其旁的“解压结果”，多来源需选择；手动设置不会被后续输入覆盖。默认一层，需要时开启“继续解压内部压缩包”并设置包含外层的总层数 2–16。密码与编码按需展开。
4. 点击“开始解压”。输入框中的密码随开始清空，候选池仅保留在当前批次内存。运行期间参数和输入编辑禁用。
5. 先查看结果摘要与问题项，需要时展开完整处理树。普通失败继续处理其他独立包；预算或不安全路径会停止后续调度。
6. 点击问题项查看原因，“补充密码”会展开并聚焦候选输入，再点击“重试所选失败项”。重试沿用原批次的输出、层数、编码和预算；参数变化需点击“开始新批次”。
7. 点击底部“打开结果”直接访问已提交产物，或在节点详情打开对应目录。清空批次恢复默认设置并释放会话，不删除成功输出和源压缩包。

深度 2 表示先解外层，再解所有允许的第二层分支；不是“总共只调用解压两次”。`.tar.gz/.tar.bz2/.tar.xz` 解到文件只占一层；GZip 中的 ZIP 再解需要第二层。

旧 ZIP 默认采用 GB18030 解读未标记 Unicode 的文件名，可选择 UTF-8、CP437 或 CP866；带 Unicode 标志的名称遵循包内标志。TAR 目前要求 UTF-8，无法解码的名称会明确失败，不提交乱码文件。ZipCrypto 密码按 UTF-8 处理。

## 开始一个压缩任务

1. 在插件中打开“压缩任务”，或在 Standalone 切换到同名标签。
2. 添加或拖入文件／文件夹。同一直接父目录会自动建议输出位置，多来源需选择；名称和位置可修改。
3. 查看文件数、总源字节；需要时展开清单检查父子合并和同名根编号。输入准备只读，不创建 ZIP。
4. 点击“开始压缩”，使用普通 ZIP、标准 Deflate、UTF-8，无需配置算法。首次才发现重要路径映射变化时会先展示清单，再点击开始执行。
5. 完成后在页面上方查看实际路径、大小和耗时，点击“打开输出文件夹”。取消等待清理退出；失败后再次开始会重新准备来源。

保留顶层文件夹、内容和空目录。同名输出自动编号，源文件保留；既有源目录内输出会排除本次目标和暂存文件，源内新输出子目录需先创建或改选已有目录。创建矩阵、资源与来源变化边界见[G0008 契约](refactoring/G0008/creation-contract.md)。当前没有压缩密码、分别打包或其他创建格式。

## 本地开发环境

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

创建 ZIP 使用独立用例：

```csharp
var pack = new PackService();
var plan = await pack.PrepareAsync(
    new PackRequest([@"D:\资料\项目"], @"D:\交付\项目.zip"),
    cancellationToken: cancellationToken);
var packed = await pack.ExecuteAsync(plan, cancellationToken: cancellationToken);
```

先检查 `plan.Roots` / `plan.Entries` 的路径与清单，再执行。准备不落盘；重复执行可能生成编号新包。压缩与解压使用不同契约，共同保持无 UI 依赖和独立生命周期。

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

[压缩包工作台路线图](roadmap/README.md)规划 R01–R08。R01 简洁解压已实施，R02 基础 ZIP 已进入 G0008 本地候选，实际验证见[G0008 结果](refactoring/G0008/result.md)。R03–R08 的分别打包、加密、浏览、整理等仍为未来目标。

生成下一阶段执行计划前，读取[共同产品原则](roadmap/product-principles.md)、所选阶段文档和[执行计划生成模板](roadmap/stage-execution-plan-template.md)。R01 映射 G0007、R02 映射 G0008，后续可细化 R03，并复核各阶段原生交互遗留。
