# Layer Unpack · 层解

MyAvaloniaManagement 的批量递归解压插件。提供独立 Headless 类库和一个临时任务 Document，支持指定深度，以及候选密码在同批次的多个压缩包和嵌套层之间共享。

当前为 **V1 本地候选 + R01 简洁交互**：界面已调整为单列、默认设置与按需详情。最新验证及原生交互边界见[G0007 结果](docs/refactoring/G0007/result.md)，原 V1 证据保留在[G0006](docs/refactoring/G0006/result.md)。本轮按用户要求不使用 AIFLOW、不新增 Windows CI、不执行 Release、正式 ZIP、部署或发布门禁。

```powershell
./tools/verify-local.ps1
dotnet run --project src/LayerUnpackPlugin.Standalone -c Debug
```

- **深度**：默认一层；开启“继续解压内部压缩包”初次使用总层数 2，可调至 16。顶层为 1，普通目录不计层，`.tar.gz` 等组合归档计一层。
- **密码**：一行一个，最多 64 个；保留大小写和空格，成功候选在本批次内优先使用。可以补密并重试可恢复失败项。
- **输出**：同目录输入建议输出到旁边的“解压结果”；多来源需选择，手动设置不被自动覆盖。每包独立目录、同名编号，保留源包，取消清理未提交内容。
- **格式**：ZIP、7z、RAR4/5、UTF-8 TAR、GZip/BZip2/XZ；具体变体和限制以[实测支持矩阵](docs/refactoring/G0003/format-support-matrix.md)为准。RAR5 加密内容存在校验限制，结果节点会显示提示。
- **状态**：没有 Dock Tool、全局命令、Workflow 注册、历史数据库或密码持久化。

阅读[快速开始](docs/README.md)、[产品文档](docs/product-shape-and-implementation-plan.md)、[V1 执行清单](docs/v1-execution-plan.md)和[阶段档案](docs/refactoring/README.md)。

后续发展见[压缩包工作台路线图](docs/roadmap/README.md)：R01 对应 G0007，R02–R08 的压缩、浏览、整理、重打包和工作流能力仍为规划，可分别生成详细执行计划。

Plugin ID 保持 `myavalonia.plugin.layer.unpack`，Document ID 保持 `myavalonia.plugin.layer.unpack.document.main`。Standalone 只预览同一份界面和业务；正式包仅由 Plugin Build 生成，后续发布流程见[部署说明](docs/deployment-and-release.md)。
