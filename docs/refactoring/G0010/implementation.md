# G0010 / R04 实施方案

日期：2026-09-08。范围以[计划](plan.md)和[浏览契约](browsing-contract.md)为准。

## 职责与设计理由

| 组成 | 职责 |
| --- | --- |
| `BrowseRequest / BrowseLimits / BrowseSelection / BrowseExtractResult` | 只读请求、有限资源参数、身份集合和归一化结果；请求不包含密码 |
| `ArchiveCatalog / ArchiveSelection` | 不变目录与索引、分页搜索、目录后代集合、父目录三态计数 |
| `IArchiveBrowseService / IArchiveBrowseSession` | Plugin 的窄用例端口；可等待打开、提取与释放 |
| `ArchiveBrowseService / ArchiveBrowseSession` | 只读打开、源校验、会话互斥、取消超时、预算和输出事务编排 |
| `BrowseSource / BrowseReadStream / BrowseBudget` | 来源句柄与摘要、同步和异步读取取消点、读取与展开分账 |
| `ZipDirectoryGuard` | 在引擎分配条目对象前检查 EOCD/ZIP64、单卷、条目数量与中心目录长度 |
| `ZipBrowseCatalog` | 保留中心目录记录序号、补出父目录、显示冲突和不安全属性、提取集合路径预检 |
| `ZipArchiveExtractor.ExtractEntryAsync / StreamCopy` | 全量解压与选择提取共享 CRC/长度/AES 认证和展开量检查 |
| `PathPolicy / OutputTransaction` | 复用既有路径与链接政策，暂存目录、不覆盖提交、失败清理 |
| `BrowseDocument / BrowseView` | 参数、200 行分页、三态选择、结果与就地补密；原生选择和拖放适配 |
| `OwnedUnpackTask` | 在浏览页内承载真实原解压任务，独立取消寿命，只传入来源和输出，不传秘密 |

SOLID 通过清晰职责和窄接口落实。没有将模式开关塞进 UnpackService，也没有为尚未支持的格式建立注册中心。只在确有共同语义的 ZIP 正文校验处提取函数，确保两种入口不会产生认证差异。中文注释解释稳定身份、祖先计数、流重载、来源替换和关闭排空等边界。

## 数据流

打开：验证参数 → 后台打开只读句柄 → 识别 ZIP → 完整来源 SHA-256 → 中心目录预检 → 元数据适配与父目录 → 再次来源验证 → 返回隔离会话。全过程不接收输出目录，不解码条目正文，不建立磁盘缓存。

选择：UI 勾选行 → Headless 展开该目录的明确后代 → 更新身份 HashSet 与祖先计数。搜索和分页只读取目录；每次重绘最多 200 行，三态查询不逐行扫描全包。Headless 单页上限 500。

提取：捕获不可变选择和本次密码 → 会话互斥与累计尝试预算 → 完整集合冲突预检 → 按路径验证源 → 验证实际解码句柄 → 打开 ZIP → 在私有暂存目录中只解所选记录 → 按条目长度、CRC/AES 验证 → 再次验证来源 → 不覆盖提交。

## 页面与生命周期

注册第三个普通 Document，稳定 ID 为 `myavalonia.plugin.layer.unpack.document.browse`。现有 Plugin、解压与压缩身份保持。三类 Document 的服务、选择、密码与 ClosingToken 隔离。Standalone 新增第三个 Scope，Opened 后初始化，关闭先取消三个 Scope，再排空工作并释放。

SDK 3.3 的公开新建激活没有任意路径载荷或页面导航接口；“全部解压…”因此在浏览页内显示真实 UnpackView/UnpackDocument，不调用 Host 内部接口。每个新准备任务默认一层，可在原页面打开递归并查看层数；只有用户点击开始才写入。已运行任务再次进入时保留并显示它的实际输入；空闲任务再次准备时按当前来源新建，旧成功文件留在磁盘。浏览页清空/关闭等待所承载任务退出。

后台在返回结果前登记会话所有权，关闭时即使 UI 续体尚未执行，也能排空并释放。进度代次检查阻止取消后、重载后和关闭后的迟到回调更新页面。秘密每次提取明确输入，字段在提交给后台时清空；加载新归档、转入全部解压、清空和关闭同样清空，不建立密码单例或持久化记录。

## 范围决定

新建浏览任务、文件选择/拖入或直接输入路径是本期入口；未添加输入/结果上下文菜单或系统文件关联。R04 原文允许多个入口来源，本期用独立 Document 完成闭环。其他格式、加密目录、分卷、自解压程序和附加目录记录不承诺浏览，详见[能力矩阵](format-support-matrix.md)。这不改变既有全量解压矩阵。
