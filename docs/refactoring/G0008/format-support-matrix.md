# G0008 创建格式与互操作矩阵

日期：2026-09-07。写入使用 .NET 10 System.IO.Compression；独立读取使用锁定 SharpZipLib 1.4.2，并核对现有解压用例。最终全量门禁状态见[结果](result.md)。

| 创建场景 | 验证方式 | 范围 |
| --- | --- | --- |
| 普通 ZIP / Deflate / UTF-8 | SharpZipLib 列目录、TestArchive(true)、逐文件 SHA-256 | 已有真实自动化场景 |
| 中文、零字节文件、空目录 | 独立读取器清单、摘要及原解压回读 | 保留内容和目录结构 |
| 多来源同名、父子输入重叠 | 规划清单与最终 ZIP 一致 | 确定编号、合并，不覆盖 |
| 2,001 个文件，含超过 260 字符来源路径 | 真实文件系统与完整 ZIP 内容验证 | Windows 当前环境的此规模样本 |
| 写入中取消／中央目录收尾取消 | 真实 ZIP 写入，断言无提交和无临时残留 | 取消后不能得到伪成功 ZIP |
| 源内容变更、源树变更与占用 | 真实文件修改与独占句柄 | 失败诊断，无不完整提交 |
| 输出超限或输出位置不可写 | 有限输出流及实际阻塞路径 | 故障回滚；额外用替身注入写入错误 |
| 仅空目录或零字节文件 | 独立读取器、统计核对 | 可生成合法归档，可能比源大 |
| 超过 65,535 条目／超大单文件 Zip64 | 本阶段无该规模端到端实测 | 不宣称全变体通过 |
| DOS 时间边界 | 实现有端点映射 | 不将代码检查等同于所有文件系统时间互操作验收 |
| Windows 资源管理器／外部 7-Zip 原生人工读取 | 尚未执行 | 独立库验证不等于所有目标软件验收 |
| ZIP 加密、分别打包、其他创建格式 | 未实现 | 分别归属 R03 / R07 |

没有增加解压格式承诺；读取限制仍见[G0003 格式矩阵](../G0003/format-support-matrix.md)。源数据、路径、空目录和受控错误是验收对象，不把“文件扩展名正确”或同一库写入后读回作为唯一通过证据。

API 依据：[ZipArchive.CreateAsync](https://learn.microsoft.com/en-us/dotnet/api/system.io.compression.ziparchive.createasync?view=net-10.0)、[ZipArchiveEntry.OpenAsync](https://learn.microsoft.com/en-us/dotnet/api/system.io.compression.ziparchiveentry.openasync?view=net-10.0)。实际支持以锁定运行时的构建和测试为准。
