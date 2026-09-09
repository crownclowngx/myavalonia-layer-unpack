# G0014 / R08 验收矩阵

| 场景 | 本轮证据与边界 |
| --- | --- |
| AC01 无 UI 解压／创建 | WorkflowActionTests 真实 ZIP/TAR/TAR.GZ 创建及解压，与直接 Headless 内容等价；无需 Document 或文件选择器 |
| AC02 真实上游 | 三 ALC 真实 Fractal → Archive → Unpack → Release，同一模板由真实 Studio 执行，SHA-256 逐字节比对 |
| AC03 部分失败 | 解压父子和坏来源、separate/combined 创建、成功清单消费；Studio 业务失败摘要及继续成功引用测试 |
| AC04 无秘密通道／缺密 | password/passwords/encrypt/secretReference 拒绝，真实加密 ZIP 缺密、无弹窗、无明文回显 |
| AC05 加密动作 | 未选入本轮；无独立秘密端口，不开放加密参数，不记为通过 |
| AC06 取消／退出 | 单元测试验证解压实际读取和创建实际写入取消，返回前清理；会话排空复用现有生命周期回归。真实宿主原生退出仍留后续实机验收 |
| AC07 重试／重复运行 | 必需 create-new，拒绝隐式覆盖，重复调用自动编号且保留原交付；不承诺幂等和自动恢复 |
| AC08 并发／GUI | 多次动作并发及独立 Scope，原 GUI 状态隔离与关闭回归；无动作单例会话或共享秘密 |
| AC09 普通页面 | 既有三类 Document 注册和界面回归，未新增 Workflow 设置；原生选择器/DPI 不作为新增完成项 |
| 补充协议 | 版本、字段、枚举、重复字段、格式组合、危险路径、输出预算与脱敏、目录 revision 漂移、SDK 共享 ALC |

通过数量、实际命令与环境以[结果](result.md)和机器摘要为准。历史 G0013 的 477 项不是本期新增测试证据；未运行项不能转记通过。
