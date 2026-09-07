# G0002 引擎选择与验证

日期：2026-09-07。结论：按格式采用成熟托管库，文件写入、预算、递归和密码调度始终由本项目控制。

| 候选 | 实际核验 | 结论 |
| --- | --- | --- |
| .NET 10 BCL | 创建/读取普通 ZIP，读取 UTF-8 TAR 和 GZip；独立生成与校验测试内容 | TAR/GZip 使用 BCL；ZIP 加密能力不能覆盖需求 |
| SharpCompress 0.50.4 | 真实 ZIP/7z/RAR、固实、加密与 BZip2/XZ；ZIP AES 认证尾部篡改用例曾错误地得到成功 | 用于 7z、RAR、BZip2/XZ；不作为 ZIP 解码器 |
| SharpZipLib 1.4.2 | 普通 ZIP、ZipCrypto、AES、混合明文/AES、Unicode、CRC=0 与认证尾部篡改 | 最终用于 ZIP；认证损坏回归必须失败并回滚 |

没有运行外部 7-Zip 进程、引入原生 DLL 或自行实现密码学。两个库在当前锁定目标下没有额外运行时依赖；私有资产为 `SharpCompress.dll` 与 `ICSharpCode.SharpZipLib.dll`。Headless 作为项目引用进入 Plugin 编译/运行依赖闭包，并由 CollectLayerUnpackHeadlessAsset 显式声明 DLL 归属，因为 Build 1.1.2 不自动部署项目引用。Debug 资产门禁验证实际引用文件、私有包和许可声明；正式目录和 ZIP 检查按本轮用户约束留到发布。

## 适配决策

`ArchiveExtractor` 只选择单包读取路径。ZIP 委托 `ZipArchiveExtractor`，从中心目录读取属性，通过 SharpZipLib 解密并验证 AES 认证；普通 ZIP 同时计算 CRC。7z 顺序提取固实数据；RAR 用 reader 顺序读取；TAR 用 `TarReader` 明确区分普通文件、目录和特殊条目。

所有流通过 `StreamCopy` 写入调用方创建的文件，块大小 128 KiB；先扣预算，再写入。禁止引擎自行决定落盘路径、覆盖模式或自动寻找分卷。压缩流先写事务内中间文件，内容为 TAR 时作为同一逻辑包继续展开。

SharpCompress 在 `EntryStream.DisposeAsync` 时可能继续跳过未读内容；失败/预算/取消路径先调用 reader.Cancel，避免清理阶段继续解完整个条目。SharpZipLib 的部分解码为同步实现，异步复制及令牌属于协作取消；头解析和密钥派生没有强制中断保证。

## 明确的支持限制

- SharpCompress 的加密 RAR 流关闭 CRC；本适配器为 RAR4 补验普通 CRC，RAR5 的加密校验值不能按普通 CRC 比较。RAR5 加密代表样本内容正确，但 MAC 未验证，结果节点强制附带说明。
- BCL TAR 只在本项目承诺 UTF-8 名称；旧编码样本保留为明确失败的回归，不把乱码输出作为通过。
- 预算约束输入长度、输出写入和调度工作，不是解码器元数据、字典内存或 CPU 的硬沙箱。不承诺任意恶意归档都能立即取消。
- 未验证变体不从扩展名推断支持，详见[格式矩阵](../G0003/format-support-matrix.md)。

## 官方依据与许可

[SharpCompress 0.50.4 包](https://www.nuget.org/packages/SharpCompress/0.50.4)、[同版本用法](https://github.com/adamhathcock/sharpcompress/blob/0.50.4/docs/USAGE.md)、[AES 流实现](https://github.com/adamhathcock/sharpcompress/blob/0.50.4/src/SharpCompress/Common/Zip/WinzipAesCryptoStream.Async.cs)、[RAR CRC 实现](https://github.com/adamhathcock/sharpcompress/blob/0.50.4/src/SharpCompress/Compressors/Rar/RarCrcStream.cs)支持上述适配限制判断。

[SharpZipLib 1.4.2 包](https://www.nuget.org/packages/SharpZipLib/1.4.2)和[同版本 AES 认证实现](https://github.com/icsharpcode/SharpZipLib/blob/v1.4.2/src/ICSharpCode.SharpZipLib/Encryption/ZipAESStream.cs)支持 ZIP 适配选择；仍以本仓真实内容和篡改测试作为最终证据。两库许可证为 MIT，完整原文纳入 Plugin 的 `ThirdPartyNotices` 自有资产声明。

测试依赖统一为 xUnit v3，以匹配 Avalonia 12 的测试适配器；变更方式参照[xUnit 官方迁移说明](https://xunit.net/docs/getting-started/v3/migration)。
