# G0009 压缩偏好与加密互操作矩阵

日期：2026-09-08。创建能力与解压能力分开声明；最终命令和环境见[结果](result.md)。

## 写入映射

| 用户偏好 | 普通 ZIP：.NET 10 ZipArchive | ZIP AES-256：SharpZipLib 1.4.2 |
| --- | --- | --- |
| 标准（默认） | Deflate / Optimal | Deflate 6 |
| 快速 | Deflate / Fastest | Deflate 1 |
| 高压缩 | Deflate / SmallestSize | Deflate 9 |
| 仅打包 | Stored / NoCompression | Deflate 0，不压缩块，仍有 ZIP/Deflate/AES 封装开销 |

时间和压缩率不作固定承诺。零字节文件由写入器使用 Stored；AES 零字节文件仍带密码验证和认证码。显式目录条目不加密；文件名、目录名、大小等元数据可见。UTF-8 名称、内容、相对结构和原有空目录沿用 R02；时间精度和 ACL 等边界见[原矩阵](../G0008/format-support-matrix.md)。

AES 采用 WinZip AE-2 / AES-256，由库生成随机盐和认证码，不提供 ZipCrypto 创建或 7z 加密。库能力依据固定版本[写入源码](https://github.com/icsharpcode/SharpZipLib/blob/v1.4.2/src/ICSharpCode.SharpZipLib/Zip/ZipOutputStream.cs)核对，支持结论另由真实测试确定。

## 实际读取与认证证据

| 读取端及版本 | 普通四级 | AES 四级 | 声明边界 |
| --- | --- | --- | --- |
| SharpZipLib 1.4.2 / 本项目 UnpackService | 完整回读 | 正确密码、逐文件 SHA-256、零字节与空目录通过 | 采用专用 byte[] 读取重载；无密码/错密码、损坏认证码不得提交 |
| SharpCompress 0.50.4 / ArchiveFactory.OpenAsyncArchive | 完整回读 | 全部映射下目录清单、真实长度和 SHA-256 通过 | 逐项使用 EntriesAsync/OpenEntryStreamAsync，作为独立内容读取器；不拿它证明 AES 认证 |
| SharpCompress 0.50.4 / 顺序 ReaderFactory | 不列为本阶段保证 | 含零字节 AES 样本观察到顺序清单不完整 | 不作为目标读取方式，不承诺它等同于中央目录读取 |
| Windows 系统压缩工具、7-Zip/WinRAR 桌面工具 | 本轮未执行原生工具测试 | 本轮未执行原生工具测试 | 不承诺系统自带工具一定支持；接收方需使用支持此加密 ZIP 的工具 |

“跨工具”自动化证据具体指上述两套独立库及版本；它不是所有桌面工具/版本的人工认证。未运行发布或外部桌面安装验收。

真实样本含中文文本、98,304 字节可压缩文本、65,536 字节随机数据、零字节文件和空目录；普通/AES × 四偏好共八组。真实 AES 还覆盖两会话不同密码、同批次相同密码和不同盐、错误/无密码、当前 ZIP 认证尾部翻转、512 KiB 写入中取消、中央目录取消及输出预算。

## 本轮兼容性修复与保留回归

SharpCompress 0.50.4 对非空 Stored AES 曾返回比原内容多 18 字节的流。因此 AES“仅打包”采用 Deflate 0，保留不压缩语义并通过长度和摘要验证；不把原失败变体宣称为已验证兼容。零字节条目的结构仍由中央目录读取器核对。

SharpZipLib 1.4.2 [ZipAESStream](https://github.com/icsharpcode/SharpZipLib/blob/v1.4.2/src/ICSharpCode.SharpZipLib/Encryption/ZipAESStream.cs) 的专用认证在 byte[] 重载中。现代 Memory 重载可能落入 CryptoStream 基类，绕开 Stored AES 认证；本项目 `StreamCopy` 显式使用前者。单测分别生成零字节/65,536 字节外部 Stored AES，先验证正确内容，再准确翻转中央目录定位的认证尾部，要求失败且无已提交输出。

没有升级库、自写 AES 或删去失败场景。所有创建任务保留单包暂存、来源复验和不覆盖提交；正确密码也不能使损坏包视为成功。超大 Zip64、超大内存峰值与桌面工具矩阵未在本阶段新增验证。
