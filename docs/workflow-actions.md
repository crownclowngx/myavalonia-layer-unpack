# Workflow 动作与边界

G0014 / R08 新增两个无密码 Provider 动作，通过 Core/UI SDK 3.3.0、Workflow SDK 1.0.0 公开接口发现和调用。归档插件不请求 Gateway，不解析其他插件容器。三个普通 Document 的默认使用方式保持不变。

| 动作标识 | 输入与结果 |
| --- | --- |
| `myavalonia.plugin.layer.unpack.workflow.unpack-v1` | 来源、位置、总层数、编码；返回实际提交目录、父子来源关系、失败和正常深度停止 |
| `myavalonia.plugin.layer.unpack.workflow.create-v1` | 明确来源、位置、名称、分组、已验证格式与压缩偏好；返回实际提交归档和各组状态 |

输入与结果 `version: 1`，必须显式选择 `repeatPolicy: "create-new"`。重新执行创建新产物并按 GUI 相同规则自动编号，不覆盖，不承诺幂等，也不在插件内自动重试。

返回结果的 `state` 区分 `completed`、`partial-failure`、`failed`、`skipped`；下游仅引用 `successfulOutputs` 中的 `path`。SDK 调用成功不等于整批业务成功。配套 Studio 适配会保留成功项与释放步骤的执行机会，并将业务失败记入最终摘要；未更新的 Studio 只能保证 SDK 调用状态，应先更新消费端。

密码、候选数组、加密开关和秘密引用均不在输入 Schema 中，未知字段直接拒绝。无密码解不开的文件返回缺密诊断，不弹页面；取消通过 SDK 取消终态返回并先清理自有临时内容。无 CLI/MCP、历史库、最近密码、跨启动恢复或后台服务。

详见[动作契约](refactoring/G0014/workflow-contract.md)、[真实流程与使用方法](refactoring/G0014/integration-example.md)、[验收矩阵](refactoring/G0014/acceptance-matrix.md)和[实际结果](refactoring/G0014/result.md)。

历史 V1／G0001–G0013 的 Workflow 零贡献是当时事实，历史文档不改写。当前 `CompositionTests` 断言两个动作、零 Consumer/Gateway、零 Tool 和全局命令。直接 Headless 调用见[快速开始](README.md)。
