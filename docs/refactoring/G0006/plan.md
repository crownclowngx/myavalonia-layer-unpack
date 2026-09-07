# G0006 计划：本地候选验收与发布准备

日期：2026-09-07。范围：G0006-01～10；状态：实现已完成，实际验证与限制见 [result.md](result.md)。

## 开工问题与依赖

前置：G0001–G0005 的实现、样本和专项档案。本阶段依据 [V1 工作项](../../v1-execution-plan.md)推进，不改变既定插件身份。

## 范围与完成标准

汇总 AC01–AC14，提供统一本地门禁脚本、性能样本、用户说明、支持矩阵、源码摘要和发布前待验收项。

完成标准：最终门禁记录在 artifacts/local-verification，性能原始数据在 artifacts/performance，UI 图像在 artifacts/ui；结果文档保存本轮实际数量与环境摘要。 对应失败需修正并留下回归，不能只核对文件后缀或编译结果。

## 本轮约束与调整

SOLID 优先，采用窄接口、适配器和显式会话，不建设通用框架。注释中文并说明关键设计思路；代码、测试和专项文档同步。没有 AIFLOW、Windows CI 或发布门禁。

按用户明确约束，Release、正式 ZIP、部署、安装、Windows CI 与真实 Host 发布门禁本轮全部暂缓。

最终方案见 [implementation.md](implementation.md)，验证归档见 [result.md](result.md)。
