# 格式夹具与来源

这些样本仅用于本地离线测试，不包含用户资料，也不随正式插件发布。所有可执行条目只比较文件字节/摘要，绝不执行。

## 上游样本

除 `Generated.*` 外的归档取自 [SharpCompress 固定版本 0.50.4](https://github.com/adamhathcock/sharpcompress/tree/0.50.4/tests/TestArchives/Archives)，许可证原文为同目录 `LICENSE-SharpCompress.txt`。逐文件长度与 SHA-256 记录在 `fixture-hashes.json`，不使用 latest/main 作为样本来源。

公开测试密码：`Zip.deflate.pkware.zip` 为 `12345678`；`Zip.deflate.WinzipAES*.zip` 与 `Rar*.encrypted*.rar` 为 `test`；`7Zip.LZMA*.Aes.7z` 为 `testpassword`。这些密码来自上游测试，仅用于公开夹具。

通用的三个内容摘要来自同版本 `tests/TestArchives/Original` 中的图片、EXE 和 `тест.txt`。原仓文本检出为 LF，而归档内为 CRLF，预期文本摘要由原文件独立转换为 CRLF 后计算，记录于 `expected-content.json`；生产解压不转换换行。

`Zip.deflate.WinzipAES2.zip` 的相对目录是 `Zip.deflate.WinzipAES2/`，其三个文件内容对应同一组原始文件；`Zip.deflate.pkware.zip` 只有 `Folder/File.txt`，内容为 ASCII 的 `bla-bla-bla-bla-bla`（19 字节），已用 Python 标准库的独立 ZipCrypto 读取交叉核对。

上游 `Tar.tar.gz/bz2/xz` 的旧编码文件名保留为拒绝乱码的负向回归。RAR 分卷首卷保留为缺卷诊断回归；`7Zip.BZip2.split.001` 与 `7Zip.encryptedFiles.7z` 仅为研究样本，尚不作为支持证据，不计入正向格式声明。

## 自有样本

`Generated.utf8.tar`、其 GZip/BZip2/XZ 组合与单文件压缩流由 [tools/generate-fixtures.py](../../../tools/generate-fixtures.py) 用 Python 3 标准库生成。归档时间固定为 0，内容为固定中文 UTF-8 文本和空文件；测试逐字节或逐文本比较，不使用本插件生成预期结果。

```powershell
python tools/generate-fixtures.py
```

其他普通 ZIP、混合明文/AES、路径逃逸、重复条目、TAR 链接/设备、截断和 CRC/认证篡改样本由各测试在独占临时目录中生成。混合 AES 样本使用 SharpZipLib 写入；正向 AES 兼容性同时由独立的上游真实包验证。性能样本按 64/128 MiB 和 2,000 个小文件的固定规模生成。
