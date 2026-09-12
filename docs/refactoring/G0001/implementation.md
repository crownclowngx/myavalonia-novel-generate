# G0001 实施方案

[计划](plan.md) · [基线](baseline.md) · [结果](result.md)

删除示例 Message/ApplyWorkbenchMessage、菜单贡献及对应演示测试；保留 PluginId、主 DocumentTypeId、
公开 SDK、原生 Avalonia View 和单一生产程序集。MainDocument 改为小说标题、创意、状态和异步生命周期。
本阶段的新建/打开按钮明确不可用，真实项目操作属于 G0002。

Standalone 通过 PreviewRegistration 收集真实 Module 的贡献并构建开发容器，按文档创建 AsyncServiceScope；
初始化可等待，关闭取消初始化并先释放文档 Scope、后释放根容器。PreviewRegistration 仅供开发和测试，
不复制生产贡献清单，不进入插件包。DI 完整容器包仅加入 Standalone/Tests，版本对齐本地 Host 的 10.0.10。

新增注册/身份/两实例隔离、取消初始化及重复释放测试。开发脚本检查退出码、锁定还原、构建、测试、格式和
Markdown 本地链接/锚点。校正 SDK 版本及 Workflow 双角色说明，删除把演示命令当作当前实现的说明。
