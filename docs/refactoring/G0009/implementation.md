# G0009 实施方案

日期：2026-09-08。范围为 [R03](../../roadmap/R03-batch-packaging-and-encryption.md)，实际检查见[结果](result.md)。

## 职责和依赖

| 组成 | 责任与设计理由 |
| --- | --- |
| `PackInputSelection` | 合并与分别共用规范路径、去重、父子吸收和根名编号，避免模式切换改变收录规则 |
| `PackOptions / PackExclusionRules` | 不可变、无密码的有限参数；规则只表达目录叶名和文件扩展名 |
| `PackPlanner / PackPlan` | 只读枚举、规则排除与目录剪枝、SHA-256；继续负责写入前后来源复验 |
| `IPackBatchService / PackBatchService` | 外层分组与目标分配，依赖 `IPackService`，准备全部组后返回可审阅快照 |
| `PackBatchSession` | 一个批次的结果、互斥、重试、取消和秘密寿命；成功组不会再调度 |
| `IPackService / PackService` | 保持单包验证、限时、输出预算、故障脱敏、暂存回滚和提交 |
| `IArchiveWriter / ZipArchiveWriter` | 普通 ZIP 写入，按明确加密选项转交 AES 适配 |
| `AesZipArchiveWriter` | 锁定 SharpZipLib 的 AES-256 写入与中央目录收尾，不处理分组、不存密码 |
| `PackEntryCopier` | 两种写入共用长度/摘要/元数据验证和固定缓冲区，避免格式扩展漏掉来源检查 |
| `PackSecret` | 通过独立执行参数进入写入器，私有字段持有秘密、无可序列化密码属性 |
| `PackDocument` 三个分文件 | 生命周期和建议值；按需选项；后台调用和结果投影。分文件仍是同一个 Document，不建立通用 UI 框架 |

S：分组不放进写入器，密码不放进计划。O：扩展写入适配而不改解压候选池。L：所有写入实现遵守输出流所有权、来源摘要、预算及取消。I：Document 依赖批次用例，批次用例依赖单包用例，单包依赖写入端口。D：DI 注册显式服务，运行状态只在会话/Document 内。仅使用简单适配器、会话与既有文件事务。

## 数据与控制流

表单生成 `PackBatchRequest` → 顶层选择和目标映射 → 每组调用 `PackService.PrepareAsync` → 冻结 `PackBatchPlan` → 审阅 → 独立 `PackSecret` 创建会话 → 顺序调用单包执行 → 每组暂存/复验/不覆盖提交 → 批次结果投影。

没有新源快照时，准备失败的组保留失败诊断。执行普通故障继续下一组。重试仅挑选原结果 `CanRetry`，始终使用原清单、输出和秘密；表单改动只作用于新任务。源变化后的“重新准备未完成项”复制失败/取消来源到新请求，保留先前成功路径，要求再次审阅和重新输入目标密码。

排除在读取内容前发生，命中目录整棵剪枝；清单记录目录一项并明确包含后代。逆序保留有内容的祖先和原有空目录，删除只因排除而留下的空容器；使用集合和新列表，避免大量空目录逐项删除的平方级移动。

## 本轮发现并修复的问题

1. `ZipAESStream` 的 `ReadAsync(Memory<byte>)` 可能落入 `CryptoStream` 的基类实现，绕开 Stored AES 的认证尾部。`StreamCopy` 改用公开的 `byte[]` 重载；真实零字节/非空 Stored AES 正确内容与损坏认证码均有回归，不自写加密算法。
2. AES 头部写入超限时，第三方 `Dispose` 再次收尾可能抛出次生错误。适配器保留原始故障，仍尝试释放，最终关闭文件和清理归事务所有。
3. SharpCompress 0.50.4 的非空 Stored AES 读取会多出 18 字节。AES“仅打包”采用 Deflate 0 不压缩块，经过同一独立读取器重新核对完整长度和 SHA-256；不把错误读取结果算兼容。

互操作调用方式与证据边界见[专项矩阵](encryption-and-compression-matrix.md)。依赖版本、锁文件、插件身份与资产声明保持原有配置。
