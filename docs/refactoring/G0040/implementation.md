# G0040 实施方案

根因有两部分：ChunkAnalysisContract 本地要求每条观察的 Evidence 为 1–8 条，v4 Schema 却只声明 array；业务校验抛出的普通异常经模型边界转换后又丢失具体原因。模型收到的纠正提示因此仍然笼统。

新增 ModelContractIssue 只承载固定规则和数值，ModelContractException 承载由本地构造的字段路径。模型边界继续对白名单路径校验，转换为 ContractMismatch；ModelDiagnostic 展示具体约束，现有账本、候选复核和一次纠正流程直接复用，不新增重试器。

ChunkAnalysisContract 按实体、结论、缺口逐项检查字段；文本、数组和证据上限与新版 Schema 使用相同常量。证据转换仍核对有效段号和原文坐标，不能以丢弃超额证据的方式把失败候选改为成功。领域层继续保留独立的防御性验证。

新建节点使用 v5 提示和有界 Schema，Evidence 优先 1–3 条代表性出处，任何一项最多 8 条；缺口可省略 Evidence，填写时也需 1–8 条。v2/v3/v4 的提示及 Schema 形态保持不变，旧节点指纹可重算，修订仅为未完成节点使用新版。

AnalysisProbe 增加 diagnose-candidate 开发入口，显式读取回放参数、只读备份运行/来源/账本三库，在副本复现指定请求。它不读凭据、不发网络请求、不采纳候选。共享的 CopyWorkspace 仅复用已有只读备份步骤。
