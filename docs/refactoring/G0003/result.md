# G0003 实施结果：多格式与密码池

日期：2026-09-07。状态：已实施；本地门禁与后续验收按[G0006 结果](../G0006/result.md)区分。

## 实际变更

对应 G0003-01～09：实现 ZIP、7z、RAR4/5、UTF-8 TAR 与 GZip/BZip2/XZ。PasswordPool 规范化候选并优先复用命中；旧 ZIP 编码按请求隔离。上游和自有夹具具备来源与摘要。

## 验证证据

FormatTests、GeneratedFormatTests、DiscoveryAndEdgeTests、IntegrityTests 验证真实内容、错误候选、缺卷、混合 AES、名称和长路径。

统一最终命令、退出码、测试总数、环境及源码摘要见[G0006 结果](../G0006/result.md)。只引用本仓实际运行，不引用 fractal-art 的测试数量；UI/性能/格式的细分证据见[验收矩阵](../G0006/acceptance-matrix.md)。

## 范围偏差与后续

RAR5 加密内容缺少 MAC 验证并显示警告；旧编码 TAR 明确失败。超大 ZIP64、全算法覆盖等不作实测通过声明。

本阶段没有发布安装操作；“已实施”不等于正式包、ALC、Dock 或实机 DPI 已验收。
