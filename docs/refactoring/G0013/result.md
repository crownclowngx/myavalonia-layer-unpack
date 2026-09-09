# G0013 / R07 实施结果

日期：2026-09-09。本阶段已实现统一能力／诊断、按需检查，以及普通 TAR/TAR.GZ 创建。7z 独立候选验证失败，保持未开放，R07 整体仍有后续单元；具体见[评估](sevenzip-evaluation.md)。

## 实际改动

检查区分目录和正文，结果带实际长度／CRC／AES 证据、校验限制、下一步及临时清理信息。复用原解码器和事务，新增 GZip CRC/长度尾部核对及 ZIP 目录资源预检，保留密码、缺卷歧义和 RAR5 MAC 限制。

普通创建菜单提供 ZIP/TAR/TAR.GZ，默认 ZIP；新格式复用来源清单、SHA-256、预算、分别打包、不覆盖事务和取消。TAR 使用 PAX UTF-8，复合后缀冲突正确编号，无效密码/模式在 UI 与 Headless 一致拒绝。R06 转换目标保持 ZIP。

## 本地验证

最终执行 `./tools/verify-local.ps1`，全部 **477 项测试通过、0 失败、0 跳过**：Headless 379 项，Plugin/UI 98 项；相对 G0012 新增 47 + 7 项。Debug 构建 0 警告、0 错误。最终机器记录见 [verification-summary.json](verification-summary.json)。

环境：Windows x64、.NET SDK 10.0.302、Avalonia 12.1.0、Plugin SDK 3.3.0；SharpCompress 0.50.4、SharpZipLib 1.4.2 未升级。基线提交 `366c5d6af2cc7a1bf436777338df45a46fc138e1`，验证包含未提交工作区；源码／配置／脚本／夹具摘要为 `C195D53B7A285632C746D59FAD132E413EA6B04D5383B1AD67A30DFBF1831932`，逐文件记录在 JSON。

| 本地检查 | 结果 | 秒 |
| --- | --- | --- |
| locked-restore | 通过 | 0.80 |
| debug-build | 通过 | 4.26 |
| debug-assets | 通过 | 0.83 |
| headless-tests | 通过 | 37.83 |
| plugin-tests | 通过 | 15.66 |
| format-interop | 通过 | 0.18 |
| format | 通过 | 7.92 |
| docs | 通过 | 0.12 |
| diff-check | 通过 | 0.06 |

日志和独立 TRX 位于 `artifacts/local-verification/`。Git 仅提示工作区换行在后续 checkout 时可能规范化，差异检查退出码为 0；这不属于编译警告。未运行发布门禁。

性能原始记录位于 `artifacts/performance/r07-*.json`：2,000 文件（2,048,000 字节）完整检查用时 1683.76 ms；64 MiB 内容检查在展开 1,048,576 字节时取消，取消到退出约 1.30 ms；2,000 条目样本中取消时已展开 1,966,080 字节，取消到退出约 4.20 ms。以上均断言临时目录为空、句柄释放，连续十次调用无残留；峰值内存是整个测试进程口径，不能解释为单项检查增量。

已目视检查 460×560 浅色完整结果、深色目录结果以及 900×720 目录结果图片；换行、结果区别与固定主动作均可见。自动测试覆盖键盘、绑定、格式切换、返回原状态及关闭排空，未替代原生/DPI 验收。

固定真实夹具和摘要已保存到测试目录，本次生成样本在 `artifacts/format-r07/`。独立 bsdtar/libarchive 3.8.4 读取 ZIP/TAR/TAR.GZ 后核对全部 6 项的目录类型、3 个文件长度与 SHA-256；两类 7z 候选均被拒绝。

## 范围与遗留

7z 未开放原因与后续验证归属为 R07 的独立创建单元；未升级锁定依赖，没有新增生产外部引擎。完整检查可能临时展开到磁盘，TAR 没有正文校验和，RAR5 加密 MAC 和未报告算法的 XZ/BZip2 限制保留。不检查内嵌包内容，不修复、不拼卷、不保留全部元数据。大于 4 GiB 正文与跨平台恶意并发源修改未验收。

检查页自动渲染深浅主题、窄窗口并检查主动作和键盘；原生选择器、系统拖放、辅助功能、真实 DPI 与 Host 留待实机验收。未使用 AIFLOW，未执行 Windows CI、Release、正式插件包、部署或发布门禁。
