# 锁定版本 7z 创建候选评估

结论：**7z 创建未开放，仍为 R07 后续工作**。独立验证已完成，不代表创建能力已验收。

## 依赖证据

开工核对 SharpCompress **0.50.4** 的 [FORMATS.md](https://github.com/adamhathcock/sharpcompress/blob/0.50.4/docs/FORMATS.md)、[SevenZipWriter.cs](https://github.com/adamhathcock/sharpcompress/blob/0.50.4/src/SharpCompress/Writers/SevenZip/SevenZipWriter.cs)、[SevenZipWriterOptions.cs](https://github.com/adamhathcock/sharpcompress/blob/0.50.4/src/SharpCompress/Writers/SevenZip/SevenZipWriterOptions.cs) 和本地锁文件／程序集。该版本有非固实 LZMA/LZMA2 写入入口，需要可定位输出，CompressionLevel 仍为预留参数；这些 API 不是互操作证明。

依赖仍为 MIT 许可 SharpCompress 0.50.4 与 MIT 许可 SharpZipLib 1.4.2，现有插件私有资产和 ThirdPartyNotices 均保留。本轮无需新库、外部生产进程或发布资产。

## 真实样本

`SevenZipCandidateTests` 直接使用锁定库 Writer 和可定位 FileStream/MemoryStream；绕过产品写入包装器。固定内容包括中文父目录、105 字符目录名、4,096 字节数据、零字节文件、中文正文和空目录，分别生成 LZMA2 压缩头及普通头样本。

| 样本 | 插件完整检查 | 独立 libarchive 3.8.4 |
| --- | --- | --- |
| [R07.candidate-header.7z](../../../tests/LayerUnpackPlugin.Headless.Tests/Fixtures/R07.candidate-header.7z) | 失败，不生成校验证据 | `Malformed 7-Zip archive`，退出码 1 |
| [R07.candidate-plain-header.7z](../../../tests/LayerUnpackPlugin.Headless.Tests/Fixtures/R07.candidate-plain-header.7z) | 失败，不生成校验证据 | `Malformed 7-Zip archive`，退出码 1 |

摘要与来源说明见 [r07-fixtures.json](../../../tests/LayerUnpackPlugin.Headless.Tests/Fixtures/r07-fixtures.json)。候选时间参数为 null，避免目录未捕获时间的问题影响验证；失败并非仅由产品临时输出流产生。此证据只否定上述候选组合满足本期交付标准，不能断言库所有 7z 写入变体都错误。

## 收缩及后续

生产代码不包含 7z Writer，创建菜单只有 ZIP/TAR/TAR.GZ。保留 `PackFormat.SevenZip` 枚举用于明确拒绝调用；不接受密码或压缩级别绕过。旧 7z 真实读取样本仍全量回归。

后续 R07 单元需定位具体上游问题或选择经过评估的版本／实现，再重新验证多文件、空文件、空目录、中文、CRC、独立工具、取消及预算。未经这些证据不得开放，也不在本轮临时更换依赖或手写归档编码器。

复现：运行 Headless 测试后执行 `./tools/verify-format-interop.ps1`。它记录正向创建和候选拒绝两类结果，不能把候选“预期拒绝”统计成 7z 正向支持。
