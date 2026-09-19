# G0015 开工依赖修复记录

日期：2026-09-19。基线门禁在未改动业务源码时因 NU1403 失败：本项目四个依赖 Workflow SDK 的锁文件及相邻 Workflow Studio 两个锁文件记录了与官方包不一致的内容摘要。

旧摘要为 `rtWjoJAzYlOnOHFmBSyp3wGsfrX1gjvuVCvXU4IdbcPO5GDEowyoHjpnuhN/4zq2vE+aVR0l4yz9DCpJ3ZDg5w==`。从 `https://api.nuget.org/v3/index.json` 还原到本仓库隔离缓存后，`dotnet nuget verify <包路径> --all` 验证 NuGet.org Repository 签名成功，规范内容摘要为 `L1JKn+MbFcs1VkE3QmhLlZhPTAA01PGIkl6mPB+5dPdkF8XL13Ud1AhiMhdCs9uyXdg1Q25a1jG+dSk/Wp+7uw==`。

核对 nuspec 后，包 ID、版本 1.0.0、MIT 许可及对 PluginSdk 3.2.0 的依赖不变。使用官方源正常 `restore --force-evaluate -p:RestoreLockedMode=false` 重新生成锁文件，只更新这个包的内容摘要，然后恢复 locked restore 验证。不手工伪造哈希、不取消门禁、不替换全局缓存、不升级版本。

相邻 Studio 锁文件同步是本项目既有四组本地门禁所需的最小关联修复；不修改 Studio 业务代码。原始失败日志在本地 `artifacts/G0015/baseline/`。本修复与分卷业务实现分别提交，最终完整本地验证见[结果](result.md)。
