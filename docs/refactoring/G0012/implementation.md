# G0012 实施方案

## 职责与设计理由

`IRepackService / RepackService` 编排有限转换和结果打包。它无可变会话字段，每次调用持有独立的账本、取消令牌、密码候选与工作区。来源、规则、映射和产物是不同契约，界面不直接串联文件系统操作。

`CommittedPackPlanner` 将 `OrganizationPlanner` 的来源映射适配为 `PackPlan`。整理仍负责来源归属、包装链、筛选和冲突；适配器只决定 ZIP 边界和包内路径。合成解压结果带来源目录，分别模式用 ZIP 名字表达来源；已整理目录合成时不会再套一层“整理结果”。

`IRepackWriter / RepackWriter` 是窄写入端口，调用原 `PackService` 的内部执行路径。复用 `IArchiveWriter`、AES/普通 ZIP 写入、来源摘要和不覆盖文件事务。`IPackService` 的公开签名保持兼容。

`RepackVerifier` 在目标关闭写句柄后回读目录和正文，以预期条目名称、目录标志、长度、加密标志及 SHA-256 验证；零字节文件同样完整读取。回读只使用缓冲，不另写展开文件。生产普通 ZIP 使用 .NET 写入、SharpZipLib 回读；验收另用 .NET 或已有解压器独立核对。

`RepackBudget` 计量整个调用。原 `ExecutionBudget` 增加可选累计写入扣额回调，转换内的 `UnpackSession` 共用同一个账本。普通转换关闭子包发现；原公开解压会话行为不变。预算不依赖 UI 进度回调，不因回滚或重试退款。

`RepackDocument / RepackView` 是解压或整理页面拥有的临时子任务，继承父关闭令牌，自己持有运行状态。关闭只等待无 UI 依赖的后台工作，不等待命令的 UI 续体；同步 Scope 释放不会因 UI 续体造成死锁。世代号屏蔽迟到进度，所有秘密字段不序列化。

这些都是直接的端口、适配器、规划器与事务职责，没有增加服务定位器、反射流程引擎或设计模式框架。

## 数据流

普通转换：冻结来源与模式 → 建立本来源私有工作区 → 复用一层解压 → 从提交清单生成精确路径 → 暂存 ZIP → 验证来源与回读 ZIP → 独立提交 → 清理工作区。

展开整理：同一流程中使用现有递归会话、可见总层数和 R05 规则生成预期清单。整理阶段只改变映射，因此没有整理目录复制成本。原包内归档仍在已展开结果中；类型筛选决定是否带入它们。

现有结果打包：冻结 `UnpackResult` 或 `OrganizationResult` → 只读核对清单／摘要 → 可检查的 `RepackPlan` → 用户开始 → 再验证来源 → ZIP 暂存／回读／提交。整理结果新增 `SourceGroups` 和 `SourceWarnings`；老结果没有来源边界时只能合成，不能猜测分别分组。

## 共享安全修正

新增 `ArchiveEntryPolicy` 识别读取器公开的 ZIP 风格 Unix 类型位，只接受普通文件和目录；链接、FIFO、设备、套接字和不一致类型报告失败。解压和 ZIP 浏览提取共用判断，避免 R06 转换把特殊对象静默变成普通空文件。TAR 继续使用既有的明确条目类型判断。

目标不会替换原归档。同名目标沿用文件事务自动编号，最终 `File.Move(..., overwrite: false)` 仲裁并发。转换工作区由外层 `OutputTransaction` 拥有，其内层解压提交只属于中间内容；外层始终回滚工作区，不回滚位于外部的已提交 ZIP。

实现位置：[Headless 用例](../../../src/LayerUnpackPlugin.Headless/Application/RepackService.cs)、[清单适配](../../../src/LayerUnpackPlugin.Headless/Application/CommittedPackPlanner.cs)、[界面](../../../src/LayerUnpackPlugin.Plugin/Features/Repack/RepackDocument.cs)。
