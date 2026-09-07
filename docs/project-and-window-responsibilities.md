# 工程职责与 SOLID 约束

V1 的依赖方向是 `Plugin → Headless`、`Standalone → Plugin → Headless`；Headless 测试只引用 Headless。生产代码不引用 Host 内部实现。

| 组成 | 单一职责与依赖 |
| --- | --- |
| `UnpackRequest / UnpackResult` | 不可变参数和结果；密码不参与 JSON；重试资格作为公共结果规则 |
| `PasswordPool` | 候选规范化、稳定顺序、成功候选复用，不认识 UI 或文件 |
| `ExecutionBudget` | 一个批次的累计资源账本，失败和重试不退款 |
| `UnpackService / UnpackSession` | 创建隔离会话；串行调度、深度、取消、重试和结果汇总 |
| `IArchiveExtractor` | 窄单包端口：解一个逻辑包到指定目录，遵守预算、取消和脱敏约定 |
| `ArchiveExtractor / ZipArchiveExtractor` | 适配已有解码器；ZIP 的认证交给已验证的 SharpZipLib |
| `StreamCopy` | 固定缓冲区、实际写入预算、增量 CRC |
| `PathPolicy / OutputTransaction` | 校验可写路径；拥有单包临时目录并以不覆盖移动提交 |
| `InputDiscovery` | 用户输入目录扫描，区别于递归解压中的后代发现 |
| `UnpackDocument` | 命令、参数快照、结果投影、UI 代次和关闭衔接 |
| `UnpackView` | 编译绑定、文件/目录选择、拖放与用户触发的打开输出 |
| `StandaloneServices / MainWindow` | 复用插件组合入口；创建预览 Scope，异步初始化，关闭时排空工作 |

S：业务、格式、路径、事务和界面分责。O：新增格式优先扩展适配层，不改变密码和递归调度。L：测试替身也遵守单包端口的预算、取消和输出契约。I：只设会话创建与单包解码两个窄端口，不建设万能上下文。D：会话依赖 `IArchiveExtractor`，Document 依赖 `IUnpackService` 和公开 `IDocumentLifetime`。

仅使用简单的适配器、会话和输出事务。没有动态插件引擎注册中心、通用 Workflow 框架、全局服务定位器或密码单例。源代码注释使用中文解释边界和设计原因。

## Scope 与生命周期

Module 只注册一个普通 Document，不保存注册对象。业务服务和 Document 按 Scope 创建；格式适配器是无状态 singleton；密码、节点和预算属于各自会话。

Standalone 在窗口构造阶段只创建对象，Opened 后观察异步初始化。首次关闭取消生命周期，等待 Headless 和扫描后台任务排空，再释放 Scope 并真正关闭窗口。Host 的同步 Scope 释放路径也受支持：只同步等待不依赖 UI 的核心任务，排队的 UI 续体观察关闭标记后退出。

Avalonia 12 的 Headless.XUnit 使用 xUnit v3；两个测试项目统一为 3.2.2。窗口测试链接 Standalone 的实际组合/窗口/生命周期源码和 AXAML，不将 Desktop 平台程序集引入测试发现进程。它验证真实窗口代码在 Headless 平台下的行为，不等同于操作系统窗口或真实 Host 验收。

## 资产归属

正式入口只有 `.Plugin`，自有运行时为 `.Plugin.dll`、`.Headless.dll`、`SharpCompress.dll` 和 `ICSharpCode.SharpZipLib.dll`，并携带对应许可。SDK、Avalonia、CommunityToolkit 与 `Microsoft.Extensions.*` 由 Host 提供。Standalone、测试和夹具不进入插件资产。

Headless DLL 在引用解析后显式纳入 ManagedPluginAsset，两个引擎通过 ManagedPluginPrivatePackage 声明。Debug 资产门禁对 MSBuild 实际求值结果和文件存在性作检查；干净插件目录、ZIP、ALC 与真实 Host 在发布阶段验证，见[部署说明](deployment-and-release.md)。
