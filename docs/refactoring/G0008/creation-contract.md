# G0008 ZIP 创建契约

日期：2026-09-07。适用于 R02 的 PackService；本地验证状态见[结果](result.md)。

## 1. 公开调用

```csharp
using LayerUnpackPlugin.Headless.Application;
using LayerUnpackPlugin.Headless.Contracts;

var service = new PackService();
var request = new PackRequest(
    inputs: [@"D:\资料\项目", @"D:\资料\说明.txt"],
    outputPath: @"D:\交付\项目.zip");
var plan = await service.PrepareAsync(request, cancellationToken: cancellationToken);
// plan.Roots 提供来源到包内根名，plan.Entries 提供文件、目录与摘要。
var result = await service.ExecuteAsync(plan, cancellationToken: cancellationToken);
```

`cancellationToken` 由调用方提供。Headless 不自动切换线程、不弹窗口；GUI 将用例放到 Task.Run 执行。调用方可提供进度接收器；其回调应快速返回，不能抛出业务无关异常。

## 2. 请求和准备

- 输入为完整本地文件／目录路径；请求复制集合。空输入、整个磁盘根、无效预算或非 ZIP 输出名在准备阶段拒绝。
- 输出名必须是 5–180 字符、以 `.zip` 结尾的合法叶名称。输出路径不能是已有目录，也不能显式作为本次源文件。
- 准备不写文件，失败可抛出 PackValidationException、归一化的 PackFailureException，或用户取消的 OperationCanceledException。
- 路径采用系统包含规则；包内名称按 Windows 接收端的合法名称政策检查。同名根按规范来源排序编号，文件编号保留扩展名；目录编号保留目录名。
- 父目录已被选中时移除被包含的子项，完整保留父目录内文件和空目录。根映射和合并数量作为清单返回。
- 拒绝目录链接、重解析点、设备及不能互操作的名称。硬链接身份不进行全盘去重，按声明的文件路径收录。

## 3. 来源一致性

规划器为文件保存长度、最后写入 UTC 时间和 SHA-256。执行前重枚举清单与元数据，逐文件读取时比较摘要，收尾后再次核对清单与元数据。目录新增、删除或来源文件内容变化可导致失败；不会扫描并自动接纳新增文件。

Windows 读取使用 FileShare.Read，阻止同时写入或删除当前读取文件。不同文件不是同时锁定，最后检查与提交间仍存在文件系统竞态；本功能不宣称对恶意并发修改提供原子快照、硬隔离或无竞态保证。

直接向正在作为来源的目录并发写入其他文件，可能触发 InputChanged；同一输出目录的独立输入任务支持不覆盖并发提交。源目录内部输出时只排除请求目标和本事务临时文件，不忽略其他来源变化或凭名称过滤用户文件。

## 4. 输出与提交

输出父目录在执行时创建。源内部的新输出子目录要求先存在，准备阶段对此给出提示；源外新目录可以在执行时创建。失败时空输出父目录可能保留，它不被当作 ZIP 产物。

暂存文件为目标父目录下随机 `.layer-pack-*.tmp`，使用 CreateNew 和独占写入。写入器完整结束、中央目录完成、流关闭并通过来源复验后，File.Move(overwrite: false) 提交；同名使用 `名称 (1).zip` 等编号，最多尝试 10,000 个名称。结果返回实际目标，不保证它与建议名完全相同。

写入失败、取消或预算超限时删除本事务拥有的暂存文件。清理失败保留原始故障，并以 CleanupWarning 返回准确暂存路径；不递归删除父目录，不清理用户源或其他任务产物。

## 5. 格式与元数据

固定普通 ZIP、标准 Deflate、UTF-8 名称，无密码。保留文件内容、声明的相对路径和显式目录条目，包括空目录。文件时间映射为 ZIP 本地 DOS 时间，精度为两秒，超出 1980–2107 的时间取端点；目录时间、ACL、原生权限、ADS、硬链接关系等不作保留承诺。

ZipArchive 可根据内容使用 ZIP 扩展，但本阶段不因 API 存在就承诺超大 Zip64 全部变体。经过实测的内容、结构和规模见[格式互操作矩阵](format-support-matrix.md)。

## 6. 资源默认值

| 项目 | 默认上限 |
| --- | --- |
| 原始请求输入 | 1,000 项 |
| 单文件源字节 | 2 GiB |
| 总源字节 | 10 GiB |
| ZIP 文件字节 | 12 GiB，包含头部与中央目录 |
| 条目 | 100,000，目录也计数 |
| 目录深度 | 128，选中根为 0 |
| 每次准备／执行 | 各 10 分钟，分别计时 |

有限上限可由 Headless 请求调整，并有合法值范围检查；GUI 不展示这些参数。准备与写入会各读取源数据一次，TotalBytes 描述源内容大小，不表示整个任务累计磁盘读取量。文件复制缓冲固定 128 KiB，不把整个文件载入内存。

归档预算按最大文件位置计量，回填 ZIP 头不重复累计。取消在扫描、读取、写入、归档收尾和提交前检查，但文件系统同步枚举和调用仍属于协作取消。

## 7. 状态与重复调用

进度状态为 Scanning / Writing / Finalizing；没有可靠总量时使用不定进度。执行结果为 Completed / Failed / Cancelled。失败包含领域诊断，用户取消不冒充超时；超时为 Failed + Timeout。

零文件但含合法空目录可以创建；无输入不能开始。压缩结果允许比源大，零字节源不计算除零比率。失败归档没有可用 OutputPath。

PackService 无共享可变运行状态；同一不可变清单可独立执行，但来源须继续有效。再次执行不幂等，可能生成编号新包。GUI 同一时刻只运行一个操作；失败后显式再次开始会重新准备，不提供 R03 的失败分组重试。
