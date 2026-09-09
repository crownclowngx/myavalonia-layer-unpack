# 压缩包工作台 · Archive Workbench

MyAvaloniaManagement 的压缩包工作台。提供独立 Headless 类库，以及“解压任务”“压缩任务”“浏览任务”三个临时 Document。解压支持指定深度和当前批次候选密码共享；压缩支持默认合成一个 ZIP，或按顶层来源分别打包，按需排除、调整压缩偏好和设置 AES-256 密码；浏览支持先查看 ZIP，再提取所需文件或目录。

当前为 **R08 无密码 Workflow 动作本地候选**：新增解压、创建两个动作及真实 Fractal → 归档流程，Studio 如实显示业务失败。现有 R07 矩阵、检查与 TAR/TAR.GZ 保留，7z 创建未开放。最新边界见[G0014 结果](docs/refactoring/G0014/result.md)，历史 G0001–G0013 证据保持。本轮不使用 AIFLOW、Windows CI、Release、正式插件包、部署或发布门禁。

```powershell
./tools/verify-local.ps1
dotnet run --project src/LayerUnpackPlugin.Standalone -c Debug
```

- **深度**：默认一层；开启“继续解压内部压缩包”初次使用总层数 2，可调至 16。顶层为 1，普通目录不计层，`.tar.gz` 等组合归档计一层。
- **解压密码**：一行一个，最多 64 个；保留大小写和空格，成功候选在本批次内优先使用。可以补密并重试可恢复失败项。
- **解压输出**：同目录输入建议输出到旁边的“解压结果”；多来源需选择，手动设置不被自动覆盖。每包独立目录、同名编号，保留源包，取消清理未提交内容。
- **解压格式**：ZIP、7z、RAR4/5、UTF-8 TAR、GZip/BZip2/XZ；具体变体和限制以[实测支持矩阵](docs/refactoring/G0003/format-support-matrix.md)为准。RAR5 加密内容存在校验限制，结果节点会显示提示。
- **状态**：注册两个无密码 Workflow Provider；没有 Dock Tool、全局命令、Gateway、历史数据库或密码持久化。
- **浏览**：单卷 ZIP/ZIP64 目录、200 行分页和路径搜索，目录勾选包含全部后代，搜索不改变选择；重复名称按条目身份区分。提取默认一层、保留相对路径，原包变化须重新加载，密码每次明确输入。完整源摘要读取计入预算，其他格式使用“全部解压…”进入原解压页。见[浏览契约](docs/refactoring/G0010/browsing-contract.md)和[独立能力矩阵](docs/refactoring/G0010/format-support-matrix.md)。
- **压缩**：默认普通 ZIP、标准压缩、UTF-8；更多选项支持分别打包、有限排除、四级偏好和 AES-256。先审阅清单，每组独立暂存和不覆盖提交；失败仅重试允许恢复的组，未完成项可新任务。见[批次契约](docs/refactoring/G0009/batch-creation-contract.md)与[创建矩阵](docs/refactoring/G0009/encryption-and-compression-matrix.md)。加密不隐藏名称，接收方工具须支持此加密 ZIP。

阅读[快速开始](docs/README.md)、[产品文档](docs/product-shape-and-implementation-plan.md)、[V1 执行清单](docs/v1-execution-plan.md)和[阶段档案](docs/refactoring/README.md)。

解压成功后点击“整理结果”，核对新目录位置，按需展开选择 PDF、图片或去包装层，然后“生成预览”→“开始整理”→“打开整理目录”。默认全部文件、不压平，类型按扩展名筛选，原包与原解压内容保留。来源变化或目标被占用需重新检查与预览；详情见[整理契约](docs/refactoring/G0011/organization-contract.md)。

解压输入处点击“转换为 ZIP…”：默认保留内部包字节，每来源一个新 ZIP；按需显式选择展开层数和整理规则。解压或整理结果处点击“打包结果”，检查提交清单后开始，可合成或分别。来源与目标密码独立，目标默认不加密；每个 ZIP 回读核对后独立提交，来源校验限制保留。见[转换契约](docs/refactoring/G0012/conversion-contract.md)和[保留矩阵](docs/refactoring/G0012/preservation-matrix.md)。

后续发展见[压缩包工作台路线图](docs/roadmap/README.md)：R01 对应 G0007，R02 对应 G0008，R03 对应 G0009，R04 对应 G0010，R05 对应 G0011，R06 对应 G0012，R07 的矩阵、检查与 TAR 创建对应 G0013；R08 最小无密码动作与流程对应 G0014，7z 创建和加密 Workflow 仍为后续目标。

Plugin ID 保持 `myavalonia.plugin.layer.unpack`，解压 Document ID 保持 `myavalonia.plugin.layer.unpack.document.main`，压缩 Document ID 为 `myavalonia.plugin.layer.unpack.document.pack`，浏览 Document ID 为 `myavalonia.plugin.layer.unpack.document.browse`。Standalone 用独立 Scope 预览三类真实任务；正式插件包仅由 Plugin Build 生成，后续发布流程见[部署说明](docs/deployment-and-release.md)。

解压或浏览页点击“检查压缩包…”：默认完整内容检查，密码本次输入；仅目录检查只支持 ZIP。结果明确列出实际校验和限制，结束后清理临时内容。压缩页可选择 TAR/TAR.GZ，默认 ZIP；新格式不支持密码，转换与结果打包仍输出 ZIP。详见[检查契约](docs/refactoring/G0013/checking-contract.md)与[按操作矩阵](docs/refactoring/G0013/format-support-matrix.md)。
