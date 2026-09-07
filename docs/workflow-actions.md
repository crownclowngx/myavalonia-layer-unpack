# Workflow 与后续扩展边界

V1 不注册 Workflow Provider、Consumer 或 Gateway。Headless 是可直接引用的独立类库，不代表已提供跨插件 Workflow 服务，也不需要 Dock Tool。

未来需要 CLI、MCP 或 Workflow 时另建执行计划，复用同一 Headless 用例。跨 ALC 只传公开 SDK/BCL 契约，不能共享插件私有 CLR 对象；在会记录入参的平台调用前，必须先解决候选密码的秘密传递与脱敏。

目前没有历史任务库、最近密码、跨启动恢复或后台服务。`CompositionTests` 对 Workflow 贡献和 Gateway 请求均断言为零。当前无界面调用方式见[快速开始](README.md)。
