# 2026-09-12 本机 Controls 部署记录

应用户要求编译项目并部署到 `D:\data\avalonia\Controls`。源码基线：`fd1a470`；配置：Release，目标：win-x64。

## 部署结果

- 插件目录：`D:\data\avalonia\Controls\NovelGeneratePlugin`，此次为新建，不存在旧小说插件需要替换。
- 插件身份：`myavalonia.plugin.novel.generate`，版本 `1.0.0`，manifest schema 2。
- 入口：`NovelGeneratePlugin.Plugin.dll` 中的 `NovelGeneratePlugin.Plugin.NovelGeneratePluginModule`。
- SDK 兼容声明：`[3.4.0, 4.0.0)`。
- 使用 Build 包的 `DeployManagedPlugin` 目标筛选部署资产，共 11 个文件，包含 SQLite 私有托管依赖与 win-x64 原生库。

## 验证

锁定依赖还原通过；Release 构建 0 警告、0 错误；Release 测试 275 通过、0 失败、0 跳过。
310 个 Markdown 链接及两份 HTML 来源哈希校验通过。

直接针对部署目录执行独立 AssemblyLoadContext 存储探针，项目创建/读取、原生 SQLite、中文双字姓名与别名检索通过。
部署 DLL 的 SHA256 与本次 Release 输出一致；Controls 下其他插件的 514 个既有文件部署前后 SHA256 一致。

本地逐文件大小与 SHA256 回执保存在 `artifacts/deployments/20260912-223155/deployed-files.json`；相邻的 `other-plugins-before.json` 为部署前既有文件摘要。回执目录不加入源代码版本库。

## 使用与验证边界

部署前宿主进程未运行。用户可启动 `D:\data\avalonia\MyAvaloniaManagement.exe`，在宿主中查看插件状态并打开“小说创作”“创作模板库”“模型连接”。

本次没有启动桌面宿主，部署目录探针不替代该宿主实际加载和界面交互验收。未改动宿主程序、其他插件或用户作品，也未生成正式 ZIP、执行远程发布或 Windows CI。

操作步骤见 [HTML 用户说明书](../product/novel-workbench-user-manual.html)。此次仅更新部署记录，首版工程候选的产品验收边界保持不变。
