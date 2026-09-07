# G0002 计划：Headless 与引擎验证

日期：2026-09-07。范围：G0002-01～10；状态：实现已完成，实际验证与限制见 [result.md](result.md)。

## 开工问题与依赖

前置：G0001 的稳定入口与 Scope。本阶段依据 [V1 工作项](../../v1-execution-plan.md)推进，不改变既定插件身份。

## 范围与完成标准

新增 Headless 和独立测试项目，定义请求、会话、快照、错误与预算。实现流写入、路径验证、输出事务，锁定两个托管解码库并声明自有资产。

完成标准：DomainTests、SafetyTests、IntegrityTests 覆盖无 UI 引用、参数前置校验、输出冲突、CRC/AES 认证故障、回滚与残留诊断。 对应失败需修正并留下回归，不能只核对文件后缀或编译结果。

## 本轮约束与调整

SOLID 优先，采用窄接口、适配器和显式会话，不建设通用框架。注释中文并说明关键设计思路；代码、测试和专项文档同步。没有 AIFLOW、Windows CI 或发布门禁。

G0002-09 的正式目录/ZIP 验证按本轮用户规则改为源码、锁文件和 Debug 依赖核对，打包检查留到发布。

最终方案见 [implementation.md](implementation.md)，验证归档见 [result.md](result.md)。
