# G0044 验收矩阵

日期：2026-09-13。工程与原生交互通过，真实模型转换和内容评阅待验。

## 自动化证据

| 目标/风险 | 证据 | 结果 |
| --- | --- | --- |
| 保存原文、重开报告、外部 TXT 删除、缺失结果拒读、超过 100 条历史 | ReportTemplateStorageTests 与 NovelReportTests | 通过 |
| 三类规范与长期规则、枚举、引用、容量上限、推断不升级为硬规则 | ReportTemplateStorageTests、ReportTemplateWorkflowTests | 通过 |
| 分组与归并覆盖、预检、预算不足、单次纠正、截断/费用未知 | ReportTemplateExecutionTests | 通过 |
| 候选写失败、恢复文件失败、账本离线重建、已成功节点复用 | ReportTemplateExecutionTests | 通过 |
| 草案保存后完成标记失败、并发固定身份、用户编辑后重试 | ReportTemplateWorkflowTests | 通过 |
| 当前编辑保护、版本列表刷新、隐藏不取消、明暗主题与缩放 | ReportTemplateWorkflowTests | 通过 |
| 分维度采用到两书、各自编辑、模板 v2 不影响旧快照、重新实例化存储 | ReportTemplateAcceptanceTests | 通过 |
| View 先加载、模型后绑定的重开顺序 | TemplateLibraryViewLifecycleTests | 通过 |
| Host 关闭准备不提前释放 Provider，原有失败保留仍生效 | HostLifecycleOwnershipTests，26 项专项 | 通过 |
| 无脏文档也等待异步准备、重复关闭共享一次、布局不被空值覆盖 | Host ApplicationAndWindowTests 新增用例 | 通过 |

插件完整本地门禁：441 项测试，零失败/跳过，locked restore、Debug 零警告/错误、格式与文档检查通过。Host 完整 verify：811 项通过，随后新增 UI 用例独立通过，合计覆盖 812 项。没有执行 Windows CI、seal 或正式发布门禁。

补跑 Host 无窗口组合探针通过：生产 Provider/Registry/Activator/DocumentScope、双书隔离、工具单例、保存重开、真实报告离线读取与引用定位、隐藏工具保持分析以及退出排空取消均满足断言。日志为 `artifacts/G0044/host-composition-verified/probe.log`，此项不替代下述原生窗口验收。

## 已执行的原生步骤

使用生产 Host 3.0.0 源码、插件 1.1.2.0 开发程序集及本轮修复。程序集版本不是新发布版本，确切变更以本轮 Git 提交为准。插件模块通过隔离测试 Catalog 注入，未验证本轮发行 ZIP 或跨 ALC 部署。

1. 从真实工具中心显示“创作模板库”，调整 Dock 宽度。
2. 输入一段未保存世界观，展开“整本小说分析”，读取已有 DeepSeek 完整报告候选。
3. 在“从报告生成模板”中预检输入，以不联网夹具生成并自动保存草案。
4. 点击“打开生成草案”时保留未保存编辑并显示提示；保存原草案后再打开成功。
5. 查看三类规范、来源身份，保存不可变 v1。
6. 退出、重新启动同一隔离目录，从模板列表恢复 v1；重新读取原报告及转换历史，仍为同一转换任务和目标模板，费用记录可见。
7. 修复 Host 退出顺序后再次关闭，退出收据确认 RuntimeShutdownSucceeded=true，最新诊断会话没有生命周期超时。重开阅读期间 FixtureRequests=0、NetworkCalls=0。

原生窗口截图在本次 computer-use 会话中逐步检查，未伪造为 Headless 截图。关键本地收据：

- artifacts/G0044/desktop/desktop-input.json：保存的报告、转换、模板身份与 v1 数量。
- artifacts/G0044/desktop/desktop-exit.json：最终真实 Runtime 退出结果。
- artifacts/G0044/desktop/host/Diagnostics：包含修复前超时与修复后正常会话，保留失败证据。

## 本次报告与转换范围

来源为既有隔离实测档案 artifacts/G0032/deepseek-full-01，一致性副本内的 RunId 为 fe239aa9-3ea7-4d84-b094-bcab9ea785f4，报告版本为 A6ADB5D61DD222ABE1E95832CCBBBF8CB79A2F2FE15823710E6D617DD4F8E531。原分析使用 deepseek-flash，提供的 TXT 覆盖 51,071/51,071 UTF-16 字符；不因此证明原书已经完结或该 TXT 包含原书全部章节。

转换选择世界观、文风、写作方法，固定输入包含 128 条结论/疑问；保守输入估算 90,218 token，单次输出预留 8,192，上下文 131,072，计划一次请求，总上限 8 次/500,000 token。夹具返回一次，账本记录的 220 token 是测试值，不能记为真实 DeepSeek 用量或费用。

## G0044-04 待验

需要本次选用的实际连接、请求/token 预算，然后对已有报告执行一次有界真实转换。过去的一次性 Key 不作为本轮默认凭据。验收至少记录：

| 内容 | 当前状态 |
| --- | --- |
| 本次真实模型、连接身份、冻结参数、预算及实际已知/未知用量 | 待执行 |
| 描述性结论是否变为可执行的创作要求 | 待评阅 |
| 是否误带原书专名、人物历史或不适用设定 | 待评阅 |
| 来源、条件、推断/建议与硬规则边界是否准确 | 待评阅 |
| 人工接受比例、修改内容及修订耗时 | 待作者评阅 |

已有真实“分析报告”加上可控“转换夹具”只证明工程链路；不能把两者组合声称真实 AI 转换质量已经通过。
