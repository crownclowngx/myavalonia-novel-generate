# G0007 实施方案

基线 G0006 `507f2fe`。本阶段完成离线作品交换基础，并提前修复真实 Host 同步释放契约的已知差异。

## 职责

`Domain/ManuscriptExport.cs` 选择编辑稿/工作稿视图/正式稿、验证范围、重查选定正文、计算字数和生成 TXT/Markdown 内容。
`Application/Export/ArtifactService.cs` 协调用例；`Infrastructure/Export/ArtifactFiles.cs` 负责 SQLite 在线备份、模板 JSON 和新文件原子提交。
没有引入通用序列化框架或任意文件访问工具；输出类型明确，导出不遍历用户目录。

`MainDocument.Export.cs` 提供预览、提示确认、写入新文件、备份恢复与选定模板交换。预览绑定不可变作品快照和选择，
作品或选择变化后必须重新预览。导出复用本地规则纯计算，不借用编辑稿的旧检查证明历史稿。

## 提前处理的 Host 契约

只读核对目标 Host `ea96b72` 发现 Scope 和插件 Provider 都同步 Dispose。
原先仅 IAsyncDisposable 的模型会使标准 DI 同步释放失败，因此本阶段提前处理 G0015 的必要生命周期基础。
`PluginCloseCoordinator` 经 `UseLifecycle` 唯一注册，跟踪模型异步关闭。同步 Dispose 启动并登记任务，
SDK Shutdown 等待任务完成后才排空根 ProjectSessions；失败反馈 Host，不谎报安全释放。详见 [关闭适配契约](host-close-adaptation.md)。

## 模型端口

新增 `Application/Models/ITextModel` 窄端口，输入为明确提示与冻结连接，输出区分完整/截断及可空用量。
测试项目的 ScriptedTextModel 由测试显式传入响应或异常；生产组合不注册 Fake，也没有模型生成入口。
真实 Codex/DeepSeek 协议由 G0008 实现。

## 验证范围

SQLite WAL 和未提交事务、已知格式恢复迁移、路径占用/无效源、三种稿件、范围/字数/编码、模板独立导入与预览失效都有实际文件测试。
DI 关闭契约测试在 Avalonia Headless 上按“同步 Scope → 异步 Shutdown → 同步 Provider”执行，验证最后输入落盘。
真实 Host Dock/隐藏/重开及视觉树交互仍未实机验收；不把契约夹具称为 Host 实机验证。
