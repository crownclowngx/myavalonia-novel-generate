# G0039 实施方案

本阶段把“单次装不下”和“全过程预算不足”分开处理。容量估算、原文切分、运行调度、事务存储分别负责自己的规则，继续使用现有服务与端口，不引入工作流框架。

| 位置 | 责任与设计思路 |
| --- | --- |
| ModelInputCapacity | 以系统提示、正文、契约的 UTF-8 字节数加适配余量做保守输入估算；输出上限独立预留，并与已知或显式上下文容量比较。 |
| NovelTextPartitioner.Split | 只二分问题区间，优先换行并避开 Unicode 代理对；两份正文连续覆盖原区间，保留章节身份。 |
| NovelAnalysisRunService / NovelAnalysisSplitting | 仅在提取阶段、输入预检超限或已知费用的长度截断时安排子单元；先检查深度、大小、剩余请求和报告预留。 |
| AnalysisSplitRules / AnalysisRunStore | 同一事务保存父节点历史、子节点、当前切分；拒绝借拆分改写其他成果。旧请求留在累计账本。 |
| NovelAnalysisPanel / View | 显示五阶段参数、单次容量和拆分层数，进度列出实际输入估算及输出预留。 |
| AnalysisProbe replay | 只读打开用户库并用 SQLite Backup API 复制；在副本重算成功节点指纹和脱敏响应诊断，不连接模型。 |

运行库升级到 schema3；schema1/2 均在升级前备份。已部署的 v3 提取提示、输入指纹和无可选证据的旧结果序列化保持兼容。新的 v4 提示与契约允许 Gaps 可选 Evidence，明确输出上限是上限而非数量指标。

完整 JSON 后断流仍不可自动纠正或采纳。SSE 累计封装容量随单次输出上限有界调整，避免固定封装上限与输出配置冲突；单行、事件、正文和超时防护仍保留。真实回放发现的 type=json_object 格式定义回显纳入 SchemaEcho 分类。

具体边界见[容量与局部拆分协议](capacity-and-splitting-contract.md)，验证见[阶段结果](result.md)。
