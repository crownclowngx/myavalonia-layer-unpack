# G0003 计划：多格式与密码池

日期：2026-09-07。范围：G0003-01～09；状态：实现已完成，实际验证与限制见 [result.md](result.md)。

## 开工问题与依赖

前置：G0002 的单包和写入契约。本阶段依据 [V1 工作项](../../v1-execution-plan.md)推进，不改变既定插件身份。

## 范围与完成标准

实现 ZIP、7z、RAR4/5、UTF-8 TAR 与 GZip/BZip2/XZ。PasswordPool 规范化候选并优先复用命中；旧 ZIP 编码按请求隔离。上游和自有夹具具备来源与摘要。

完成标准：FormatTests、GeneratedFormatTests、DiscoveryAndEdgeTests、IntegrityTests 验证真实内容、错误候选、缺卷、混合 AES、名称和长路径。 对应失败需修正并留下回归，不能只核对文件后缀或编译结果。

## 本轮约束与调整

SOLID 优先，采用窄接口、适配器和显式会话，不建设通用框架。注释中文并说明关键设计思路；代码、测试和专项文档同步。没有 AIFLOW、Windows CI 或发布门禁。

RAR5 加密内容缺少 MAC 验证并显示警告；旧编码 TAR 明确失败。超大 ZIP64、全算法覆盖等不作实测通过声明。

最终方案见 [implementation.md](implementation.md)，验证归档见 [result.md](result.md)。
