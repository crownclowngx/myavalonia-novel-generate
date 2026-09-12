# G0034 实施方案

调度仍由 `NovelAnalysisRunService` 统一负责。`AnalysisTarget.Report` 在完整提取和整合之后追加 Summary、六个 Dimension、Synthesis 节点；节点材料选择、层数、依赖和结果与检查点一并持久化。模型调用继续经过 `ModelRequestService`，没有第二套付费入口。

`NovelReportPlanner` 只决定有界材料和依赖。`NovelReportRequests` 负责请求、证据允许集合与输入指纹。`NovelReportContract` 拒绝未知字段、越界证据、无内容输出和可识别的确定性提升。`NovelAnalysisReportService` 从保存结果投影报告与定位原文；`NovelReportMarkdown` 是纯格式化器，文件交互留给 G0035。

首层汇总最多 80 条原始事实，保留本地全量索引；上层合并 2–3 个有界子摘要直到单根。专题同时读取根摘要、相关连续性观察和按固定规则选取的原句，避免只对压缩摘要评价文风。输入与输出均有限制，较大的真实规模可能触发预算暂停，不能借层级追加绕过总额。

真实整合发现模型将 Plan 写入 Role，以及旧 40 条观察上限低于 60 条输入事实的合理输出密度。现保持 Role 严格枚举，明确 Plan 属于 Narration；观察上限调整为 80，并有 42/81 条边界回归。对已付费完整响应，只在当前输入指纹相同、已知用量、协议失败且当前契约验证通过时，允许显式本地复核采纳；保留原失败账本，不重新调用模型。

显式重试可保存最多 2000 字符的补充约束，参与输入指纹；旧成功节点不允许修改。修订运行在来源、切分及冻结模型一致时继承已保存的整合选择与复核说明，再逐节点校验缓存。详见[报告与综合契约](report-and-synthesis-contract.md)。
