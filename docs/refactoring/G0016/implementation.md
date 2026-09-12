# G0016 实施方案

`ProductAcceptanceTests` 把真实 DeepSeek HTTP/SSE 适配接入连续运行和 SQLite，用受控 HttpMessageHandler 提供正文、结构化审校、截断、断流与认证错误。
验证三章自动承接、完整报告、整段定稿/导出，以及第二章故障时只保留第一章、读取不新增请求。协议费用记录不包含测试密钥。

`tools/LiveSample` 是明确收费的独立控制台入口，复用生产连接、模型路由、预算、章纲、单章审校、连续运行和会话存储；
只在传入 `--run-paid-sample` 时执行，输出目录必须为空，不纳入普通单元门禁。真实样稿与输入、报告、用量和哈希分别保存。

`tools/HostProbe` 是本地 Host 组合探针，不加入插件解决方案与交付物。它引用当前 Host 源码，使用 Host 已有测试友元身份，
实际执行 Provider/Registry/Activator/DocumentScope/ManagedDocumentDockable/ViewLocator/Recycling 与 Host Lifecycle。
模块装饰器只注入临时数据目录和可取消模型替身，文件选择器也为测试端口；不把它称为完整桌面 Host 操作。
首次运行发现菜单 PlacementId 缺少 `command-placement` 段，实际 Host 拒绝整个插件；已修复并加入注册回归。

文档门禁新增 Markdown 与 HTML 源哈希一致性检查，以及本阶段文件的 Git 空白检查。
核心业务保持 SOLID 职责划分，没有引入发布 CI、AIFLOW 或额外业务框架。
