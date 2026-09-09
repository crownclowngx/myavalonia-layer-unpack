# G0013 实施方案

## 职责与设计理由

| 组成 | 职责 |
| --- | --- |
| `ArchiveCapabilities` | 独立读取、创建矩阵；创建 GUI 和参数验证引用同一能力表，旧浏览契约只做投影 |
| `ArchiveCheckRequest / Result / Evidence` | 不可变快照、范围、状态、按文件计数的实际证据、限制和资源数据；密码不序列化 |
| `IArchiveCheckService / ArchiveCheckService` | 目录／正文用例编排、共享候选预算与总超时、临时目录所有权和清理 |
| `IArchiveExtractor` | 继续负责单逻辑包解码；成功后附加它实际执行的校验证据 |
| `ArchiveWriter / ZipArchiveWriter` | 有限格式分派、TAR/PAX 写入及原 ZIP 适配；不负责枚举或提交 |
| `VerifiedPackReadStream` | 在引擎拉取来源时检查取消、长度和 SHA-256，随后复验元数据 |
| `PackPlanner / PackFileTransaction` | 继续负责冻结清单及不覆盖提交；按选定容器验证扩展名，复合扩展名整体编号 |
| `ArchiveCheckDocument / View` | 本次输入、显式开始、进度及诊断；父解压／浏览页拥有子任务和关闭责任 |

应用层依赖窄端口，领域契约不引用 Avalonia。没有通用引擎插件体系、动态注册框架或模式开关堆入解压调度器。只有解码器知道 CRC/认证是否真正执行；用例不能依据“格式通常支持 CRC”推导成功证据。

## 数据流

普通创建：选择格式 → 能力验证 → 冻结输入清单和摘要 → 随格式分派 Writer → 验证实际源内容 → 归档收尾 → 复验来源 → 不覆盖提交。密码只允许 ZIP AES，TAR 与 TAR.GZ 的密码组合在准备时拒绝，7z 格式显式拒绝。

目录检查：ZIP 浏览用例 → 限定目录大小与条目数 → 读取目录 → `DirectoryRead`。目录成功不产生正文证据，不创建临时目录。

完整检查：锁定只读源 → 单调用候选池与累计账本 → 每次尝试建立私有 `OutputTransaction` → 单包解码及校验 → 清理 → 检查实际取消和来源元数据 → 结果。检查事务从不 Commit；失败尝试不返还预算，清理失败阻断后续密码尝试。

## 重要修正

ZIP 在解析完整目录前复用目录预检，限制条目和元数据内存，并识别明确卷号；对加密正文长度／CRC 失败保留密码与损坏歧义。普通损坏仍使用 `CorruptArchive`。

GZip 不能仅以解码器返回 EOF 表示容器完整。新增尾部 CRC32 与 ISIZE 核对，真实缺少 1／4／8 字节尾部的样本均应失败。范围为单成员 GZip；多成员拼接未开放。

RAR5 加密 MAC 未验证限制保留；TAR 正文没有校验和；XZ/BZip2 当前仅报告可观察到的完整解码，不从猜测的校验算法生成“认证通过”。数字卷后缀的 7z 在读取失败且不能确定时返回 `MissingVolumeOrCorruptArchive`，不以文件名断定缺卷。

详见[检查契约](checking-contract.md)、[矩阵](format-support-matrix.md)、[交互](interaction-design.md)。
