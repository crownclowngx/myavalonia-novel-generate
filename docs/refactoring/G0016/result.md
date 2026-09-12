# G0016 实施结果

日期：2026-09-12。基线 `af373c3`（270 项测试）。

G0016-01、02、05 的工程矩阵、组合回归和文档已完成；G0016-03 真实三章生成通过，作者签收待补；
G0016-04 Host 组合通过，完整桌面交互仍待补。当前结论为 **工程候选已验证，P1 产品验收未完成**。

实际 Codex GPT-6 生成 3 章工作稿，487/498/477 字，6 次请求，90187 token，未知用量 0，未修正、未定稿。
真实 Host 严格注册发现并修复菜单身份前缀，双书/双 Tool、Scope、View 适配、保存重开、在途任务关闭和 Shutdown 通过。
新 HTTP/SSE 组合测试覆盖成功三章与第二章截断/断流/认证错误，以及改写不能绕过本地硬规则。

证据见 [A01–A16 矩阵](p1-acceptance-matrix.md)、[样稿](sample-evaluation.md)、[Host 边界](host-integration.md)、[已知限制](known-limitations.md)。
可重跑工具保留在 tools/LiveSample 与 tools/HostProbe，二者不加入普通门禁或产品交付物。

## 最终本地验证

- `tools/verify-local.ps1` 通过：275 项测试，0 失败、0 跳过；Debug 构建 0 警告、0 错误；格式校验、296 个 Markdown 链接及产品 Markdown/HTML 来源哈希校验通过。
- 独立暂存目录 `artifacts/G0016/stage-verified-774e11ef9c3640d6b2e0d62dcd311e28/NovelGeneratePlugin` 包含 11 个文件；独立 AssemblyLoadContext 内的 SQLite 保存、重开及中文双字姓名/别名搜索通过。
- Host 源码基线为 `ea96b72`，源码工作区保持干净；组合验证输出位于 `artifacts/G0016/host-verified-1cf7690d5fec426d92eee8b10d52863d`。
- 可复跑的 `tools/LiveSample` Debug 构建通过；未重复执行付费生成。样稿请求与用量以本阶段已归档的一次真实运行计数。
- 产品 HTML 浏览器检查通过：实际 Avalonia 界面图片完整载入，原始尺寸为 1200×850，无页面横向溢出；概念示意图与实际界面有明确标注。

以上均为开发阶段本地验证，不代表完整桌面交互、作者文学质量签收或发布验收。
