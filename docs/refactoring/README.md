# 实施档案与文档治理

日期：2026-09-19。当前已实施基线仍为 R08 无密码 Workflow 本地候选：两个动作与真实 Fractal 流程对应 G0014，当前证据见[G0014 结果](G0014/result.md)。G0001–G0013 保留历史记录；7z 创建、加密 Workflow、原生与发布项仍待后续。

G0015 已实现标准 7z 分卷与按层串行解压，全部本地门禁通过。见 [结果](G0015/result.md)、[核心记录](G0015/core-result.md)、[界面记录](G0015/ui-result.md) 和 [当前格式矩阵](G0015/format-support-matrix.md)。

沿用 `myavalonia-fractal-art` 的“产品主文档 + G 编号阶段三件套 + 质量基线 + 专项证据”方式，不复制参考项目的功能、进度、测试数量或临时规则。本轮以用户明确规则为准：SOLID 优先、朴素模式、详细中文注释、单元测试与本地门禁齐全、同步文档；不使用 AIFLOW、Windows CI 或发布门禁。

## 文档职责

| 文档 | 职责 |
| --- | --- |
| [产品主文档](../product-shape-and-implementation-plan.md) | 稳定产品语义、范围、已确认限制 |
| [V1 执行清单](../v1-execution-plan.md) | 原工作项、当前状态及本轮范围调整 |
| [未来产品路线图](../roadmap/README.md) | R01–R08 的目标、范围、依赖和验收要求；用于后续生成详细执行计划 |
| `Gxxxx/plan.md` | 开工问题、范围、依赖、完成标准 |
| `Gxxxx/implementation.md` | 最终方案、职责、数据流、理由 |
| `Gxxxx/result.md` | 实际结果、证据、偏差、未验收项 |
| 专项文档 | 引擎、契约、格式、递归、交互、验收与性能的详细事实 |
| [质量基线](quality-baseline.md) | 长期工程约束与本地/发布门禁边界 |

## 阶段索引

| 阶段 | 名称与状态 | 档案与专项 |
| --- | --- | --- |
| G0001 | 产品壳与身份；已实施，本地验证归档 | [计划](G0001/plan.md) · [方案](G0001/implementation.md) · [结果](G0001/result.md) |
| G0002 | Headless 与引擎；已实施，发布资产待验收 | [计划](G0002/plan.md) · [方案](G0002/implementation.md) · [结果](G0002/result.md) · [引擎评估](G0002/engine-evaluation.md) · [执行契约](G0002/execution-contract.md) |
| G0003 | 多格式与密码池；已实施，支持范围明确 | [计划](G0003/plan.md) · [方案](G0003/implementation.md) · [结果](G0003/result.md) · [支持矩阵](G0003/format-support-matrix.md) |
| G0004 | 深度递归与输出；已实施，本地验证归档 | [计划](G0004/plan.md) · [方案](G0004/implementation.md) · [结果](G0004/result.md) · [递归设计](G0004/recursive-execution-design.md) |
| G0005 | Document 交互；已实施，原生交互/DPI 待实机检查 | [计划](G0005/plan.md) · [方案](G0005/implementation.md) · [结果](G0005/result.md) · [交互设计](G0005/interaction-design.md) |
| G0006 | 本地候选验收；本地证据归档，发布暂缓 | [计划](G0006/plan.md) · [方案](G0006/implementation.md) · [结果](G0006/result.md) · [验收矩阵](G0006/acceptance-matrix.md) · [性能基线](G0006/performance-baseline.md) |
| G0007 / R01 | 简洁交互与解压体验；已实施，本地验证通过，部分原生交互待验收 | [计划](G0007/plan.md) · [方案](G0007/implementation.md) · [结果](G0007/result.md) · [交互专项](G0007/interaction-design.md) · [验收矩阵](G0007/acceptance-matrix.md) |
| G0008 / R02 | 基础 ZIP 创建；已实施，本地 172 项测试通过，部分原生交互待验收 | [计划](G0008/plan.md) · [方案](G0008/implementation.md) · [结果](G0008/result.md) · [创建契约](G0008/creation-contract.md) · [互操作矩阵](G0008/format-support-matrix.md) · [交互专项](G0008/interaction-design.md) · [验收矩阵](G0008/acceptance-matrix.md) |
| G0009 / R03 | 批量打包与加密；已实施，本地 230 项测试通过，部分原生交互待验收 | [计划](G0009/plan.md) · [方案](G0009/implementation.md) · [结果](G0009/result.md) · [批次契约](G0009/batch-creation-contract.md) · [格式矩阵](G0009/encryption-and-compression-matrix.md) · [交互](G0009/interaction-design.md) · [验收](G0009/acceptance-matrix.md) |
| G0010 / R04 | 压缩包浏览与选择提取；已实施，本地 292 项测试通过，部分原生交互待验收 | [计划](G0010/plan.md) · [方案](G0010/implementation.md) · [结果](G0010/result.md) · [浏览契约](G0010/browsing-contract.md) · [格式矩阵](G0010/format-support-matrix.md) · [交互](G0010/interaction-design.md) · [验收](G0010/acceptance-matrix.md) |
| G0011 / R05 | 输出整理；已实施，本地 347 项测试与门禁通过，部分原生交互待验收 | [计划](G0011/plan.md) · [方案](G0011/implementation.md) · [结果](G0011/result.md) · [整理契约](G0011/organization-contract.md) · [交互](G0011/interaction-design.md) · [验收](G0011/acceptance-matrix.md) |
| G0012 / R06 | 格式转换与重新打包；已实施，本地 423 项测试与门禁通过，部分原生交互待验收 | [计划](G0012/plan.md) · [方案](G0012/implementation.md) · [结果](G0012/result.md) · [转换契约](G0012/conversion-contract.md) · [保留矩阵](G0012/preservation-matrix.md) · [交互](G0012/interaction-design.md) · [验收](G0012/acceptance-matrix.md) |
| G0013 / R07 | 统一能力、按需检查与 TAR/TAR.GZ 创建；本地 477 项测试与门禁通过，7z 候选失败未开放 | [计划](G0013/plan.md) · [方案](G0013/implementation.md) · [结果](G0013/result.md) · [检查契约](G0013/checking-contract.md) · [格式矩阵](G0013/format-support-matrix.md) · [7z 评估](G0013/sevenzip-evaluation.md) · [交互](G0013/interaction-design.md) · [验收](G0013/acceptance-matrix.md) |
| G0014 / R08 | 最小两个无密码动作及真实跨插件流程；本地验证见结果 | [计划](G0014/plan.md) · [方案](G0014/implementation.md) · [结果](G0014/result.md) · [契约](G0014/workflow-contract.md) · [真实流程](G0014/integration-example.md) · [验收](G0014/acceptance-matrix.md) |
| G0015 / R07 后续 | 标准 7z 分卷与按层串行解压；已实施／本地门禁通过 | [计划](G0015/plan.md) · [技术方案](G0015/implementation.md) · [当前记录](G0015/result.md) · [卷组契约](G0015/split-volume-contract.md) · [调度专项](G0015/layered-scheduling-design.md) · [验收计划](G0015/acceptance-matrix.md) |

