# G0001 实施方案：产品壳与身份

日期：2026-09-07。本文描述最终方案；历史模板和中途方案不作为现行实现。

## 最终行为

移除 MainDocument、示例工作台消息和对应身份，改为 UnpackDocument/UnpackView。Plugin ID 与 Document ID 沿用初始化值，Module 只 AddDocument。Standalone 使用独立 Scope 和异步初始化。

## 职责与设计理由

组合入口只负责登记；Document、窗口和 Host 生命周期分开。同步 Scope 释放只等待核心后台工作，窗口 Closing 负责等待 Scope 后再真正关闭。

公共参数和错误以[执行契约](../G0002/execution-contract.md)为准，不能在 UI 再定义一套深度、密码或重试规则。状态只保留在当前会话；生命周期结束释放密码引用和活动工作。

## 验证方法

CompositionTests 验证唯一普通 Document、零其他贡献与严格 Scope；WindowLifecycleTests 验证实际窗口源码的关闭流程。 整仓门禁通过统一脚本执行，命令、环境及数量见[G0006 结果](../G0006/result.md)，避免每个阶段复制一份易过时的测试统计。

## 范围与遗留

原生窗口交互和真实 Host 加载不能由 Headless 平台测试代替。

下次修改对应实现时，同时修订本方案、专项文档、用例及[result.md](result.md)。
