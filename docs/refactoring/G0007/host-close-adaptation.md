# G0007 Host 同步释放与插件异步保存适配

## 已核对事实

目标 Host 为相邻 `avalonia_dock_simple_test` 的 `ea96b72`，工作树检查未发现本阶段对 Host 的修改。
只读源码显示 DocumentScopeManager 同步释放 Scope，PluginProviderOwner 同步释放私有 ServiceProvider。
HostRuntimeShutdown 先关闭工作区与 Scope，再等待插件 IPluginLifecycle.ShutdownAsync，之后决定释放或保留 Provider。
此处依据真实源码安排本地契约测试，不代表已运行完整 Host UI。

## 插件内实现

Document 与两个 Tool 同时实现 IDisposable 和 IAsyncDisposable。活动模型通过 PluginCloseCoordinator 登记关闭回调及 UI 同步上下文。
同步 Dispose 不阻塞等待 UI 续体，只启动登记的异步任务；重复关闭共享任务。异步 Dispose 可等待同一任务。
未显示且未编辑的 Tool 可直接幂等释放，不在停止后新建登记。

Coordinator 作为唯一 SDK Lifecycle 根登记，初始化不依赖视觉树。Shutdown 拒绝新登记、启动未关闭参与者，
等待全部任务完成，再排空 ProjectSessions。必要本地保存不因网络式取消跳过；Host 负责其超时和资源保留政策。
关闭异常被观察并保留登记，Shutdown 可重试并报告失败；失败不能宣称数据已受保护或强行提前释放根依赖。

根 ProjectSessions 增加同步 Dispose 兜底，其内部等待不捕获 UI 上下文；正常路径在此之前已经完成异步排空。
插件不释放 Host 端口，不引用 Host 内部程序集，不修改 SDK。Standalone 保留关闭前保存失败的显式阻止能力，
成功后也走同一 Coordinator，而非另写业务保存实现。
一旦 Scope 进入最终释放，失败时不再重新启用可能已经释放的编辑对象，只提供再次关闭的收尾路径；
前置保存检查失败则仍可继续编辑和重试。这避免把“关闭失败后显示窗口”误当作“对象仍可安全编辑”。

## 测试与限制

原生 Headless 调度中使用真实模块、标准 DI、真实 SQLite，先同步释放 Scope，再 await SDK Shutdown，最后同步释放 Provider。
验证正文与模板最后输入落盘、未显示 Tool 可释放、重复关闭幂等、在途任务不被提前放行、失败可重试。

尚未实机验证目标 Host 的 Dispatcher 停止时机、Dock 单页关闭、隐藏 Tool、卸载和整个窗口退出。
真实 Host 没有本插件自定义 IClosePreparation 的关闭否决接口；同时无法保存和恢复时只能报告生命周期失败与保留资源，
不能承诺已关闭的 UI 自动恢复。此项保留在 G0015 联调清单，不冒充已验收。
