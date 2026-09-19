# G0015 独立分卷夹具

这些文件是本项目生成的合成测试数据，按仓库许可证分发，不含用户归档或私人密码。开发期生成器为官方 7-Zip (r) 26.03 x86（2026-09-03，Public domain）。工具地址和 SHA-256 见 `manifest.json`；可执行文件只存在于忽略的 artifacts 中，不随插件或夹具分发。

基础原文为 `资料/data.bin`（65539 字节，.NET `new Random(1515).NextBytes`）、UTF-8 `资料/说明.txt`（54 字节）、`资料/空.txt` 和 `资料/空目录`。原始文件的路径、长度与 SHA-256，以及每个物理卷的摘要全部登记在清单中。公开密码仅为 `volume-test`。

在原文目录执行 `7zr a -t7z -v16k -mmt=1 -y <名称>.7z 资料`，各组另外指定：

| 名称 | 参数 | 卷数 |
| --- | --- | --- |
| plain | `-m0=LZMA2 -ms=off` | 5 |
| solid | `-m0=LZMA2 -ms=on` | 5 |
| content-encrypted | `-m0=LZMA2 -ms=on -pvolume-test` | 5 |
| header-encrypted | `-m0=LZMA -ms=on -pvolume-test -mhe=on` | 5 |

性能夹具 `performance/load.7z.*` 使用原 data.bin 重复 512 次，另有 256 个 UTF-8 小文件：路径 `密集/条目-<0..255>.txt`，内容 `测试条目 <编号>\n`。固实 LZMA2、4 KiB 分卷，总计 18 卷、72891 字节来源、33560210 字节正文。独立清单为 `performance/manifest.json`。这是一份高压缩率合成样本，不代表 GiB 级随机数据性能。

生成时间戳可能改变容器字节，因此回归使用已冻结的卷摘要；重建时必须由独立工具重新解出并核对原文，不能只刷新摘要。测试还会将 plain 逻辑字节重新切成不等长卷，包括 1 字节首卷和 `.009`／`.010` 边界，验证读取不假设等长分段。

独立复核命令：

```powershell
./tools/verify-split-interop.ps1 -SevenZipPath ./artifacts/G0015/7zr.exe
```

该脚本核实工具及卷摘要，以 7zr 解出所有五组，核对条目数量、空目录、长度和原文 SHA-256。日常单元测试直接使用固定夹具，不需要下载或启动外部程序。
