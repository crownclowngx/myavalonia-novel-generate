# G0016 Host 组合与桌面证据边界

目标 Host：相邻 avalonia_dock_simple_test，当前源代码保持未修改；使用 Debug 本地组合探针。

## 已通过的真实 Host 代码路径

- 注册表严格校验一个 Document、两个 Tool 和七项命令/菜单；首轮发现 PlacementId 所有者前缀错误，修复后通过。
- Host PluginProviderOwner 与 PluginContributionActivator 创建两个 Document Scope，模板/连接各一个共享 Tool。
- 真实 ManagedDocumentDockable、ViewLocator、DocumentControlRecycling 构建并绑定两个独立原生 View。
- 两书分别编辑和保存；通过 Document Target 预览导出；关闭甲书时乙书与共享 Tool 保持可用。
- 甲书通过 Host 命令开始模型任务，Host 同步 Dispose 后插件等待在途模型取消退出；重开甲书保留正文。
- Host LifecycleCoordinator 初始化与 Shutdown 排空后释放 Scope/Provider。

截图为 `host-composition.png`，实际输出目录记录在 artifacts/G0016/host-path.txt。
探针保留随机独立目录，不读写作者真实作品；只对样本使用测试文件选择器、取消模型替身和测试目录注入。

## 尚未取得的桌面证据

实际系统文件选择器、完整 Dock 标签拖放/工具隐藏再显示、菜单点击路由、真实 IME 候选窗、多显示器 DPI 切换和桌面退出尚待交互验收。
当前会话的原生桌面控制接口不可用；没有用普通窗口截图或本地模型测试替代以上结论。

独立暂存目录的 ALC/SQLite/中文检索由既有 probe-staged-storage.ps1 验证；本组合探针使用 Host 源码工程引用，
不冒充正式包经完整目录发现和独立 ALC 加载。Release/ZIP 和发布验证留到 G0028 的明确发布任务。

## 重跑

```powershell
dotnet run --project tools/HostProbe/HostProbe.csproj -c Debug -p:NovelHostRoot=D:/code/local/avalonia_dock_simple_test
```

可设置 NOVEL_HOST_PROBE_OUTPUT 为新输出目录。项目使用 Host 既有测试友元程序集名，只是本地验证工具，不能进入插件交付目录。
