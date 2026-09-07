# G0006 本地验收矩阵

日期：2026-09-07。本文区分已执行的本地测试和未执行的原生 / Host 验收；最终数量、命令及结果见 [result.md](result.md)。

## 场景与证据

| 场景 | 本地证据 | 验证边界 |
| --- | --- | --- |
| AC01 ZIP、深度 1 | RecursiveTests 深度 1/2/3，FormatTests ZIP 内容摘要 | 外层提交、子包保留且 DepthLimit，不进入密码尝试 |
| AC02 目录不计层、不同深度停止 | RecursiveTests 多级目录三级嵌套；混合 ZIP/7Z/RAR 子包用例 | 使用等价层级规则与混合格式样本分别验证；没有把“ZIP→7Z→RAR”指定链当作独立实测样本 |
| AC03 同层多分支 | RecursiveTests 深度 2 展开两个分支 | 深度按分支计算，不按全批次解压次数计算 |
| AC04 一个密码跨包跨层 | 加密 AES ZIP 外层、加密 RAR 后代、另一加密 ZIP 均使用公开候选 test | 一个有效候选在三个节点复用；第二次调用隔离由 LifecycleTests 补充 |
| AC05 A/B/C 多密码 | 加密 AES ZIP 外层 A、加密 7Z 后代 B、另一 ZipCrypto 顶层 C | 三个实际密码分别命中；不是只测试明文外层 |
| AC06 耗尽、继续、补密 | RecursiveTests 补密仅重试失败项；DocumentTests 真实 ZipCrypto 重试 | 成功节点输出位置和身份保持，失败尝试也消耗预算 |
| AC07 TAR 组合占一层 | GeneratedFormatTests UTF-8 TAR、GZip/BZip2/XZ 组合；RecursiveTests GZip→ZIP | TAR 组合一层；非 TAR 内层 ZIP 仍增加一层；中间流计入预算 |
| AC08 冲突和去重 | SafetyTests 并发目录提交、大小写冲突；DiscoveryAndEdgeTests 路径去重 | 不覆盖旧输出，只提交本次私有目录 |
| AC09 损坏及输出错误 | IntegrityTests AES 认证尾部损坏、ZIP CRC/方法；SafetyTests、LifecycleTests 写入故障 | 正确映射错误与部分失败；RAR5 加密完整性限制见支持表 |
| AC10 取消和关闭 | PerformanceTests 真实写入取消；DocumentTests 关闭排空；WindowLifecycleTests 实际窗口源代码关闭流程 | 本地 Headless UI 平台验证；真实 Host 退出留待发布 |
| AC11 实例和调用隔离 | CompositionTests 严格 DI/Scope；WindowLifecycleTests 双 Document 并行；LifecycleTests 密码隔离 | 一个 Document 关闭不取消另一个；输出冲突独立处理 |
| AC12 无历史恢复 | 新建 Document 空状态、清空/关闭释放候选、注册检查无持久化接口 | 本地通过；真实 Host 重启尚未执行，不能写成完整环境验收通过 |
| AC13 路径、链接、预算 | SafetyTests 逃逸/保留名/重复条目/TAR 特殊条目；真实 Windows Junction；累计预算与取消测试 | 不承诺抵御同用户进程在检查与写入间恶意替换目录，也不是强制资源沙箱 |
| AC14 同一业务入口 | 独立 Headless 测试进程不引用 Avalonia；DocumentTests 对比同一输入的内容和终态 | Plugin 和 Standalone 共用服务注册、同一 Headless 会话 |

具体测试源文件位于 `tests/LayerUnpackPlugin.Headless.Tests` 和 `tests/LayerUnpackPlugin.Tests`。真实格式均校验文件内容或摘要，夹具来源与公开测试密码见[夹具说明](../../../tests/LayerUnpackPlugin.Headless.Tests/Fixtures/README.md)。

## UI 检查与尚未执行项

已自动化：真实 View 在明暗主题、1180×760、1000×600 及 800×600 渲染；参数双向绑定、错误摘要、树节点展示、Tab 焦点移动；真实 Standalone 窗口源代码的异步初始化与关闭释放。图像位于 `artifacts/ui`。

这些测试使用 Avalonia Headless 平台。另外已实际启动 Debug Standalone，确认窗口标题为“层解 · Layer Unpack · Standalone”、存在原生窗口句柄，发送关闭请求后进程正常退出（退出码 0）；记录为 `artifacts/local-verification/standalone-smoke.json`。没有用图像或启停冒烟代替原生交互验收。G0005-10 尚需实际操作系统窗口检查：

- 文件/目录选择器、从资源管理器拖入文件或目录、多选移除、点击打开输出目录。
- 125%、150%、200% DPI，键盘完整遍历及焦点可见性，中文长路径错误可读性。
- 实际桌面环境下开始、补密、取消和连续关闭窗口的完整人工试用。

当前会话可用的计算机控制接口未提供原生应用操作；自动化可验证部分已完成，以上人工项保留待验收。

## 本轮范围调整

| 工作项 | 当前处理 | 后续条件 |
| --- | --- | --- |
| G0002-09、G0003-09 资产 | 集中依赖、锁文件、私有包与许可证资产声明、Debug 构建核对 | 正式 ZIP 内资产与干净安装检查到发布时执行 |
| G0005-10 | 自动化、渲染与本地回归完成；原生人工项待验收 | 可操作的本地桌面环境 |
| G0006-02 | 按用户要求使用 Debug 零警告本地门禁 | Release 门禁到发布时执行 |
| G0006-05～08 | 未执行正式 ZIP、安装、真实 Host/ALC、Host 重启与退出验证 | 用户启动发布工作时 |
| G0006-10 | 本地源码、测试与文档归档 | 正式包与验收代码一致性、可交付封板到发布时 |

Windows CI、发布、部署与安装均没有执行。当前状态是“V1 本地候选”，不是“已发布”。
