# R07 检查与统一诊断契约

## Headless 调用

```csharp
var service = new ArchiveCheckService();
var result = await service.CheckAsync(new ArchiveCheckRequest(
    sourcePath: @"D:\资料\归档.zip",
    scope: ArchiveCheckScope.FullContent,
    passwords: ["调用方明确提供的候选"],
    temporaryDirectory: @"D:\临时空间"), cancellationToken: cancellationToken);
```

`cancellationToken` 由调用方提供。省略临时父目录时使用系统临时目录，调用方不需要选择用户输出位置。源路径和指定的临时父目录必须为绝对路径。请求复制候选集合、验证最多 64 个候选，每个不超过 1024 字符；密码不进入 JSON、ToString、结果或异常链。不保证托管字符串物理清零。

`ArchiveCheckScope.DirectoryOnly` 仅 ZIP，复用浏览目录预检、来源摘要和资源控制；不需要密码。其他格式不偷换为正文检查。`FullContent` 检查一个逻辑归档，包括 TAR 外层压缩流，内部 ZIP 等文件只按普通字节读取。

## 结果语义

| 状态 | 可表达的事实 |
| --- | --- |
| `DirectoryRead` | 目录读取成功；正文未检查，Evidence 为空 |
| `Completed` | 完整读取结束，声明的实际校验已通过；不是所有变体或内嵌包的认证承诺 |
| `CompletedWithLimitations` | 内容已读完，但仍有无校验和、未认证或无法报告算法等限制 |
| `Failed` | 当前包未通过完整检查；没有整包证据标签 |
| `Cancelled` | 调用方取消，实际工作及清理已经退出；残留路径仍可能存在 |

Evidence 记录种类及实际验证文件数。ZIP 分开记录长度、非 AES 的 CRC32、AES 认证码；RAR/7z 只记录已经执行的非零声明长度及允许比较的 CRC；RAR5 加密文件没有普通 CRC 或 MAC 成功标签。GZip 单流记录尾部 CRC32 和长度；外层 GZip 包含 TAR 时单独标识“1 个 TAR 流”。TAR 条目记录长度，同时说明没有正文校验和。

空归档可完成读取，但不捏造“验证了 1 个文件”。失败时 `FilesRead` 为 0，表示没有确认整包完整读取；`ExpandedBytes` 仍报告已经发生的工作。进度中的条目数包含目录、失败尝试和组合流中间条目，不能当作成功文件数。

## 预算、临时空间与来源

沿用 `UnpackLimits`：默认单源 4 GiB、单文件 2 GiB、累计展开 10 GiB、100,000 条目、2,048 次尝试和 5 分钟单调用总超时。密码尝试共享预算，不退款；TAR 外层流及展开的正文分别计量，因此临时空间不会脱离累计预算。调用方可显式调整有限值。

每次尝试只拥有随机 `.layer-unpack-*` 子目录，成功、失败和取消都回滚，不提交到目标目录；父目录本身可能被创建并保留为空。清理失败返回确切残留路径且停止后续尝试。取消优先于成功，超时与用户取消分别表达，不承诺第三方解码器内部每一步都可抢占。

Windows 只读共享句柄贯穿正文检查，阻止普通替换／写入；结束复验长度与修改时间。此设计不声称提供跨平台原子快照或对恶意并发写入者的强一致保护，调用方应提供静止来源。目录检查沿用浏览用例的摘要复验。

## 诊断

`UnpackDiagnostic` 增加共享 `NextStep`，旧 Code／Message 保持；正文失败统一使用脱敏业务诊断。明确 ZIP/RAR 卷信息使用 `MissingVolume`；7z 数字卷读取失败无法确定时使用新增 `MissingVolumeOrCorruptArchive`。提供候选或已识别加密内容的引擎校验故障保留密码／损坏歧义，不泄露底层异常。

编码错误建议重新选旧 ZIP 编码，来源不可用提示路径和占用，预算／超时建议减少输入或由调用方重新评估有限限制。不存在修复、拼卷或密码破解能力。

相关：[支持矩阵](format-support-matrix.md)、[验收](acceptance-matrix.md)、[结果](result.md)。
