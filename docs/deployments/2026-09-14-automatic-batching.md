# 2026-09-14 小说自动分批 1.3.0 本机发布

已按用户要求先提交源码，再编译并发布到 `D:\data\avalonia\Controls\NovelGeneratePlugin`。部署完成时间为 **2026-09-14 10:44:34（UTC+8）**，实际目录验证于 10:45:11 完成。原 1.2.0 已整体替换为 **1.3.0**，程序集版本为 `1.3.0.0`。

## Git 提交

源码提交：`899a528d43bc8ae17f8d7d740be02faa032095bf`。

标题：`feat(analysis): 支持小说长上下文自动分批并升级至 1.3.0`。

提交正文说明了原 12000 字符限制、按模型上下文合批、逐片段六维结果与证据、超限拆分、断点恢复、成功批次复用、schema 4 备份升级、界面与文档，以及本地验证范围。原先未提交的 `.gitignore` 修改未纳入本次提交。

## 构建与部署验证

- 锁定依赖还原通过，Release 构建 0 警告、0 错误；本次发布重新执行的 Release 回归 **457 项全部通过**，无失败或跳过，TRX 已归档。
- 使用 Build 包的 `BuildManagedPluginPackage` 生成正式 win-x64 ZIP 和配套摘要；包记录源码版本 `899a528d43bc`。通过干净的插件资产目录部署，没有直接复制普通 bin 输出。
- 核对安装目录及其祖先、内部路径，拒绝重解析点和越界路径；替换前再次确认宿主未运行。
- 发布前备份原 1.2.0 的 11 个文件并验证 SHA-256。仅整体替换 `NovelGeneratePlugin` 叶子目录，部署 11 个文件的路径、长度及摘要与包清单一致。
- 其他插件的 **514 个文件**在发布前后路径、长度及 SHA-256 一致。
- 暂存及实际部署目录均通过独立 ALC、入口类型、程序集版本、插件自带 SQLite 托管/原生依赖、中文作品往返、分析运行库及报告转模板库检查。共享 SDK 来自同版本 Standalone 运行目录，SQLite 不允许从该共享目录兜底。

插件 DLL SHA-256：`D5C9DC11121B6CB270246E26A8DB8DB67CBB955A30B4CABA16AF1FBA30103539`。

ZIP SHA-256：`B52E9A4A96DBC312B405219CBF140F5D91086D15F05EF5EAA89A7AAC4895E01B`。

## 制品、备份与回执

本机证据根目录：`artifacts/deployments/20260914-104011-automatic-batching/`（Git 忽略）。

| 文件或目录 | 内容 |
| --- | --- |
| `NovelGeneratePlugin.Plugin-1.3.0-win-x64.zip` | 正式安装包 |
| `NovelGeneratePlugin.Plugin-1.3.0-win-x64.manifest.json` | 配套包及文件摘要、源码版本 |
| `previous-plugin/` | 原 1.2.0 完整备份 |
| `staged/Controls/NovelGeneratePlugin/` | 已核对的发布文件 |
| `deployment-receipt.json` | 实际部署时间、版本、摘要及验证回执 |
| `previous-files.json`、`staged-files.json`、`deployed-files.json` | 替换前、暂存及安装目录清单 |
| `other-plugins-before.json`、`other-plugins-after.json` | 其他插件未变的证据 |
| `tests/release-tests.trx` | 本次 457 项 Release 回归结果 |
| `stage-probe.log`、`deployed-probe.log` | 暂存及实际目录的存储验证 |

## 使用与回退

重新启动安装目录的宿主，在“创作模板库 → 整本小说分析 → 运行”勾选“根据模型上下文自动分批”，先预览批次数再新建分析或修订。目标输入默认 20 万、最高 80 万 token；实际大小同时受模型窗口与输出余量约束，旧小说不必重新导入。详见[自动分批说明](../automatic-novel-batching.md)。

回退时先退出宿主，核对上述备份摘要后整体恢复 `previous-plugin/` 到同一插件叶子目录。新版本首次访问运行库会先创建 SQLite 一致性备份再升级 schema 4；如果已经使用新版本升级过运行库，回退旧插件还需恢复匹配的运行库备份，保留升级后数据供日后使用。

本次没有改动 Host 文件、访问用户工作区数据库、启动宿主或发送真实模型请求。存储检查使用新建合成数据，不等同于真实长篇小说的语义质量验收。
