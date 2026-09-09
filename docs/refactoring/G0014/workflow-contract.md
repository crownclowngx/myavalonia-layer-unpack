# G0014 Workflow v1 契约

## 动作、参数与版本

动作 ID 为 `myavalonia.plugin.layer.unpack.workflow.unpack-v1`、`myavalonia.plugin.layer.unpack.workflow.create-v1`。输入对象全部字段必需且禁止未知字段、重复字段。结构由 Workflow SDK 1.0.0 验证；业务路径、格式组合和当前来源可用性仍由 Headless 检查。

| 字段 | 解压 | 创建 |
| --- | --- | --- |
| version | 固定 1 | 固定 1 |
| inputs | 1–64 个绝对来源文件路径 | 0–64 个明确文件／文件夹路径；空清单正常跳过 |
| outputDirectory | 绝对输出目录 | 绝对输出目录 |
| repeatPolicy | 必须为 `create-new` | 必须为 `create-new` |
| maxDepth | 总层数 1–16；默认业务仍为一层，工作流定义必须明确写出 | 不适用 |
| nameEncoding | `gb18030`、`utf8`、`cp437`、`cp866` | 不适用 |
| archiveName | 不适用 | 最多 255 字符的单一文件名，扩展名须匹配格式；分别模式由规划器命名 |
| grouping | 不适用 | `combined`、`separate` |
| format | 遵循现有读取能力 | `zip`、`tar`、`tar.gz` |
| compression | 不适用 | ZIP 四级；TAR 仅 standard；TAR.GZ 为 standard/fast/high；其余组合业务拒绝 |

路径字符串 Schema 上限 32767 字符且仍受协议 UTF-8 字节预算限制；不是任意长度路径在操作系统均可用的承诺。资源使用 Headless 默认有限预算，Workflow 额外限制输入、解压总节点为 64，适配 Studio 有限 ForEach。不能在节点中随意调大预算。GUI 同输入在此范围内的内容、层数、分组和不覆盖语义等价。

新增字段即使看似兼容，也会改变目录 contract revision，Studio 必须重新验证。已有 ID 的字段含义、状态和枚举不修改；破坏性变化新增 v2 Action ID。不注册枚举、整理、检查、重打包或 7z 创建占位动作。

## 输出和诊断

根字段：`contract: "myavalonia.layer-unpack.workflow-result"`、`version: 1`、宿主 `invocationId`、`state`、`diagnosticCode`、`detailsTruncated`、`counts`、`successfulOutputs`、`items`。

- state：`completed`、`partial-failure`、`failed`、`skipped`。SDK 调用 Succeeded 只表示得到合法结果；业务成功必须检查 state。Studio 配套适配报告非成功业务摘要。
- counts：全量 completed、failed、skipped、notRun 计数；父包已提交但后续发现失败时，节点可同时保留提交事实与 diagnosticCode，批次仍为部分失败。
- successfulOutputs：仅提交成功项，每项 `{ id, path, kind }`；kind 为 directory 或 archive。下游用 ForEach 遍历该数组，通过 `${item.path}` 读取。失败、未运行和计划目标从不进入此数组。
- items：`id`、`parentId`、`sourcePath`、`depth`、`state`、`diagnosticCode`、`cleanupRequired`。解压根深度 1，创建组深度 0；创建 combined 来源标签为“全部输入”，来源清单保留在本次调用参数中；separate 为组来源路径。无父项时 parentId 为空串。
- 节点状态：completed、failed、skipped、not-run；正常深度停止的诊断为 `depth-limit`。空输入根诊断为 `no-inputs`。
- 非法 JSON 为 `invalid-arguments`，业务请求无效为 `invalid-request`；实际错误沿用 Headless 稳定枚举，如 InputUnavailable、InputChanged、UnsafePath、BudgetExceeded、Timeout、PasswordRequiredOrInvalid。不要根据本地化引擎文字分支。
- cleanupRequired 只表示存在自有临时残留，不把随机暂存路径公开成稳定 API。下游失败不自动删除已提交成果。

输出整体最多 1 MiB，并遵守单字符串、数组等共享限制。极端长路径使明细过大时，按原有顺序保留完整项及其对应成功产物，counts 仍表示全量事实；`detailsTruncated=true`、根 `diagnosticCode=result-budget-exceeded`，state 为 partial-failure。未提供的成功项不能猜测；应缩小本次输入和输出路径再核对，不能把显式子集当作整批交付。父子关系只保证原始 ID 稳定于本次结果，不用于跨次恢复。

## 取消、秘密与重跑

取消不作为普通成功输出返回：先等待 Headless 停止和临时清理，再抛协作取消异常，由 Gateway 返回 Cancelled/TimedOut 等终态。SDK 非成功调用没有 Output，因此取消前已经提交的文件仍保留，但消费者不能从取消结果自动恢复成功清单；应检查本次输出目录，不自动重跑整批。

没有密码参数、候选数组、加密开关或秘密引用。未知字段会在 Host Schema 边界被拒绝，直接调用 Handler 也复验；只传稳定错误码和固定进度，不回显普通输入正文。没有共享候选池、弹窗补密或无限等待。加密 Workflow 为后续独立子范围。

`create-new` 是显式非幂等策略：每次独立调用重新冻结输入、创建新会话，命名冲突按同名编号处理，最终 Move 不覆盖仲裁并发。重试成功项也会生成第二份新交付，故调度器应关闭自动重试；若要恢复，仅显式选定失败来源并接受新建政策。不采用 invocationId 作为可跨次幂等键，不保存隐藏任务台账，不借用 GUI 原批次补密重试状态。
