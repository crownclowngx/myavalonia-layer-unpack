# G0002 实施方案：Headless 与引擎验证

日期：2026-09-07。本文描述最终方案；历史模板和中途方案不作为现行实现。

## 最终行为

新增 Headless 和独立测试项目，定义请求、会话、快照、错误与预算。实现流写入、路径验证、输出事务，锁定两个托管解码库并声明自有资产。

## 职责与设计理由

UnpackSession 依赖窄 IArchiveExtractor；ArchiveExtractor 选择格式；ZIP 专用适配使用 SharpZipLib，公共 StreamCopy 负责预算/CRC。引擎从不自行决定输出路径。

公共参数和错误以[执行契约](../G0002/execution-contract.md)为准，不能在 UI 再定义一套深度、密码或重试规则。状态只保留在当前会话；生命周期结束释放密码引用和活动工作。

## 验证方法

DomainTests、SafetyTests、IntegrityTests 覆盖无 UI 引用、参数前置校验、输出冲突、CRC/AES 认证故障、回滚与残留诊断。 整仓门禁通过统一脚本执行，命令、环境及数量见[G0006 结果](../G0006/result.md)，避免每个阶段复制一份易过时的测试统计。

## 范围与遗留

G0002-09 的正式目录/ZIP 验证按本轮用户规则改为源码、锁文件和 Debug 依赖核对，打包检查留到发布。

下次修改对应实现时，同时修订本方案、专项文档、用例及[result.md](result.md)。
