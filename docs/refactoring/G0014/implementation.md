# G0014 实现与设计说明

## 职责与依赖

`LayerUnpackPluginModule` 增加两个 scoped Handler 注册；普通三个 Document 不增加 Workflow 控件。`ArchiveWorkflowActions` 集中声明 SDK 描述符和冻结版本，简单 Schema 构造函数只消除重复对象声明。

`UnpackWorkflowAction` 仅依赖解压服务，`CreateArchiveWorkflowAction` 仅依赖批次创建服务。二者不依赖 UI Lifetime、Gateway 或其他插件类型。参数校验、业务翻译、结果投影分别放在窄职责中；Headless 继续不引用 SDK 或 Avalonia，满足依赖倒置和单一职责。

每次调用用 `await using` 拥有独立会话。工作流令牌进入扫描、摘要读取、解码、写入、提交及清理过程；返回前等候工作收口。Headless 的 Cancelled 结果转换为 SDK 协作取消异常，避免宿主将返回 JSON 误记为成功。已提交文件保留，未提交暂存由已有事务回滚。同步进度仅转发固定文本，不创建队列，不向日志传入路径或引擎正文。

## 结果与消费端

结果显式分离 `successfulOutputs` 和逐项 `items`，只使用实际提交路径。父包提交后的子包失败不回滚父包，父子 ID 和深度仍可追踪。正常深度停止、空输入或空组分别表达为跳过；失败不是跳过。

结果超过宿主协议预算时只裁剪完整项与其对应成功输出，保留全量计数并设置 `detailsTruncated`、`result-budget-exceeded` 和 `partial-failure`。长路径不被截成假的路径；下游能消费明确提供的子集，但不得据此声称整批交付完整。详细政策见[契约](workflow-contract.md)。

Studio 增加 `ArchiveWorkflowOutcome`，仅按两个 Action ID、公开 contract/version/state 识别业务失败，不依赖归档 CLR 类型。合法部分失败继续运行成功项及释放步骤，最终 `WorkflowRunResult.Succeeded` 为 false，条目保留 SDK 状态并增加白名单业务错误码。未知契约或版本停止后续消费；其他 Provider 执行规则不变。

## 真实集成边界

独立测试项目只引用 SDK/BCL，不获得三个插件的编译引用；ProjectReference 的 `ReferenceOutputAssembly=false` 只保证真实源码先构建。三个入口分别载入可回收 ALC，公共 SDK 使用默认 ALC，归档 Headless 与引擎由归档 ALC 私有加载。

本地测试 Gateway 验证描述符和实例，创建独立动作 Scope 并排空取消。Studio Codec/Runner 由测试反射驱动，反射只在测试入口内，不属于插件之间的通信。真实 Fractal 生成 PNG、真实归档创建、真实解压，使用 SHA-256 和独立 ZIP 读取器验证字节等价。

这不是正式宿主安装、确认窗口或卸载验证。测试中 Run 拥有者在 finally 经 Fractal Release 清理会话 Artifact；归档插件从不删除生产者文件，也不删除用户已交付成果。使用与遗留见[集成专项](integration-example.md)。
