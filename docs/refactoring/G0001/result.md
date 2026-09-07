# G0001 实施结果：产品壳与身份

日期：2026-09-07。状态：已实施；本地门禁与后续验收按[G0006 结果](../G0006/result.md)区分。

## 实际变更

对应 G0001-01～07：移除 MainDocument、示例工作台消息和对应身份，改为 UnpackDocument/UnpackView。Plugin ID 与 Document ID 沿用初始化值，Module 只 AddDocument。Standalone 使用独立 Scope 和异步初始化。

## 验证证据

CompositionTests 验证唯一普通 Document、零其他贡献与严格 Scope；WindowLifecycleTests 验证实际窗口源码的关闭流程。另外实际启动 Debug Standalone，读取正确窗口标题及原生句柄，发出关闭请求后退出码为 0；它属于启停冒烟，不替代人工交互。

统一最终命令、退出码、测试总数、环境及源码摘要见[G0006 结果](../G0006/result.md)。只引用本仓实际运行，不引用 fractal-art 的测试数量；UI/性能/格式的细分证据见[验收矩阵](../G0006/acceptance-matrix.md)。

## 范围偏差与后续

原生窗口交互和真实 Host 加载不能由 Headless 平台测试代替。

本阶段没有发布安装操作；“已实施”不等于正式包、ALC、Dock 或实机 DPI 已验收。
