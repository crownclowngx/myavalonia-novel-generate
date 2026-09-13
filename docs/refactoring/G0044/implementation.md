# G0044 实施说明

## 端到端与边界

增加 ReportTemplateAcceptanceTests，使用真实 SQLite 保存一份由可控模型产生的完整报告，删除外部 TXT，然后生成三类规范、自动交付、发布 v1。甲书只采用文风，乙书采用世界观与方法；修改甲书并发布模板 v2 后，新服务实例重读两书、原报告与 v1，核对快照独立、来源不变、转换只交付一次，且没有重跑全文节点。

G0041–G0043 的契约、故障注入、预算、并发、旧模板和 UI 回归继续作为验收矩阵的依据。新测试关注跨用例风险，不镜像每个私有实现方法。

## 原生 Host 验收入口

扩展现有 tools/HostProbe，增加仅开发使用的 DesktopProbe 和 ConversionFixtureModel。默认入口仍是 Headless 组合探针；bin 目录存在显式 desktop-probe.json 时才在 STA 线程进入生产 Host App、Shell、菜单、Dock、选择器和关闭链。

配置仅包含 OutputDirectory 与 AnalysisSampleRoot 两个绝对路径。以 SQLite Backup API 只读复制来源、运行与请求账本，不复制 catalog、连接文件或凭据。重开沿用同一测试目录，保留模板和转换历史，不再覆盖数据库。模型端口被明确的不联网夹具替换；原生测试不代表 DeepSeek 的真实转换质量。

编译入口：

```powershell
dotnet build tools/HostProbe/HostProbe.csproj -c Debug -p:NovelHostRoot=D:/code/local/avalonia_dock_simple_test -warnaserror
```

本轮原生数据位于 artifacts/G0044/desktop。交互通过 computer-use 执行，未替换 D:/data/avalonia 下的用户宿主或插件。探针不加入插件交付物、普通门禁或发布包；原生模式与普通 Headless 探针不能使用同一个仍启用的配置文件混跑。

## 验收中修复的界面问题

1. 打开生成草案成功后，清除上一次“请先保存当前编辑”的提示，避免成功行为仍呈现旧失败消息。流程测试同时验证拒绝与随后成功。
2. TemplateLibraryView 同时处理 Loaded 与已加载后的 DataContextChanged，调用模型既有的幂等初始化。新原生控件测试覆盖“先挂载 View，再绑定 Tool”，确保 Host 恢复布局的不同顺序不会遗漏模板加载。

## 配套 Host 退出修复

原生验收发现，在消息循环结束之后才执行插件 Shutdown，会让需要回 UI 的关闭续体超时。仅改插件页面或延长超时不能修正宿主顺序，因此在 Host 仓库同步修复：

- 同一 HostRuntimeShutdown 增加 PrepareAsync，在窗口仍存在时撤销入口并排空生命周期；最终 RunAsync 才释放或保留 Provider。
- MainWindow 在既有脏文档决策后保存布局、禁用交互、等待准备，再真正关闭；不重复保存已经拆除的空 Workspace 布局。
- 同步 Command/Workflow 排空不阻塞 UI；组合根通过窄的 HostDesktopClosePreparation 接入同一个关闭事务。

公共 SDK 和数据格式没有变化。Host 专用文档为 docs/plan-history/host-v5/desktop-close-preparation-acceptance.md。此修复需要在后续交付时更新宿主；只替换小说插件 DLL 不会给旧宿主补上该退出行为。
