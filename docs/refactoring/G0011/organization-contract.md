# G0011：整理规则、来源与复制契约

## Headless 调用

```csharp
var service = new OrganizationService();
// unpackResult 是当前已退出的解压操作结果；outputParent 是用户明确看到的位置。
var plan = await service.PlanAsync(unpackResult, outputParent,
    new OrganizationRules(OrganizationFileTypes.Pdf, FlattenWrappingDirectories: true), cancellationToken);
// 先呈现 plan.Sources / Mappings / Conflicts / OutputDirectory。
var organized = await service.ExecuteAsync(plan, progress: null, cancellationToken);
// 仅 Completed 的 OutputDirectory 与 Entries 是新的已提交产物。
```

`OrganizationPlanner.CreateAsync` 另外接受 `OrganizationLimits`，供 Headless 调用方明确限制工作量。默认 100,000 个输入条目（含源根和目录）、10 GiB 总输入文件、2 GiB 单文件、每次规划/执行最多 10 分钟。大小限制针对完整提交清单，筛选不能绕过来源验证成本。复制流缓冲 128 KiB，租用并归还；不把整文件读入内存。时间限制也覆盖哈希与直接成员检查。

输入必须含每个成功归档的提交清单。旧调用方构造的 null 清单拒绝；不会补扫历史。运行中的解压结果拒绝，已退出的部分失败或取消结果可整理其中已成功提交的内容。失败节点不产生可整理条目。

## 有限规则

| 规则 | 行为 |
| --- | --- |
| 全部文件（默认） | 保留普通文件、嵌套归档及空目录；不同顶层来源分别输出 |
| PDF | 大小写不敏感 `.pdf` 扩展名 |
| 图片 | `.png .jpg .jpeg .gif .bmp .webp .tif .tiff` |
| PDF 和图片 | 上述集合并集 |
| 去包装层（默认关闭） | 仅从各来源根开始，完整清单中恰好一个普通子目录时向下推进；遇同层文件或其他目录就停止 |

类型规则不识别文件内容。筛选模式只保留匹配文件的必要祖先目录；默认全文件模式也保留空目录。整个计划没有文件时返回 `NoMatches`，不创建整理目录，即使清单中存在空目录。有文件的全文件任务同时保留其他空来源目录。

压平不以筛选后的树判断。例如 `外层/资料/a.pdf` 和 `外层/说明.txt` 仅去掉 `外层/`；即便只选 PDF，也保留 `资料/`。只处理来源根连续包装链，不递归压平每个分支、不跨来源边界、不改文件名。`Sources.RemovedPrefix` 与全部映射可检查路径变化。

## 计划与执行

计划内部保存所有来源验证凭据，公开映射保留顶层 SourceId、真实源路径、目标相对路径、类型、长度和摘要。调用方不能构造或编辑计划中的集合。重复执行同一计划不会产生隐式编号目录：第一个成功后，其余执行报告目标占用；应重新预览。

源凭据检查长度、创建/修改时间、SHA-256 和目录直接成员。预览前变化需要重新解压，预览后变化拒绝旧计划；新文件不自动纳入。目录替换为文件、链接、失踪、不可读都会停止。源目录 mtime 可能被子归档合法展开改变，因此目录身份主要由类型和成员关系验证。

冲突按不区分大小写的名称分配，顶层来源永不混入同名目录。已有目标只在预览分配下一名称；执行前或复制中占用返回可操作诊断。最终 `Directory.Move` 负责并发不覆盖仲裁，竞争失败可能表现为输出错误，均无成功目录承诺。

原解压器已拒绝同一归档内的重复路径及大小写别名，因此当前真实 Windows 验收中的命名冲突主要来自不同顶层来源和已有目标。整理仍统一处理目标名称，不能将原来解压失败的冲突条目伪造为成功输入。

规划失败抛 `OrganizationFailureException`；用户取消规划抛 `OperationCanceledException`。执行返回 `Completed / NoMatches / Failed / Cancelled`，只有 `Completed` 带输出目录和新的 `CommittedEntry` 清单。`Error` 为整理专用诊断，不保留原始异常；`CleanupWarning` 是本次暂存残留路径。后续 R06 可直接消费结果清单，本期不自动压缩或执行下一步。

## 扩展边界

当前入口为 `UnpackResult`，包括浏览中“全部解压”承载的真实解压任务。任意目录、单独 `BrowseExtractResult`、移动、覆盖、语义分类、内容去重、自动删除嵌套包、脚本式重命名和 Workflow 仍不在本期。
