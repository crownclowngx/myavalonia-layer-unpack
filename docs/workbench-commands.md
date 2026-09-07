# Workbench Command 边界

V1 不注册全局 Workbench Command、菜单贡献或快捷键。初始化模板的 `ApplyWorkbenchMessage`、专用 ID 和适配代码已移除。

开始、取消、移除、清空和重试使用 Document 内的命令。它们直接调用同一个 Headless 用例；没有为局部操作创建全局入口。`CompositionTests` 断言 Module 只登记一个普通 Document，其余贡献数量为零。

未来确有跨工作台操作需求时，再依据当时的公开 SDK 增加声明、Scope 内适配及对应测试；不要恢复模板示例作为产品能力。当前入口见[交互设计](refactoring/G0005/interaction-design.md)。
