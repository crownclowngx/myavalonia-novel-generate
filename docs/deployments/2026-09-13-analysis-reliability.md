# 2026-09-13 分析稳定性修复本机覆盖部署

按用户要求，将 G0037–G0039 修复编译为 **1.1.1**，于本机时间 2026-09-13 16:50:47（UTC+8）覆盖到 `D:\data\avalonia\Controls\NovelGeneratePlugin`。构建为 Release、win-x64，程序集版本 1.1.1.0，稳定插件身份及 SDK 范围保持原配置。

业务源码基线为 `a7bb602`，本次另行更新 PluginVersion。三阶段修复进度见[稳定性计划](../analysis-reliability-plan.md)，具体操作见[用户说明书](../product/novel-workbench-user-manual.md)。

## 本次交付

- 区分格式定义回显、结构错误、输出截断与断流，完整且用量已知的格式错误最多一次自动纠正。
- 五阶段独立模型参数；新建修订保留范围不变的成功成果，未完成节点使用新设置。
- 单次容量预检、仅问题提取单元的局部拆分，以及预算、深度和最小正文边界。
- 拆分事务保存与恢复，保留父操作、历史费用和原文坐标，继续运行不会意外重发已知截断父请求。

启动宿主后，在“创作模板库 → 整本小说分析 → 运行 → 分析专用参数”设置。已有运行继续时使用冻结参数；需采用新默认值时新建修订。未知用量仍需先复核，升级不自动重新发起分析。

## 验证与替换

锁定还原、Release 构建和 402 项 Release 测试全部通过；构建零警告、零错误。使用 Build 包的 DeployManagedPlugin 目标筛选 11 个部署文件，包含声明的 SQLite 私有依赖，没有复制普通 bin 输出。

覆盖前确认宿主未运行；核对目标绝对路径、MSBuild 求值路径和重解析点，完整备份旧版 1.1.0。实际部署的逐文件路径、长度及 SHA256 与暂存产物一致，主 DLL 也与 Release 输出一致。其他插件 514 个文件的路径、长度及 SHA256 保持一致。

暂存与实际目标目录的存储探针通过：独立加载上下文、入口类型、1.1.1.0 程序集版本、部署目录的 SQLite 托管/原生依赖、中文作品创建/回读及分析运行库可用。安装宿主为单文件 EXE，因此探针从 Release 测试输出提供相同 SDK 共享程序集；没有将 SDK 复制进插件，也不将该探针等同于桌面宿主人工验收。

插件 DLL SHA256：

```text
C5508EFD81FB7ECD47E5FD4FCCFA1A6DCBA84BEF849059290AAEFF8469C07A62
```

## 备份和回执

本机证据根目录为 `artifacts/deployments/20260913-164415-analysis-reliability/`，被 Git 忽略。

| 文件或目录 | 内容 |
| --- | --- |
| previous-plugin/ | 1.1.0 的 11 个原插件文件 |
| staged/NovelGeneratePlugin/ | 筛选后的 1.1.1 暂存产物 |
| previous-files.json、staged-files.json、deployed-files.json | 逐文件摘要 |
| other-plugins-before.json、other-plugins-after.json | 其他插件不变的摘要证据 |
| deployment-receipt.json | 时间、版本、来源提交、备份、哈希与验证结果 |
| deploy.ps1、storage-probe.ps1 | 本次路径检查、替换及隔离存储验证脚本 |

首次启动新版读取旧运行库时，会按 schema3 规则先建立 SQLite 一致性备份再升级。程序文件备份与运行库备份分别保存；回退旧程序时应退出宿主并恢复匹配的运行库备份。

本次没有启动桌面宿主、生成 ZIP、触发 Windows CI 或新增付费 AI 请求，没有访问用户小说/分析数据库进行写入。程序已覆盖，完整桌面交互及人工语义质量验收继续按原计划执行。
