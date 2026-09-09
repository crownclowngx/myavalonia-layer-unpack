# G0014 / R08 实施计划

日期：2026-09-09。对应 [R08](../../roadmap/R08-workflow-integration.md)。本轮实现最小两个无密码动作及一个真实跨插件流程，按已有 G 编号惯例归档；不使用 AIFLOW、Windows CI、Release 或发布门禁。

## 开工核实

1. Core/UI 3.3.0 已有 `IWorkflowActionRegistration`、scoped `IWorkflowActionHandler`、caller-bound `IWorkflowActionGateway/IWorkflowActionRun`。Handler 输入/输出只能是 BCL JSON；取消通过调用令牌传入。
2. Workflow SDK 1.0.0 提供统一 Schema 校验和目录修订。对象必须封闭，数组有上限，输入 256 KiB、输出 1 MiB。新增公开 SDK 依赖仍由宿主共享，不列为插件私有资产。
3. Studio 支持严格定义 v2、目录修订、前序 required 结果引用、最多 100 项 ForEach 和串行调用；SDK 失败立即停止，普通输出仅保留在会话中。
4. Studio 会话 Secret 通过引用解析后仍进入 Handler 普通 JSON；当前 SDK Context 没有独立秘密端口。本期不交付加密动作，不用明文或普通 `secretReference` 字段绕过。
5. Fractal 已注册真实配方渲染和 Artifact 释放动作；选择这两个公开接口作为实际生产者和清理入口，不假设下载或 ImageLab 的未来能力。
6. Studio 原 Runner 将合法 JSON 等同于业务成功；为 R08 增加仅识别公开归档 v1 结果的最小适配，继续成功输出和清理流程，但最终摘要报告业务失败。

## 分工与范围

契约目录负责版本和 Schema；两个 Handler 分别依赖 `IUnpackService`、`IPackBatchService`；结果投影仅输出白名单；事务、层数、分组和命名复用 Headless。没有通用策略框架、隐藏队列或服务定位器。

实现动作、结构验证、独立调用生命周期、非幂等重跑政策、部分失败语义；增加真实格式测试、取消和并发测试、跨三 ALC 的真实插件与 Studio 集成；更新当前文档及 G0014 专项。

## 完成标准

声明范围的本地 Debug 构建、锁定还原、单元测试、集成测试、Studio 回归、格式与文档门禁通过。R08-AC01–09 的适用部分有明确证据，尚无证据的正式宿主安装／原生退出、加密秘密通道和后续可选动作单独列出，不标成完成。
