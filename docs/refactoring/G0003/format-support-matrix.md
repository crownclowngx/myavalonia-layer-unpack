# G0003 实测格式与限制

日期：2026-09-07。测试夹具来源、摘要和公开密码见[夹具说明](../../../tests/LayerUnpackPlugin.Headless.Tests/Fixtures/README.md)。支持是下列具体变体通过真实内容验证，不是同扩展名的所有格式都已验证。

| 格式/变体 | 读取器 | 样本与验证 | 状态 |
| --- | --- | --- | --- |
| ZIP Stored / Deflate | SharpZipLib 1.4.2 | 运行时生成 ZIP、`Zip.deflate.zip`；文件清单和 SHA-256 | 通过 |
| ZIP ZipCrypto | 同上 | `Zip.deflate.pkware.zip`，公开密码 `12345678`；精确内容、错误候选和补密 | 通过；密码编码 UTF-8 |
| ZIP AES | 同上 | `Zip.deflate.WinzipAES*.zip`，公开密码 `test`；内容摘要、认证尾部损坏 | 通过 |
| ZIP 明文/AES 混合 | 同上 | 测试中生成混合条目；失败回滚和第二次完整提交 | 通过 |
| ZIP 旧名称编码 | 同上 | GB18030 中文、UTF-8 中文、CP866 俄文 | 通过；CP437 有选项但无专用语种样本 |
| ZIP CRC/截断 | 同上 | 内容翻转、零 CRC、中心目录截断 | 明确失败，未提交 |
| ZIP 强加密/未知方法 | 同上 | 未知压缩方法回归；强加密标志拒绝 | 不承诺 PKWARE 强加密；未知方法明确拒绝 |
| ZIP64 大文件 | 同上 | 本轮未生成超过 4 GiB 的真实 ZIP64 | 未验收，不由库宣称代替实测 |
| 7z LZMA / 固实 | SharpCompress 0.50.4 | `7Zip.LZMA.7z`、`7Zip.solid.7z`；清单/摘要 | 通过 |
| 7z LZMA / LZMA2 AES | 同上 | `7Zip.LZMA.Aes.7z`、`7Zip.LZMA2.Aes.7z`；密码 `testpassword` | 通过；错误候选不提交 |
| RAR4 普通/固实 | 同上 | `Rar4.rar`、`Rar.solid.rar`；清单/摘要 | 通过 |
| RAR4 加密内容/加密头 | 同上 | `Rar.encrypted_filesOnly.rar`、`Rar.encrypted_filesAndHeader.rar`；密码 `test` | 通过；适配层补 CRC |
| RAR5 普通/固实 | 同上 | `Rar5.rar`、`Rar5.solid.rar`；清单/摘要 | 通过代表样本 |
| RAR5 加密内容/加密头 | 同上 | `Rar5.encrypted_filesOnly.rar`、`Rar5.encrypted_filesAndHeader.rar`；密码 `test` | **受限**：样本内容正确，MAC 不验证；节点显示提示 |
| RAR4/5 分卷首卷 | 同上 | `Rar4.multi.part01.rar`、`Rar5.multi.part01.rar`、固实首卷 | 明确 MissingVolume；不拼卷 |
| UTF-8 TAR | .NET 10 TarReader | Python PAX 样本的中文名、空文件；运行时 TAR 链接/设备样本 | 正常内容通过；链接/设备拒绝 |
| 旧编码 TAR | 同上 | 上游 `Tar.tar.gz/bz2/xz` 中旧编码俄文名 | 明确 InvalidNameEncoding，保留失败回归 |
| GZip / BZip2 / XZ 单文件 | BCL / SharpCompress | Python 独立生成 `Generated.single.txt.*`，精确 UTF-8 内容 | 通过 |
| TAR.GZ / TAR.BZ2 / TAR.XZ | 同上 + TarReader | `Generated.utf8.tar.*`；中文内容、空文件和深度 1 | 通过 |
| GZip 内含 ZIP | 同上 + ZIP 读取器 | 运行时生成，深度 2 展开 | 通过，计两个逻辑层 |
| 空 ZIP / TAR / TAR.GZ | 对应读取器 | 测试中生成 | 通过，生成独立空目录 |
| 中文长路径 ZIP | ZIP 读取器 | 输出完整路径超过 260 字符 | 当前 Windows 环境通过；每组件仍遵守文件系统约束 |

## 不扩大的承诺

不支持分卷拼接、损坏修复、SFX EXE、ISO/CAB；不承诺全部 ZIP 算法、RAR 哈希/重定向变体、非 UTF-8 TAR、超大 ZIP64 或所有压缩流损坏形式。没有执行包内的任何可执行文件，夹具中 `test.exe` 仅作为字节数据验证摘要。

所有格式受同一单包输出事务、路径和预算约束。RAR5 加密的限制不会被“解压成功”隐藏：结果有 Warning，UI 显示“已解压（有提示）”；建议保留原包。加密内容校验失败可能与错误密码无法区分，会保留歧义。

ZIP 最终引擎选择来自认证损坏回归；不是删除失败测试或添加一个“忽略校验”开关。选择证据见[引擎评估](../G0002/engine-evaluation.md)。
