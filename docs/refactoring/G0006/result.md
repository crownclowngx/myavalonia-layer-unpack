# G0006 实施结果：V1 本地候选

日期：2026-09-07，最终记录 15:41（UTC+08:00）。状态：V1 本地候选已实施，121 个测试与统一门禁通过；原生人工验收及发布阶段事项另列。

## 实际交付

已完成 Headless 多格式批量递归、跨层跨包密码、失败重试、输出事务和有限预算；Plugin 提供单一 Document，Standalone 共用服务注册并按 Scope 关闭。没有 Tool、历史存储、AIFLOW、Windows CI 或发布流程变更。

G0001–G0006 均建立计划、最终方案、实际结果与适用专项文档；原 58 项中 53 项按本轮调整后的范围完成。G0005-10 的原生人工部分待验收，G0006-05～08 按用户要求留到发布。功能边界见[验收矩阵](acceptance-matrix.md)、[格式支持矩阵](../G0003/format-support-matrix.md)和[性能基线](performance-baseline.md)。

## 验证记录

入口：`./tools/verify-local.ps1`，最终退出码 0。脚本遇失败即停止，核查 TRX 非零测试、全部通过且无跳过。最终实际记录如下：

| 检查 | 结果 | 原始证据 |
| --- | --- | --- |
| locked restore | 退出码 0，五工程依赖精确锁定 | locked-restore.log |
| Debug build -warnaserror | 退出码 0，零警告、零错误 | debug-build.log |
| Debug MSBuild 资产求值 | Headless DLL、两个私有引擎和两份许可的声明/文件通过 | debug-assets.json |
| Headless 测试 | 108 通过，0 失败，0 跳过 | headless.trx、headless-tests.log |
| Plugin/UI 测试 | 13 通过，0 失败，0 跳过 | plugin.trx、plugin-tests.log |
| dotnet format --verify-no-changes | 退出码 0 | format.log |
| Markdown 路径检查 | 36 份文档通过 | docs.log |
| 原生 Standalone 启停 | 正确窗口标题、原生句柄；发出关闭请求，正常退出码 0 | standalone-smoke.json、标准输出/错误日志 |

上述证据位于 `artifacts/local-verification`。实际命令、各检查耗时、性能与启停记录已保存为 [verification-summary.json](verification-summary.json)。两类测试合计 121；四项性能测试包含在 Headless 数量内，不重复相加。UI 渲染四种主题/尺寸组合，已查看明亮 1180×760 和暗色 800×600 图像；全部图像位于 `artifacts/ui`。

环境：Windows 11 家庭中文版 x64 / 10.0.26200，.NET SDK 10.0.302、runtime 10.0.10；Avalonia 12.1.0，Plugin SDK 3.3.0、Build 1.1.2；SharpCompress 0.50.4、SharpZipLib 1.4.2。硬件与计量边界见[性能基线](performance-baseline.md)。

## 候选源码标识

本轮门禁执行时仓库尚无 Git 提交，因此记录以下 95 个源码、配置、测试夹具和工具文件的集合摘要：

`db335f25b956a3bf3904e4add56cec0b9d9cb2c3ba0d17af28bf81e51def5dfa`

单文件清单为 `artifacts/local-verification/source-manifest.json`。范围是 `rg --files src tests tools`（遵循忽略规则），再加入根目录 `.gitignore`、`Directory.Build.props`、`Directory.Packages.props`、`LayerUnpackPlugin.slnx`；按相对路径 ordinal 排序，路径统一使用斜线，每行“文件 SHA-256 小写 + 两个空格 + 相对路径 + LF”，对 UTF-8 无 BOM 字节再次计算 SHA-256。

不包含 docs、根 README 或生成目录；最终门禁后仅回填文档记录。此摘要不是正式包摘要；该门禁运行没有执行 Git 提交或发布。

## 关键修正与限制

- ZIP AES 认证损坏回归发现原引擎未校验认证尾部，ZIP 改由 SharpZipLib 验证；保留真实损坏样本回归。
- Build 1.1.2 不自动收集项目引用，已显式声明 Headless DLL 并增加 Debug 资产求值检查；发现问题后完整重跑本地门禁通过，没有执行部署目标。
- 传统编码 TAR 明确拒绝无法可靠解码的名称；中文正向样本采用 UTF-8 TAR。ZIP 支持请求级旧名称编码选项。
- RAR5 加密样本内容输出通过，但当前库未验证加密 MAC，结果中给出可见警告，不能宣称已完成该完整性校验。
- 原生选择器、拖放、DPI 人工检查仍待验收；发布 ZIP、安装、真实 Host/ALC、重启退出门禁遵照用户指令留到发布阶段。
- 进程内取消和预算是协作机制，不能代替强制资源隔离。

后续 Git 提交保存本次源码与文档。验证快照中的 `gitCommit: null` 表示门禁当时没有提交，不代表仓库永远没有提交；正式候选包和发布状态仍未生成。
