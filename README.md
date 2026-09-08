# 压缩包工作台 · Archive Workbench

MyAvaloniaManagement 的压缩与解压插件。提供独立 Headless 类库，以及“解压任务”“压缩任务”两个临时 Document。解压支持指定深度和当前批次候选密码共享；压缩支持默认合成一个 ZIP，或按顶层来源分别打包，按需排除、调整压缩偏好和设置 AES-256 密码。

当前为 **R03 批量打包与加密本地候选**：使用默认参数快速开始，清单和细节按需查看。最新实施与验证边界见[G0009 结果](docs/refactoring/G0009/result.md)，R02、R01 与 V1 历史证据保留在 G0008 / G0007 / G0006。本轮不使用 AIFLOW、Windows CI、Release、正式插件包、部署或发布门禁。

```powershell
./tools/verify-local.ps1
dotnet run --project src/LayerUnpackPlugin.Standalone -c Debug
```

- **深度**：默认一层；开启“继续解压内部压缩包”初次使用总层数 2，可调至 16。顶层为 1，普通目录不计层，`.tar.gz` 等组合归档计一层。
- **解压密码**：一行一个，最多 64 个；保留大小写和空格，成功候选在本批次内优先使用。可以补密并重试可恢复失败项。
- **解压输出**：同目录输入建议输出到旁边的“解压结果”；多来源需选择，手动设置不被自动覆盖。每包独立目录、同名编号，保留源包，取消清理未提交内容。
- **解压格式**：ZIP、7z、RAR4/5、UTF-8 TAR、GZip/BZip2/XZ；具体变体和限制以[实测支持矩阵](docs/refactoring/G0003/format-support-matrix.md)为准。RAR5 加密内容存在校验限制，结果节点会显示提示。
- **状态**：没有 Dock Tool、全局命令、Workflow 注册、历史数据库或密码持久化。
- **压缩**：默认普通 ZIP、标准压缩、UTF-8；更多选项支持分别打包、有限排除、四级偏好和 AES-256。先审阅清单，每组独立暂存和不覆盖提交；失败仅重试允许恢复的组，未完成项可新任务。见[批次契约](docs/refactoring/G0009/batch-creation-contract.md)与[创建矩阵](docs/refactoring/G0009/encryption-and-compression-matrix.md)。加密不隐藏名称，接收方工具须支持此加密 ZIP。

阅读[快速开始](docs/README.md)、[产品文档](docs/product-shape-and-implementation-plan.md)、[V1 执行清单](docs/v1-execution-plan.md)和[阶段档案](docs/refactoring/README.md)。

后续发展见[压缩包工作台路线图](docs/roadmap/README.md)：R01 对应 G0007，R02 对应 G0008，R03 对应 G0009；R04–R08 仍可分别生成详细执行计划。

Plugin ID 保持 `myavalonia.plugin.layer.unpack`，解压 Document ID 保持 `myavalonia.plugin.layer.unpack.document.main`，压缩 Document ID 为 `myavalonia.plugin.layer.unpack.document.pack`。Standalone 用独立 Scope 预览两类真实任务；正式插件包仅由 Plugin Build 生成，后续发布流程见[部署说明](docs/deployment-and-release.md)。
