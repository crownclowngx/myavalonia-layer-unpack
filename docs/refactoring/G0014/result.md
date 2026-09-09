# G0014 / R08 实际结果

日期：2026-09-09。R08 最小两个无密码动作与真实 Fractal 流程已实施，本地门禁通过。环境：Windows x64、.NET SDK 10.0.302、Avalonia 12.1.0、公开 Core/UI SDK 3.3.0 与 Workflow SDK 1.0.0。

已实现两个版本化动作、共享 Schema 校验、成功清单与来源关系、部分失败和缺密诊断、独立生命周期与显式新建重跑政策。Studio 增加公开归档 v1 业务结果适配。普通三个任务页没有工作流控件。

依赖核查发现全局 Workflow SDK 1.0.0 缓存来源为旧的 Studio 本地 feed，与 Studio 既有锁定哈希不一致。独立目录从 NuGet.org 严格还原 Studio 原锁文件成功；本轮归档锁文件使用同一官方包，不修改全局缓存、不放宽锁定校验。验证脚本据此固定本仓库独立包缓存和官方源。

声明范围为两个无密码动作和真实本地 SDK/ALC 流程；未交付加密秘密通道、可选更多动作、CLI/MCP、通用补偿或幂等任务库。真实 Host 授权窗口、安装、原生退出及发布仍待对应阶段验证。本轮不使用 AIFLOW、Windows CI、Release、正式 ZIP、部署或发布门禁。

专项：[方案](implementation.md) · [契约](workflow-contract.md) · [流程](integration-example.md) · [验收](acceptance-matrix.md)。

## 实际验证

执行 `./tools/verify-local.ps1`，包括 NuGet.org 隔离缓存 locked restore、Debug 零警告构建、引用资产归属、四组 TRX、ZIP/TAR/TAR.GZ 独立工具互操作、dotnet format、Markdown 链接及 git diff --check。全部通过。Studio 本轮三个源文件的格式验证在脚本追加该项后单独运行并记录，不重复执行已通过的业务测试。

| 测试组 | 通过 | 失败 | 跳过 |
| --- | --- | --- | --- |
| Headless | 379 | 0 | 0 |
| Plugin/UI（含新增 Workflow 单元测试） | 127 | 0 | 0 |
| 三 ALC 真实插件与 Studio 集成 | 5 | 0 | 0 |
| Studio 全量回归 | 86 | 0 | 0 |
| 总计 | 597 | 0 | 0 |

本仓库三组共 511 项，配套 Studio 86 项。本轮相对原归档基线增加 29 项单元/界面用例、5 项集成用例，Studio 增加 13 项。实际 GUI 与 Workflow 同时处理真实加密包证明密码不串用，关闭 GUI 后独立动作仍可运行；Run 释放通过跨 ALC 取消真实解压并等待暂存清理。Fractal 真实 PNG → ZIP → 解压后的 SHA-256 相同，正常路径及故障补偿只经公开 Release 释放临时来源。

原始证据位于 `artifacts/local-verification/`：`checks.json`、四份 TRX、各步日志和互操作摘要；可提交快照见[机器摘要](verification-summary.json)。文件夹为本地忽略制品，不是发布资产。最终仅同步证据文档后重跑链接和空白检查，不把历史摘要覆盖为本期证据。

## 改动边界

生产变更集中在归档插件的 SDK 适配与注册，以及相邻 `myavalonia-workflow-studio` 的最小业务结果识别。Fractal 和宿主源码没有修改。归档 Plugin 只新增官方 Workflow SDK 依赖；还原时修正测试锁文件中已不在当前项目图中的历史 Standalone 依赖记录，没有升级现有运行包。

7z 创建、加密 Workflow、更多候选动作、CLI/MCP 均不标成完成。真实宿主原生退出与确认窗口未执行；本地 Run 取消测试不能代替原生验收。通用定义没有 finally 节点，异常／取消后的生产者临时文件仍按[流程所有权](integration-example.md)处理。
