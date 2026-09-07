# G0008 实施结果：R02 基础 ZIP 压缩

日期：2026-09-07。状态：G0008-01–05 实现完成，完整本地门禁通过；部分原生人工待验收，发布门禁未执行。

## 已实施内容

新增 Headless 创建契约、可检查清单、来源摘要、普通 ZIP 写入、输出预算和文件事务；新增简洁压缩 Document/View、建议值保护、失败恢复、取消与关闭排空；插件登记两类普通任务，Standalone 提供独立 Scope 的双标签预览。

沿用已安装 .NET 10 和现有依赖，不新增库、锁文件、Tool、Workflow、历史存储或发布动作。保持 Plugin / 解压 Document 身份，新增压缩 Document 身份。中文注释说明规划、输出、取消与 UI 职责，专项见[实施方案](implementation.md)、[创建契约](creation-contract.md)、[互操作矩阵](format-support-matrix.md)、[交互专项](interaction-design.md)和[验收矩阵](acceptance-matrix.md)。

## 验证记录

在仓库根运行 `./tools/verify-local.ps1`，最终源码通过以下检查：

| 检查 | 实际结果 |
| --- | --- |
| 锁定依赖恢复 | `dotnet restore LayerUnpackPlugin.slnx --locked-mode` 通过 |
| Debug 构建 | `dotnet build LayerUnpackPlugin.slnx -c Debug -warnaserror --no-restore`，零警告、零错误 |
| Debug 引用资产 | 解析真实 MSBuild 引用项，Headless、两个解压引擎与许可证归属检查通过 |
| Headless 测试 | 135 通过，0 失败，0 跳过；较 G0007 新增 27 项 |
| Plugin / UI 测试 | 37 通过，0 失败，0 跳过；较 G0007 新增 12 项 |
| 代码格式 | `dotnet format LayerUnpackPlugin.slnx --verify-no-changes --no-restore` 通过 |
| Markdown 路径 | `./tools/check-docs.ps1`，59 份文档通过；收尾文档修改后再次检查 |

合计 **172 项测试全部通过**，保留原 133 项回归。测试门禁分别读取两个项目的 TRX，要求非零且无失败、无跳过。

验证环境为 Windows x64（系统版本 10.0.26200.0）、.NET SDK 10.0.302、Avalonia 12.1.0、Plugin SDK 3.3.0。基线提交为 `c7ddcb05ff5e9c89c1d550640a3bf832e9b31791`，本轮验证包含尚未提交的实现。111 个源码、配置和夹具文件按路径排序后的 SHA-256 为 `a201412cd430f099c6ff0e4898d03c641a0904024400c159126368735d5c6421`；算法和实际命令见[验证摘要](verification-summary.json)。摘要排除文档、README 和构建产物，文档收尾不改变该源码标识。

本地日志、TRX 与逐文件摘要复制到 `artifacts/G0008/local-verification/`；它们受 Git 忽略规则管理，仓库内保留可复跑命令和结构化摘要，不宣称存在正式插件包。

## 真实格式、界面与性能证据

- 真实 ZIP 由 SharpZipLib 独立读取器执行 `TestArchive(true)`、条目清单和逐文件 SHA-256 核对，并经已有 UnpackService 回读。覆盖中文、空文件、空目录、根映射与源内输出；故障和生命周期替身不代替格式证据。
- 保留真实写入、中央目录收尾取消、同长度同时间戳的内容变更、清单变化、读取占用、输出阻塞、并发不覆盖和链接拒绝等回归。创建资源限制与取消分别覆盖准备和执行路径。
- 900×760、640×520，深浅两种主题，各有空、就绪、完成三个状态，共 12 张自动渲染图，位于 `artifacts/ui/G0008/`。实际查看深浅就绪、明亮完成与暗色窄窗口完成图；绑定、Tab 导航、开始及打开产物按钮位置有断言。
- 2,001 文件样本含超过 260 字符来源路径，源内容 8,000 字节、ZIP 219,248 字节；本次准备、写入及独立内容检查累计 3,608 ms。原始数据位于 `artifacts/performance/G0008/small-files.json`，并保留于验证摘要。这是单次 Debug 小文件样本，不是性能承诺，也不证明大文件 Zip64 或峰值内存。

实施复核修正了窄窗口完成后产物入口被表单挤到下方的问题；结果区域已前移并固定底部操作。还修正了成功后尝试非法新名称仍保留旧产物按钮的问题，增加“失败时无旧产物入口”断言后重新运行全部本地门禁。最终结果以本节的最后一次运行记录为准。

## 范围与遗留

仅普通 ZIP 全部合包，无加密、分别打包和其他创建格式。相对路线图，源目录内部的新输出子目录采取明确限制：需预先存在，或改选其他位置；源外新输出目录支持执行时创建。原因是准备必须只读，执行产生的新目录不能无说明地改变已审阅的源清单。来源持续变化可能导致 InputChanged；时间、元数据及超大 Zip64 证据边界见[创建矩阵](format-support-matrix.md)。

真实原生文件／文件夹选择成功、拖放、多 DPI、资源管理器打开产物和外部工具人工互操作未执行，保留为人工验收项，不以自动渲染替代。未使用 AIFLOW、Windows CI、Release、正式插件 ZIP、部署、安装或发布门禁；真实 Host 验收保持发布阶段归属。

R02 状态为“已实施／本地验证通过，部分原生交互待验收”，不标记全环境封板。R03 可基于本阶段创建契约继续生成详细计划，并继承上述边界与待验收项。
