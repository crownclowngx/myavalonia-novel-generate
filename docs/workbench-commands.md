# Workbench Command 开发说明

Plugin SDK `3.4.0` 允许插件把少量高价值的用户意图声明为 Workbench Command，使同一语义动作可由
Host 菜单、快捷键或后续 Command Palette 投影。Command 不是 Avalonia `ICommand` 的替代品；按钮点击、
表单编辑、拖放和只对单个控件有意义的局部交互，继续使用插件自己的命令或业务用例即可。

## 当前实现

G0015 已登记七项小说 Document 命令：打开、开始、暂停、取消、生成本章、人工定稿和导出预览。菜单 PlacementId 必须使用插件前缀加 `.command-placement.`，真实 Host 会严格拒绝错误所有者身份。
不贡献新全局快捷键；选区操作仍为局部命令。普通小说项目保存不能假定由 Host Ctrl+S 信封完成。

详见[实际交互与命令契约](refactoring/G0015/interaction-and-command-contract.md)和[Host 组合验证](refactoring/G0016/host-integration.md)。
注册只保存身份、显示元数据与目标类型；调用可等待，外部取消传给任务，关闭后拒绝重入。

## 适配既有局部命令

已有 Document 可以在 Target 内委托同一个业务用例或可等待命令，但公共身份始终是 `CommandId`。如果现有
入口只有 `ICommand.Execute` 并启动 `async void`，应先提取可等待业务方法或使用明确的异步命令 API；不能让
Host Executor 在真实工作尚未完成时误报成功。

不要在 Target 中解析 Host 或插件根容器。需要的依赖应由 Document Scope 在构造时显式注入；Target 只表达
当前实例能够做什么，不承担 Catalog、菜单排序、快捷键冲突或 Dock 生命周期。

## 测试清单

- 已知与未知 `CommandId` 的 `CanExecute`；
- 当前实例执行后不影响同类型的另一个实例；
- `CommandStateChanged` 只通知真实变化的命令；
- 取消令牌在修改业务状态前生效；
- 重复执行、未知身份和业务失败有明确异常；
- Module 只为自己拥有的 Document 注册命令；
- 默认模板没有快捷键贡献；
- 最终 ZIP 由真实 Host 在独立 ALC 中加载，SDK 仍来自 Default ALC。

Standalone 只承载同一份 `MainDocument` 和 View，不模拟 Host 的 Catalog、活动 Document 路由或菜单投影。
当前阶段用 Debug 暂存与真实 Host 组合验证；完整桌面操作待补，正式 ZIP/发布门禁留到明确发布任务。
