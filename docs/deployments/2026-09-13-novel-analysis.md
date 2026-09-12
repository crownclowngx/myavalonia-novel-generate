# 2026-09-13 整本小说分析本机覆盖部署

按用户要求编译并直接覆盖 `D:\data\avalonia\Controls\NovelGeneratePlugin`。部署时间为本机时间 2026-09-13 07:04:54（UTC+8），插件版本由 1.0.1 更新为 **1.1.0**，构建配置为 Release，目标平台为 win-x64。

业务源码基线为 `f853b87`，本次额外更新 `PluginVersion`；程序集版本由 Build 包统一派生为 `1.1.0.0`。插件身份仍为 `myavalonia.plugin.novel.generate`，manifest schema 为 2，SDK 范围为 `[3.4.0, 4.0.0)`。

## 交付内容与使用入口

本次部署包含 G0029–G0036 当前已实现的 TXT 来源导入、全文分块提取、跨章整合、预算和检查点恢复、六专题与综合报告、原文依据定位及 Markdown 导出。

启动宿主后，在“创作模板库”展开“整本小说分析”，依次使用“导入”“运行”“报告”页签。模型连接和操作步骤见[用户说明书第 9 节](../product/novel-workbench-user-manual.md#9-整本小说分析从-txt-到综合报告)。

## 验证与部署结果

- `dotnet restore NovelGeneratePlugin.slnx --locked-mode` 通过。
- Release 构建使用 `--no-restore -warnaserror`，0 警告、0 错误。
- Release 测试使用 `--no-build`，377 项通过，0 失败、0 跳过。
- 部署前确认宿主和插件进程未运行，校验目标叶子目录、祖先目录及内部路径没有重解析点，并核对 MSBuild 求值后的部署位置。
- 先使用 Build 包的 `DeployManagedPlugin` 目标生成干净暂存目录，通过独立加载上下文的 SQLite 存储探针，再完整备份旧插件的 11 个文件并逐项核对 SHA256。
- 使用同一目标重建指定插件目录，部署 11 个文件；每个文件的相对路径、长度和 SHA256 均与已验证暂存目录一致，主 DLL 摘要同时与 Release 输出一致。
- 对实际部署目录再次运行存储探针，独立 ALC、win-x64 SQLite 原生库、项目创建/读取、中文姓名和别名检索全部通过。
- 其他插件的 514 个文件在部署前后路径、长度和 SHA256 一致。
- 文档链接、产品及说明书 Markdown/HTML 来源哈希、Git 空白检查通过。

插件 DLL 的 SHA256：

```text
524D51BA87FB27B7B9ADAD138A4BB7910C60A18F9A48A570B48C82687C8C92BC
```

## 备份与回执

本机记录根目录：`artifacts/deployments/20260913-070226-novel-analysis/`，位于本仓库且已被 Git 忽略。

| 相对路径 | 内容 |
| --- | --- |
| `previous-plugin/` | 覆盖前的 1.0.1 完整插件目录 |
| `previous-files.json` | 旧插件路径、长度与 SHA256 |
| `staged/NovelGeneratePlugin/` | 已验证的 1.1.0 暂存产物 |
| `staged-files.json`、`deployed-files.json` | 暂存与实际部署逐文件摘要 |
| `other-plugins-before.json`、`other-plugins-after.json` | 其他插件部署前后摘要 |
| `deployment-receipt.json` | 部署时间、版本、目录、摘要与验证结果 |

如需回退，完整退出宿主，重新核对目标路径和备份摘要后，用 `previous-plugin/` 整体替换指定插件叶子目录，再启动宿主。备份放在仓库 artifacts 中，不在 Controls 中保留第二份同身份插件。

## 验收边界

这是本机直接覆盖部署，未生成 ZIP、触发 Windows CI、启动桌面宿主或新增付费 AI 请求。部署文件仅包含插件及声明的私有依赖，未携带小说原文、分析数据库或 API Key。

部署成功证明 Release 产物、依赖和存储路径可用；完整桌面交互、百万字符真实小说和人工语义质量验收仍按[分析实施计划](../novel-analysis-feasibility-plan.md)保留待验。当前阶段进度仍为 6/8、工作项 35/40，不因本次部署变更为 M3 完成。
