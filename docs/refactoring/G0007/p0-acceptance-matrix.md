# G0007 P0 验收矩阵

2026-09-12：**P0 本地已验证，真实 Host 待验**。不是首版 AI 小说产品完成或发布结论。

| 场景 | 本地证据 | 状态 |
| --- | --- | --- |
| A01 无网络/无 Key 编辑、保存、TXT/Markdown 导出 | ProjectStorage/DocumentEditing/Connection/Artifact 测试 | 通过 |
| A02 模板独立快照、归档与源变化 | TemplateTests、模板交换真实 JSON | 通过 |
| A13 保存失败恢复与现有输出保护 | SaveRace/ProjectStorage/Artifact 测试 | 通过 |
| A15 明确稿件版本、选定正文复查、无秘密备份 | ArtifactTests，UTF-8 文件和 SQLite 副本 | 通过 |
| A16 一个 Document、两个共享 Tool 与作用域隔离 | CompositionTests、原生控件重建 | 本地通过 |
| SDK 同步 Scope/Provider 与异步 Shutdown 衔接 | PluginCloseTests，真实 DI 与 Headless UI 线程 | 本地契约通过 |
| 真实 Host 发现、Dock 隐藏、重开、关闭、重启 | 仅只读核对 Host ea96b72 关闭源码 | 待实机联调 |
| 开发私有 SQLite / DPAPI 资产 | G0005 干净 Debug 暂存与独立 ALC 探针 | 已有开发证据 |
| 模型生成与文学质量 | 只有窄端口、测试替身和 GPT-6 源码审查 | 尚未实现 |
| Windows CI、Release、ZIP、发布门禁 | 用户明确本阶段不执行 | 不适用 |

本阶段没有修改 Host 工作树、没有覆盖用户原作品、没有把个人目录或密钥放入测试夹具。
可复跑入口是 `./tools/verify-local.ps1`；原始阶段产物位于被 Git 忽略的 `artifacts/G0007/`。
