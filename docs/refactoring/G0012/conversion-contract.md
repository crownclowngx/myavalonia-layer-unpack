# G0012 转换与结果打包契约

## 入口与结构语义

`ConversionRequest` 只保存公开参数，不含密码。`FormatOnly` 固定读取一层并关闭内嵌包发现；ZIP/RAR 等内部文件逐字节保留。普通转换不接受整理或排除规则，不能通过隐式筛选实现表面成功。

`ExpandAndOrganize` 显式使用总层数 1–16，顶层算 1，普通目录不计层，组合 TAR 沿用既有一层语义。按现有递归会话处理全部分支，边界节点不再展开。R05 的 All/Pdf/Images/PdfAndImages 和单目录包装链规则形成新预期清单；All 保留内部原包，筛选模式只带入匹配文件及父目录。无匹配时 `Skipped`，不写空壳包；真正的空归档普通转换仍创建空 ZIP。

每个来源独立转换。一般损坏、密码或写入故障不撤销其他成功产物；应展开分支失败时整个该来源不创建残缺 ZIP。共享预算耗尽、不安全路径的既有批次停止信号、取消、总超时或未清理工作区会停止后续调度，未运行来源明确标记。

## 现有结果与来源变化

解压结果只接受明确提交节点及其 `CommittedEntries`，包括用户明确选择的部分已提交结果；失败或认证提示随关联产物保留。父子关系沿用 R05 真实来源链校验，不根据文件名猜测。没有清单的旧结果明确拒绝。

预览和执行验证目录直接成员、文件类型、长度、修改／创建时间及 SHA-256。文件删除、同长度内容变化或清单内目录新增历史成员会拒绝；不会重新扫描扩大输入。分别执行只依赖该来源凭据，一个来源变化不阻止其他独立来源。整理分别模式先核对完整结果，再按已保存来源边界分组，边界缺失或未覆盖提交条目时拒绝。

结果打包目标位于来源目录之外。合成解压结果保留顶层来源目录；分别包去掉来源包装层。合成整理结果保留整理目录内部结构；分别按 `SourceGroups`，不根据事后扫描猜测分组。生成预览不创建目标目录，也不写 ZIP。

## 秘密与可信度

`ConvertAsync` 的 `sourcePasswords` 只传给解压候选池；`targetSecret` 是独立 `PackSecret`，只传给创建和目标回读。默认 `Encrypt=false` 且目标秘密为空。加密开关与目标秘密不一致即拒绝，来源候选不替代创建密码。调用方拥有 `PackSecret`，执行结束后负责 Dispose；UI 后台调用使用 `using` 释放，字段在开始和退出时清空。

公开请求、计划、结果、日志与文档不保存秘密。释放撤销托管字符串引用，不承诺物理内存清零。输入候选仍保留空格、大小写，沿用最多 64 个、每个最多 1024 字符的规则。

RAR5 加密来源仍可能处于“已解码、未验证加密内容校验值”。目标 ZIP 与读取到的内容相符只说明目标重建正确，不能升级来源认证。该产物为 `CompletedWithWarnings`，批次单列受限数。详细元数据保留范围见[矩阵](preservation-matrix.md)。

## 总体资源与阶段进度

| 边界 | 默认值／口径 |
| --- | --- |
| 整项累计写入 | 24 GiB；所有来源的展开输出＋组合压缩流中间文件＋所有 ZIP 暂存增长 |
| 总超时 | 30 分钟，覆盖来源读取、规划、写入、回读；清理在取消后仍完成 |
| 解压总账本 | 10 GiB 展开、100,000 条目、1,000 节点、2,048 尝试；全部来源及递归共用，不按包重置 |
| 单个来源读取／单文件展开 | 4 GiB／2 GiB，仍受总账本限制 |
| 创建阶段 | 单包内容最多 10 GiB、单文件 2 GiB、单 ZIP 12 GiB；整项累计写入条目 100,000 |
| 整理空间 | 直接使用映射，0 额外复制字节；不是把另一套 10 GiB 预算重置后作为总体上限 |

写入前扣额，失败、清理和回滚不退款；ZIP 头部回填不重复扣额。写入器或磁盘失败前已预扣的片段可能未实际完成，因此错误结果里的计量是保守计费值，不是最终磁盘占用。成功时 ZIP 计量等于最终长度；源包读取和摘要校验不写磁盘，其时间仍属于同一总超时。清单/目录的内存由条目上限约束，缓冲不随文件大小增长；不承诺绝对进程内存上限。

进度阶段为 Reading → Planning → Writing → Verifying → Committed → Cleaning，包含当前来源索引、总来源数和累计资源。阶段进度不作为事务成功凭据，不用尚未处理的来源估算整批百分比；观察者异常与迟到 UI 回调不改变提交事实。

## 提交、取消与清理

每个 ZIP 完成写入、收尾、来源复验和逐条目回读后独立不覆盖提交。取消在下一检查点停止，所有已提交产物保留；未提交 ZIP 只由自己的文件事务删除。取消发生在某包提交之后，该包仍是成功，后续包取消。

转换工作区位于目标目录下随机命名的 `.layer-unpack-*` 目录；只有本任务创建的工作区可被清理。其内部提交的解压结果是中间内容，外部用户已有目录和已提交 ZIP 不属于清理范围。残留绝对路径返回 `CleanupWarnings`，本期没有自动删除来源或保留中间结果选项。旧的用户解压／整理结果在结果打包成功、失败或取消后都保留。

文件系统防护沿用既有链接／重解析点策略和 Windows 只共享读句柄，不声称内核快照或敌对进程隔离。返回结果是当前执行快照，不提供恢复、断点续传或自动重试；再开始是用户发起的新任务，可能生成编号新 ZIP。

## Headless 示例

```csharp
var service = new RepackService();
var converted = await service.ConvertAsync(
    new ConversionRequest([sourceRar, sourceSevenZip], outputDirectory),
    sourcePasswords: candidatePasswords,
    cancellationToken: cancellationToken);

// existingResult 是明确的 UnpackResult，或已有来源清单的 OrganizationResult。
var plan = await service.PrepareAsync(existingResult, outputDirectory,
    PackGrouping.Separate, new PackOptions(), cancellationToken);
// 先将 plan.Groups 和各 PackPlan.Entries 展示给用户，再响应“开始”。
var packed = await service.ExecuteAsync(plan, cancellationToken: cancellationToken);
```

变量由调用方提供。目标加密时显式设置 `PackOptions.Encrypt=true`，单独构造并释放 `PackSecret`，不从 `candidatePasswords` 中挑选。