## 状态与维护规则

R02 基础 ZIP 创建已建立 G0008 实施档案：[计划](G0008/plan.md)、[方案](G0008/implementation.md)、[创建契约](G0008/creation-contract.md)、[创建矩阵](G0008/format-support-matrix.md)、[交互专项](G0008/interaction-design.md)、[验收矩阵](G0008/acceptance-matrix.md)、[实际结果](G0008/result.md)。最终本地检查与人工遗留以结果为准，历史阶段记录不重写。

规划中表示尚无完整阶段方案；待实施表示方案已归档；实施中表示代码或适用门禁未完成；已实施/待验收表示声明范围的实现完成，缺失环境和检查明确列出；已封板要求该阶段声明的验收都有证据。当前不声称 V1 全环境封板或已发布。

修改源码时先核对产品与阶段约束，再同步方案、专项和结果；最后更新本索引与执行清单。来源冲突以实际源码和运行证据核对，不通过改文档把失败改成通过。范围变化必须说明原因、影响、后续归属；未执行项不能勾成已完成。

编号稳定递增，不复用已有 G 编号。结果至少包含日期、改动、实际命令/环境、测试结果、范围偏差和遗留。密码、私人压缩包和原始引擎异常不进入文档；公开测试密码可用于夹具说明。

未来路线图使用 R 编号，与 G 实施档案分开。一个 R 阶段可以映射多个新 G 阶段，生成详细计划时再分配当前可用编号并更新映射；不预占旧编号，不把路线图或计划文档完成计为功能已实施。编写要求见[阶段执行计划生成模板](../roadmap/stage-execution-plan-template.md)。

本地日志在忽略的 `artifacts/` 下便于复跑；当前摘要与验收状态保存在阶段结果中。G0006 验证时尚无基线提交；G0007 以 8ae2949 为基线，G0008 以 c7ddcb0 为基线，分别用源码/配置/夹具摘要标识验证工作区；后续 Git 提交保存验证快照，不改写原始门禁记录，不虚构正式包摘要。

## R03 历史增量

G0009 / R03 批量打包与加密已实施，最终本地验证与原生遗留见[结果](G0009/result.md)。专项：[计划](G0009/plan.md)、[方案](G0009/implementation.md)、[批次契约](G0009/batch-creation-contract.md)、[格式矩阵](G0009/encryption-and-compression-matrix.md)、[交互](G0009/interaction-design.md)、[验收](G0009/acceptance-matrix.md)。历史 G0008 记录保持，不将其 172 项测试作为当前新增功能的证据。

## R04 历史增量

G0010 / R04 实现 ZIP 元数据浏览、稳定身份与后代选择、一层提取、来源摘要复验及独立浏览 Document。完整契约、格式支持、交互和验收分别维护；实际本地命令、源码摘要、测试与图片检查见[结果](G0010/result.md)。历史 G0009 的 230 项测试不作为当前新增浏览功能的证据。

## R05 历史增量

G0011 / R05 建立提交清单、独立整理预览/执行、来源与包装链规则、复制事务及上下文子页。专用文档记录来源契约、源变化策略、生命周期和真实样本；本地测试、图像与资源实测见[结果](G0011/result.md)。历史 G0010 的 292 项测试不作为本期新增整理功能的证据。

## R06 历史增量

G0012 / R06 复用递归解压、提交清单、整理映射和 ZIP 创建，新增普通转换、显式有限复合操作、结果打包、统一资源计量和逐包回读提交。页面衔接、秘密和元数据边界有专用文档，全部本地证据见[结果](G0012/result.md)；不改写 G0011 的历史验证。

## R07 当前增量

G0013 新增按操作独立矩阵、完整检查契约与上下文子页、GZip 尾部校验及 TAR 创建。独立工具证明 TAR/TAR.GZ 产物完整，7z 候选未通过且未开放。最终证据见[结果](G0013/result.md)，R07 整体仍有后续创建单元。

## R08 当前增量

G0014 注册两个无密码归档动作，使用实际提交清单、来源和诊断表达业务结果；真实 Fractal → 归档 → 解压样例跨三个 ALC，经真实 Studio 运行。Studio 最终摘要识别业务部分失败，普通 GUI 保持三类快速入口。专用契约、清理与重跑政策及最终本地证据见[G0014 结果](G0014/result.md)。
