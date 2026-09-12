# 原生分析工作流与 Host 验证

## 操作入口

在创作模板库展开“整本小说分析”，依次选择“导入 / 运行 / 报告”。导入先冻结预览快照，修改路径、编码或切分即使预览失效；确认后保存完整来源。书目分页读取，每页 50 条。模型采用已有连接的检查预设，预算覆盖全部阶段及失败重试。

运行面板显示已保存正文覆盖、每个节点的已保存/失败/正在处理/未处理状态，以及已知用量、未知请求数量和保守预留。正常节点自动推进。暂停在当前节点保存后停止，取消保留请求账本。更改连接或切分可创建修订，预算身份和历史费用不变。

报告可以在专题尚未齐全时读取已有部分；只有所有必需节点有效才标为完整候选。专题中每条结论列出 F 编号，选择依据后定位保存快照中的原文；TextBox 仅接收证据附近片段。Markdown 新文件导出带完整的覆盖、来源哈希及所引事实的简短原句。

## 已执行的自动化与真实 Host 代码路径

- 原生 Avalonia 控件树验证专题与证据选择、高亮选区、明/暗主题、100%/150% DPI、720/420 宽度以及工具控件移除/重建。
- 生产服务验证有效/过期预览、原文件删除后导入快照、两份独立运行切换、请求预算阻止发送、暂停、取消、费用复核、报告离线导出及旧目标保护。
- Host 的实际 Provider、Registry、Activator、Document Scope、Dock View 适配、LifecycleCoordinator 和同步根容器释放使用相邻 Host 源码执行。
- 已完成的真实 DeepSeek 报告数据库通过 SQLite 在线备份复制到探针目录；不复制连接或凭据。Host 对象图内成功读取七部分、定位原文、经文件选择端口导出 Markdown 并渲染截图。
- 新建隔离来源启动挂起模型替身，隐藏工具和关闭创作 Document 后分析继续；Host Shutdown 取消请求、排空 Activity，保存 Cancelled 检查点并成功释放容器。

本次通过目录：`artifacts/G0035/host-d57bae605e6e4bef8ec93cf43216c255`。原生截图：`artifacts/G0035/native`。探针没有发出新的付费请求，也未修改相邻 Host 源码。

## 完整桌面验收仍待补

系统文件选择器使用替身，控件运行在 Avalonia Headless 原生树；上述结果不能代表完整 Dock 菜单点击、实际系统对话框、桌面重启和跨显示器 DPI 操作。当前会话未提供 computer-use 所需的 node_repl/@oai/sky 控制入口，可用 CUA 也明确禁用原生控制。已读取 computer-use 技能并检查工具能力，没有通过其他方式绕过该限制。

因此 G0035-05 只完成本地及 Host 组合部分，完整桌面出口保留待验；G0036 的桌面签收仍不能标为通过。这是验证环境能力缺口，不是发布门禁或权限确认要求。

## 重跑

```powershell
$env:NOVEL_TEST_ARTIFACTS = '<新的原生截图目录>'
dotnet test tests/NovelGeneratePlugin.Tests --filter FullyQualifiedName~NovelAnalysisPanelTests
$env:NOVEL_HOST_PROBE_OUTPUT = '<新的 Host 验证目录>'
$env:NOVEL_ANALYSIS_SAMPLE_ROOT = '<已完成分析的独立数据库目录>'
dotnet run --project tools/HostProbe/HostProbe.csproj -c Debug -p:NovelHostRoot=D:/code/local/avalonia_dock_simple_test -warnaserror
```

不设置真实样本目录时仍验证新的隔离任务及退出；完整付费报告的离线复用部分相应不执行。全部属于本地开发验证，不运行 Windows CI、发布打包或发布门禁。
