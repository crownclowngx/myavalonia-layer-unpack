# R07 按操作区分的支持矩阵

日期：2026-09-09。读取与创建使用独立数据表；“依赖库提供接口”不作为插件支持证明。真实固定样本及 SHA-256 位于 [r07-fixtures.json](../../../tests/LayerUnpackPlugin.Headless.Tests/Fixtures/r07-fixtures.json)，全部夹具文件摘要见 [fixture-hashes.json](../../../tests/LayerUnpackPlugin.Headless.Tests/Fixtures/fixture-hashes.json)。

## 创建

| 容器 | 压缩偏好 | 密码／名称 | 文件与目录 | 边界与独立证据 |
| --- | --- | --- | --- | --- |
| ZIP | 标准、快速、高压缩、仅打包 | 可选 AES-256；名称可见 | UTF-8、空文件、空目录 | 原 ZIP64 能力保留；libarchive 逐项核对；大于 4 GiB 正文未实测 |
| TAR | 固定仅打包（API 用 Standard） | 不加密；名称可见 | PAX UTF-8、长路径、空文件、空目录 | .NET 10 Writer；libarchive 逐项核对；无正文校验和；大于 4 GiB 正文未实测 |
| TAR.GZ | 标准、快速、高压缩 | 不加密；名称可见 | 单成员 GZip 包装 PAX TAR，同上 | .NET 10 Writer/GZip；libarchive 逐项核对；默认预算会计量中间 TAR 与正文 |
| 7z | 未开放 | 未开放内容／头加密 | 混合条目候选验证失败 | 锁定版本 LZMA2 压缩头／普通头均失败，见[评估](sevenzip-evaluation.md) |
| RAR、TAR.BZ2、TAR.XZ、单 GZip/BZip2/XZ | 不创建 | 不创建 | 不创建 | 读取能力不扩大创建菜单 |

默认 ZIP、合成一个包；格式切换使预览失效，重置压缩偏好并清除密码。TAR 不显示压缩级别，TAR/TAR.GZ 不显示密码设置。Headless 同样拒绝不支持的密码／模式，不静默降级。冲突命名为 `资料 (1).tar.gz`。

所有创建只承诺普通文件字节、相对路径和空目录；拒绝链接与设备。不保留 ACL、扩展属性、所有者、所有时间、原归档注释等全部元数据；目录时间不承诺保留。转换与提交清单重打包仍为 ZIP 目标。

## 读取、选择与校验

| 格式 | 识别 | 列目录／选择提取 | 全部解压／完整检查 | 密码 | 实际校验及特殊限制 |
| --- | --- | --- | --- | --- | --- |
| 单卷 ZIP/ZIP64 | 是 | 是／是 | 是／是 | 已测 ZipCrypto、WinZip AES，名称可见 | 长度、普通/ZipCrypto CRC、AES 认证分别记录；严格旧编码、目录预算；拒绝分卷／危险路径／链接 |
| 7z | 是 | 否／否 | 已测变体 | 已测 LZMA/LZMA2 AES、加密头 | 非零长度与 CRC；固实读取沿用；不拼接分卷，数字卷失败可保留缺卷/损坏歧义 |
| RAR4/5 | 是 | 否／否 | 已测变体 | 已测内容／头加密 | RAR4 CRC；RAR5 加密 MAC 未验证，结果始终受限；固实沿用，拒绝分卷及重定向条目 |
| UTF-8 TAR | 是 | 否／否 | 是／是 | 无 | 条目长度；没有正文校验和，等长篡改不可检出；拒绝链接／设备 |
| 单成员 GZip | 是 | 否／否 | 是／是 | 无 | 尾部 CRC32 + 长度补验，截断尾部失败；多成员拼接未开放 |
| BZip2/XZ | 是 | 否／否 | 已测流 | 无 | 完整解码；不报告未确认的校验算法，无校验变体不获得认证标签 |
| TAR + GZip/BZip2/XZ | 是 | 否／否 | 已测组合 | 无 | 外层完整解码 + TAR 条目长度；组合算一个逻辑归档、同时计量中间流与条目 |

目录成功不等于内容完整。文件名旧编码只显式选择；重复名称、大小写冲突、特殊条目与元数据范围继续服从历史安全政策。历史变体证据见 [G0003](../G0003/format-support-matrix.md)、[G0009](../G0009/encryption-and-compression-matrix.md)、[G0010](../G0010/format-support-matrix.md)。

本地互操作工具：bsdtar/libarchive 3.8.4。脚本每次使用本次测试生成的产物核对完整目录、长度与 SHA-256，输出独立记录；不是生产外部引擎依赖或发布门禁。
