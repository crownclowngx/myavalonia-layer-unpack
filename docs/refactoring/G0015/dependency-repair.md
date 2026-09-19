# G0015 开工依赖修复记录

日期：2026-09-19。基线门禁在未改动业务源码时因 NU1403 失败：本项目四个依赖 Workflow SDK 的锁文件及相邻 Workflow Studio 两个锁文件记录了与官方包不一致的内容摘要。

旧摘要为 `rtWjoJAzYlOnOHFmBSyp3wGsfrX1gjvuVCvXU4IdbcPO5GDEowyoHjpnuhN/4zq2vE+aVR0l4yz9DCpJ3ZDg5w==`。从 `https://api.nuget.org/v3/index.json` 还原到本仓库隔离缓存后，`dotnet nuget verify <包路径> --all` 验证 NuGet.org Repository 签名成功，规范内容摘要为 `L1JKn+MbFcs1VkE3QmhLlZhPTAA01PGIkl6mPB+5dPdkF8XL13Ud1AhiMhdCs9uyXdg1Q25a1jG+dSk/Wp+7uw==`。

核对 nuspec 后，包 ID、版本 1.0.0、MIT 许可及对 PluginSdk 3.2.0 的依赖不变。使用官方源正常 `restore --force-evaluate -p:RestoreLockedMode=false` 重新生成锁文件，只更新这个包的内容摘要，然后恢复 locked restore 验证。不手工伪造哈希、不取消门禁、不替换全局缓存、不升级版本。

相邻 Studio 锁文件同步是本项目既有四组本地门禁所需的最小关联修复；不修改 Studio 业务代码。原始失败日志在本地 `artifacts/G0015/baseline/`。本修复与分卷业务实现分别提交，最终完整本地验证见[结果](result.md)。

## 完整门禁发现的测试宿主私有资产缺口

执行全量门禁时，五个跨 ALC 测试因默认域无法加载 `MyAvaloniaManagement.Icons` 而失败。生产项目已将 Icons 1.0.0 声明为私有资产；本地测试宿主未同步该规则。修复为集成测试显式引用现有精确版本，并在测试 PluginContext 中将 Icons 与解码库一样各自加载。只新增集成测试锁文件中的 Icons 1.0.0，不改变生产版本或对外协议。

修复后五项真实跨插件用例全部通过。第一次失败日志保存在 `artifacts/G0015/pre-final-gate/`，最终完整门禁另见结果；没有用跳过测试、部署正式插件或共享私有程序集规避失败。

## 本地差异检查的换行归一化

全部功能与集成测试通过后，最后的 Git 差异检查把 Windows 工作区 CRLF 与索引 LF 的差别误报为行尾空白。脚本的该条命令改用 `git -c core.autocrlf=input -c core.safecrlf=false diff --check`，与提交采用的归一化一致。只作用于当前命令，未修改全局配置；真实行尾空格与冲突仍会导致失败。
