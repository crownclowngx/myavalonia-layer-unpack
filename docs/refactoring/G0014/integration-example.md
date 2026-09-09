# G0014 真实流程与使用说明

## 使用入口

使用已有 Workflow Studio，普通压缩／解压页没有工作流配置。需要当前归档插件、公开 Core/UI SDK 3.3.0、Workflow SDK 1.0.0、具有 `render-artwork-file`／`release-artifact` 的 Fractal，以及本轮包含业务状态适配的 Studio。缺动作或目录契约漂移必须先刷新目录和重新验证，不能绕过静态引用检查。

样例文件：[严格 v2 模板](../../../examples/workflow/fractal-archive.workflow.json)、[64×64 Fractal 配方](../../../examples/workflow/sample.fractal-workflow.json)。配方使用 Fractal 已支持的历史文件格式迁移入口，最终由真实生产者渲染。

1. 在 Studio 刷新动作目录。界面风险摘要显示本次目录的 contract/presentation revision，分别替换模板中的两个 `__CURRENT_*_REVISION__` 占位符。不要用别的机器或缺少动作目录的 revision。
2. 替换配方、归档输出目录、解压输出目录三个路径占位符，填写绝对路径；在 JSON 中正确转义反斜杠。普通 `${...}` 结果引用保留原样。
3. 将完整 JSON 放入 Studio 定义编辑区，验证后运行。风险确认由宿主控制。本模板不含秘密；不要把真实密码写进定义。
4. 流程依次渲染 PNG → 用 `${render.result.artifact.path}` 创建 ZIP → ForEach `${pack.result.successfulOutputs}` 解压 → 使用原 Artifact 对象调用 Fractal Release。
5. 查看最终摘要和逐项业务错误码，检查真实输出。仅 `successfulOutputs` 可交给下游，SDK Succeeded 不能替代业务 completed。

重复执行会按 `create-new` 自动编号生成新归档和解压目录。关闭自动重试；需要恢复时只选择明确未完成的来源。失败不覆盖既有包，也不回滚已经交付的成功包。

## 中间文件所有权

Fractal run Artifact 属于生产者，正常路径通过最后一个 Release 节点释放；归档动作只借读，不删除它。ZIP 和解压目录为用户交付成果，由用户持有。

Studio 通用定义目前没有 finally／补偿节点。若 SDK 调用失败或取消导致流程在 Release 前停止，最后一个节点不会自动运行；应由本次 Run 的拥有者通过公开 Release 契约显式清理已捕获 Artifact，或按生产者既有过期回收政策处理。不要递归删除 WorkflowArtifacts 公共根，也不要删除已交付 ZIP 作为“回滚”。本地集成测试的拥有者在 finally 执行这项公开补偿；不将测试补偿包装成 Studio 已具有通用自动补偿功能。

## 实际环境证据

`LayerUnpackPlugin.WorkflowIntegration.Tests` 运行上述同一模板，使用真实 Fractal Module、真实归档 Module、真实 Studio Codec／Validator／Runner。三个独立 ALC、独立容器；插件之间仅 SDK/BCL JSON。生成 PNG、ZIP 创建和解压后对正文 SHA-256，证明不是伪造路径的 Fake 流程。

专项还验证上游文件在交接前消失、下游失败保留交付成果、拥有者公开补偿和目录修订漂移。测试替换的只是本地宿主基础设施，不覆盖真实授权窗口、正式插件包安装或原生宿主退出。结果见[验收](acceptance-matrix.md)与[执行记录](result.md)。
