# G0011 / R05 实施方案

## 数据流与职责

`UnpackSession → CommittedManifest → ArchiveNodeResult.CommittedEntries → OrganizationPlanner → OrganizationPlan → OrganizationService → OrganizationResult`

| 组成 | 职责与设计理由 |
| --- | --- |
| `CommittedManifest` | 只枚举当前私有暂存目录，在提交前记录普通文件、目录、长度、时间及 SHA-256；同一组件在整理提交前校验目标清单 |
| `ArchiveNodeResult.CommittedEntries` | 增量兼容属性；null 表示没有契约，空集合表示确认的空归档；只有成功提交才赋值 |
| `OrganizationPlanner` | 验证来源图、去重、选择规则、包装链和稳定冲突名称，生成不可变映射 |
| `OrganizationFiles` | 复用 PathPolicy，检查链接、类型、元数据、内容摘要、目录直接成员及目标占用；转换整理诊断 |
| `IOrganizationService / OrganizationService` | 两个窄用例：预览、执行；无共享可变会话状态，负责超时、验证、复制和结果 |
| `IOrganizationCopier / OrganizationCopier` | 流复制适配；共享读源句柄、CreateNew 目标、租用固定缓冲、同步内容摘要与取消；可注入实际写入后的故障 |
| `OutputTransaction.CommitExact` | 复用目录事务所有权；整理必须按已预览名称提交，占用后不能自动另取名字 |
| `OrganizationDocument / OrganizationView` | 临时子任务负责 UI 编排、100 行分页与代次；View 适配选择器和打开目录 |
| `UnpackDocument.Organization` | 结果上下文入口与子任务所有权；不修改三类顶级 Document 注册 |

解压原有深度、候选密码和不覆盖输出逻辑不变。新增提交清单需要额外读取文件生成摘要，因此运行成本增加；本轮保留原解压全部回归与性能测试，不将既往耗时当作当前证据。

## 来源与预览

只收集已提交节点。通过 ParentId 回溯顶层归档，并核对每一层的源归档在父清单中、子输出在父输出边界内。外层解出的嵌套归档文件与其展开目录分别收录；同一实际路径和相同凭据只处理一次，冲突归属拒绝。目录清单保留空目录，父目录在递归展开后的合法 mtime 变化不视为失败。

预览首先检查完整源清单，再按规则筛选。目录检查只枚举清单中目录的直接成员：新成员立即拒绝，不遍历新成员或输出根中的历史目录。新目录的位置不能等于或位于任一来源根内。

所有来源按固定顺序分配目录名称，大小写不敏感冲突追加 ` (1)` 等编号；文件编号放在扩展名前。现有目标保留，在预览时选定新的 `整理结果 (n)`。执行期间目标被占用必须重新预览，精确映射不变。

## 验证与事务

规划验证一次完整来源。执行前再次验证，逐文件复制时对同一个已打开流检查长度和 SHA-256，所有复制结束后再次验证完整来源，并捕获暂存结果与预览逐项比较。任何选外文件或目录结构变化也会使计划失效，保证去层级依据不改变。

Windows 下 FileShare.Read 在当前文件复制期间阻止写入与替换；路径检查拒绝祖先重解析点。此实现沿用仓库现有路径政策，不声称提供文件系统快照或对有权限恶意改换祖先的进程实现内核级隔离。时间与 SHA-256 是可验证凭据，不能区分元数据和内容都完全相同的文件系统对象替换。

暂存目录与最终目录同父目录。执行检查可用空间，写入阶段仍处理空间耗尽、访问错误和取消。取消检查覆盖验证、枚举、复制与提交；提交前取消回滚，已经提交的目录保留。回滚失败返回所属暂存绝对路径，不隐藏清理残留。

## 生命周期

选择当前解压结果的临时子状态，原因是输入有明确所有者且不需要同一解压页并存多个整理运行。父页隐藏时冻结原操作，整理运行期间禁止返回；不同解压 Document/Scope 可独立执行。返回仅切换显示；新解压、重试与清空释放旧整理子任务。关闭沿父令牌取消，只等待后台任务，不同步等待 UI 续体；迟到回调由代次与关闭标记拦截。

Headless 完全不引用 UI 或 SDK；Standalone 通过原解压 Scope 自动获得整理页面。没有新增 NuGet 包、Host 内部依赖、Tool、Workflow、持久历史或秘密字段。

契约见[整理契约](organization-contract.md)，交互与验收见[交互设计](interaction-design.md)、[验收矩阵](acceptance-matrix.md)。
