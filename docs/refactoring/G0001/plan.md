# G0001 产品壳与模板清理计划

日期：2026-09-12。用户已授权实现软件并清理无用 demo。

依据[总计划](../../implementation-plan.md#g0001)。当前无 Git 提交，初始 36 文件摘要见 [baseline-files.json](baseline-files.json)，原件归档在忽略的 artifacts/baseline/pre-g0001.zip。

基线：.NET SDK 10.0.302 / win-x64，locked restore 成功，Debug 零警告/错误，4 个模板测试通过。

移除示例消息、一次性演示命令及菜单身份、演示输入框与模板测试。保留生产程序集、稳定 Plugin/Document 身份、图标、独立预览和测试工程。补充实际组合、取消与 Scope 隔离回归。

预览读取同一个 Module，异步初始化，关闭取消并排空。它不实现 Host 的 Dock、授权或 JSON 持久化。工作项为 G0001-01 基线、02 清理与产品壳、03 异步预览、04 验证与文档。
