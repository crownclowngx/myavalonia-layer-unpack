# G0001 计划：产品壳与身份

日期：2026-09-07。范围：G0001-01～07；状态：实现已完成，实际验证与限制见 [result.md](result.md)。

## 开工问题与依赖

前置：无，沿用用户初始化工程。本阶段依据 [V1 工作项](../../v1-execution-plan.md)推进，不改变既定插件身份。

## 范围与完成标准

移除 MainDocument、示例工作台消息和对应身份，改为 UnpackDocument/UnpackView。Plugin ID 与 Document ID 沿用初始化值，Module 只 AddDocument。Standalone 使用独立 Scope 和异步初始化。

完成标准：CompositionTests 验证唯一普通 Document、零其他贡献与严格 Scope；WindowLifecycleTests 验证实际窗口源码的关闭流程。 对应失败需修正并留下回归，不能只核对文件后缀或编译结果。

## 本轮约束与调整

SOLID 优先，采用窄接口、适配器和显式会话，不建设通用框架。注释中文并说明关键设计思路；代码、测试和专项文档同步。没有 AIFLOW、Windows CI 或发布门禁。

原生窗口交互和真实 Host 加载不能由 Headless 平台测试代替。

最终方案见 [implementation.md](implementation.md)，验证归档见 [result.md](result.md)。
