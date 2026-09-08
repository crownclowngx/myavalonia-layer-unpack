# G0009 批次创建与重试契约

日期：2026-09-08。继承 [R02 单包契约](../G0008/creation-contract.md)的路径、源摘要和不覆盖事务；本文件描述 R03 增量。

## 调用示例

```csharp
var service = new PackBatchService();
var request = new PackBatchRequest(
    inputs: [@"D:\资料\甲", @"D:\资料\乙"],
    outputDirectory: @"D:\交付",
    grouping: PackGrouping.Separate,
    options: new PackOptions {
        Compression = PackCompression.Standard,
        Encrypt = true,
        Exclusions = new PackExclusionRules([".tmp"], ["obj"])
    });
var plan = await service.PrepareAsync(request, cancellationToken: cancellationToken);
// 审阅 plan.Groups 中的输出路径、Plan.Entries 与 Plan.ExcludedItems。
// targetPassword 必须由本次调用方明确提供，不取自解压候选池。
await using var session = service.CreateSession(plan, new PackSecret(targetPassword));
var result = await session.ExecuteAsync(cancellationToken: cancellationToken);
if (result.CanRetry)
    result = await session.RetryFailedAsync(cancellationToken: cancellationToken);
```

`targetPassword` 和 `cancellationToken` 由调用方提供；此示例不存储密码。准备只读；Headless 不切换 UI 线程。请求、计划、结果中的集合均复制为只读集合。已有普通 `PackService` 调用无需新增参数；单包 AES 在执行时另传 `secret`。加密开关和秘密必须一致，否则抛出验证错误，绝不自动降级。

## 分组、命名和包内路径

- 输入须为完整本地路径；按系统路径比较去重、规范路径排序，选中父目录时吸收子项。根名冲突按不区分大小写编号。
- 合并模式默认输出 `ArchiveName`，一个单包事务。分别模式以最终顶层项形成一组；文件夹后代和原有空目录留在本组，保留顶层目录名；单文件直接作为包内根文件。
- 分别目标以目录名或去掉最后扩展名的文件名加 `.zip`，空基名回退“资料”，基名最多 150 字符，冲突依次加 ` (1)`。准备避开已有文件/目录和本批次已分配名称；最多 10,000 次尝试。
- 预览给出来源 → 建议输出 → 包内根，以及全部保留条目路径。最终提交遇到竞态仍可能继续编号，结果路径是实际可用位置，不能把建议路径当已存在产物。
- 分别模式要求目标目录在所有选中源目录之外。合并模式仍支持源内已有目录，排除本目标与自有临时文件；源内新输出子目录仍需先创建后重新准备。该限制防止组间产物污染已审阅清单，不凭 `.tmp` 等名称静默过滤用户内容。

## 排除语义

| 规则 | 精确定义 |
| --- | --- |
| 扩展名 | 最后一段 `Path.GetExtension`，例如 `.tmp` 命中 `a.TMP`，不命中 `a.tmp.txt` |
| 目录名 | 完整叶名，任意深度，包括选中的根；`obj` 不命中 `obj2` |
| 大小写 | 两类均采用 OrdinalIgnoreCase，不依赖当前语言区域 |
| 目录传播 | 命中目录后不遍历后代、不读取文件内容；预览计该目录一项，明确“包含全部后代” |
| 规则语法 | 每类最多 32 个值，每值最多 80 字符；拒绝通配符、路径、控制字符、首尾空格等，不支持正则或脚本 |
| 默认 | 不排除任何类型、隐藏文件或看起来临时的文件 |

`ExcludedItems` 是可检查的命中项，不声称计出了被剪枝子树的全部文件数；预览明确“排除 N 项（目录含后代）”。规则造成的空祖先不写入；本来就空的合法目录保留。整组条目为零时为 `Skipped`，不产生 ZIP、不计成功。已排除子树内部变化不改变收录结果；新出现的未排除内容会在复验时使计划失效。

## 失败、重试和取消

| 情况 | 当前组 | 其他组 | 下一步 |
| --- | --- | --- | --- |
| 请求无效、批次累计预算或总准备超时 | 批次准备拒绝，无产物 | 未执行 | 修改参数并重新准备 |
| 一组准备失败 | `Failed`，没有完整计划 | 继续准备/执行独立组 | 重新准备该来源建立新任务 |
| 读取、输出、单组超时 | 失败并回滚 | 继续 | 有完整计划且无清理警告时可原地重试 |
| 来源变化、不安全路径、组预算、未知错误 | 失败并回滚 | 继续独立组 | 修正原因后新任务，不原地重试 |
| 用户取消 | 当前及未开始的待执行组为 `Cancelled` | 已成功、已失败、已跳过结果保留 | 取消项不归入失败重试，可明确重新准备 |
| 清理残留 | 原故障及准确 `CleanupWarning` | 独立组继续 | 不提供原地重试；处理残留后新任务 |

执行只允许一次；重复执行拒绝。`RetryFailedAsync` 只处理当前结果中 `CanRetry` 的组，重试为空时返回原结果，不复制成功包。重试仍执行来源清单/元数据/逐文件 SHA-256 验证；源变更不是自动接纳新内容。原结果对象是不可变快照，后续运行产生新结果。

执行和重试互斥。DisposeAsync 取消并等待正在运行的单包清理，随后释放目标秘密；重复释放可安全等待同一关闭任务。已成功文件不回滚、不删除，关闭不保存跨重启队列。

## 预算和统计

沿用 PackLimits 默认值：1,000 个输入、2 GiB 单文件、10 GiB 总源内容、100,000 条目、目录深度 128、12 GiB 单个 ZIP。批次准备总时限 10 分钟；执行和重试每个单包各 10 分钟。分别模式额外检查所有成功准备组的累计源字节，以及保留条目与排除命中项总数，避免通过分组绕开清单总量限制。失败组的单次扫描也受自身预算和批次准备时限约束。

结果分别提供成功、失败、跳过、取消数量；批次 SourceBytes/ArchiveBytes 只统计已提交产物，逐组结果另保留各组源字节。零字节不计算比率，不假设 ZIP 比源小。进度明确为当前组和当前文件，不推算未读取组的压缩大小或完成时间。

## 秘密寿命

PackSecret 通过构造参数接收一个明确密码，1–1024 个字符，保留大小写和空格。没有候选尝试、成功密码继承或来源密码复用。创建会话成功后由会话拥有该对象；一个对象不应跨两个会话共享。普通调用描述只包含 Encrypt 开关；密码没有可序列化属性，ToString 遮蔽，异常不复制第三方错误链。

GUI 二次输入、默认遮蔽，可临时显示。完成/取消后清空输入；允许失败重试时秘密仅保留在原会话，重试结束、清空、新任务或关闭释放。释放撤销引用，不能承诺清零托管字符串的所有副本。内容加密不隐藏文件名、目录名及 ZIP 可见元数据。
