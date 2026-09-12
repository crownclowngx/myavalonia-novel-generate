# G0002 实施方案

[计划](plan.md) · [存储契约](project-storage-contract.md) · [会话生命周期](session-lifecycle.md) · [验证结果](result.md)

## 职责与依赖

遵守 SOLID，以单一职责划分领域、应用、持久化和展示，不引入通用框架。

| 位置 | 职责与理由 |
| --- | --- |
| `Domain/BookProject.cs`、`BookEdits.cs` | 不可变书卷章快照、合法性、卷章插入与复制身份；不依赖 UI、SDK、SQLite |
| `Application/Projects/ProjectPorts.cs` | 作品存储、可重建目录、恢复存储及跨进程租约四个窄端口；仅隔离真实外部边界 |
| `ProjectSessions` | 规范路径和书身份去重、租约获得、会话所有权；不持有 View |
| `ProjectSession` | 合并自动保存请求、单写入、编辑/数据库/恢复版本、关闭准备与释放 |
| `Infrastructure/Persistence` | SQLite、系统文件租约、恢复文件和本地目录的具体实现 |
| `MainDocument` / `MainView` | 本书交互、控件状态和编辑缓冲映射；不执行 SQL，卷章规则委托 Domain |
| `PreviewWindowInteraction` | 只在 Standalone 适配真实文件选择；生产使用 Host 的公开 SDK 端口 |

一个生产程序集，私有 DI 登记根服务；Document 由 SDK 创建 Scope。运行中的会话以身份登记，第二个写入者
明确拒绝，不通过全局当前书切换。模板和模型 Tool 尚未登记，避免提供空入口。

## 本地交互

可新建/打开 `.noveldb`、修改书名/创意、增加卷章、编辑提纲与正文、自动或显式保存、打开最近项目，
以及把失败恢复副本另存为独立新作品。文本框编辑期间允许暂时空标题，重开保留真实输入；新建需要书名。
空标题在目录展示为未命名，不用旧标题掩盖输入。新建不覆盖任何既有文件。

主视图使用原生 Avalonia、主题资源及普通控件。Avalonia Headless 在真实控件树验证中文输入和选章绑定，
没有将 HTML 产品图嵌入应用。完整审阅布局和 Host 命令属于 G0015。

## 阶段内修正

GPT-6 源码审查指出四项边界问题，已修正：跨书选择值相等时强制加载正文；关闭准备失败后保留编辑与保存能力；
恢复成功绑定编辑版本；单个不可读副本不遮蔽正常列表。对应回归见 `DocumentEditingTests`、`SaveRaceTests`。

原生渲染检查另修正窄窗口正文区最小高度，以及恢复目录尚未创建时不应显示清理失败的提示。
