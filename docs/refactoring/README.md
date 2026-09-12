# G 编号实施档案与文档治理

日期：2026-09-12。G0001–G0004 已完成本地实现与验证；每阶段验证后独立 Git 提交。

总入口为[网络小说创作工作台实施总计划](../implementation-plan.md)，共同工程要求见[质量基线](quality-baseline.md)。沿用其他插件的 G 四位编号及计划/方案/结果三件套；本项目是从模板实现产品，目录名 `refactoring` 仅用于保持现有插件文档习惯。

## 文档分工

| 文档 | 职责 |
| --- | --- |
| [产品需求](../product/novel-generation-product-requirements.md) | 用户目标、FR 功能、A 验收、P 路线 |
| [HTML 图文版](../product/novel-generation-product-requirements.html) | 完整产品正文与概念界面示意 |
| [技术设计](../design/novel-generation-plugin-design.md) | 架构、存储、规则、记忆、任务和接入边界 |
| [实施总计划](../implementation-plan.md) | G0001–G0028 工作拆分、顺序、依赖、范围与出口 |
| `Gxxxx/plan.md` | 开工时真实基线、该阶段选定范围、工作项和验收计划 |
| `Gxxxx/implementation.md` | 实际方案、修改位置、职责、数据流、理由与兼容处理 |
| `Gxxxx/result.md` | 已发生的实现和验证、偏差、限制及未完成事项 |
| 阶段专项文档 | 存储/事务/协议/交互/评测等需要独立维护的契约和证据 |

阶段三件套随实施建立；未来阶段链接指向总计划，不创建空的结果文件。

## 阶段索引

| 编号 | 阶段 | 计划 | 当前状态 |
| --- | --- | --- | --- |
| G0001 | 基线、身份与最小产品壳 | [计划](G0001/plan.md) / [方案](G0001/implementation.md) / [结果](G0001/result.md) | 已实施，本地验证通过 |
| G0002 | SQLite 项目与可靠保存会话 | [计划](G0002/plan.md) / [方案](G0002/implementation.md) / [结果](G0002/result.md) | 本地验证完成，Host 待联调 |
| G0003 | 正文修订与双稿状态 | [计划](G0003/plan.md) / [方案](G0003/implementation.md) / [结果](G0003/result.md) | 本地验证完成 |
| G0004 | 命名模板库与本书快照 | [计划](G0004/plan.md) / [方案](G0004/implementation.md) / [结果](G0004/result.md) | 本地验证完成，Host 待联调 |
| G0005 | 模型连接与凭据 | [计划](../implementation-plan.md#g0005) | 待实施 |
| G0006 | 长期规则与本地检测 | [计划](../implementation-plan.md#g0006) | 待实施 |
| G0007 | 备份导出与 P0 验收 | [计划](../implementation-plan.md#g0007) | 待实施 |
| G0008 | 模型协议与用量基础 | [计划](../implementation-plan.md#g0008) | 待实施 |
| G0009 | 记忆、检索与上下文 | [计划](../implementation-plan.md#g0009) | 待实施 |
| G0010 | 分层规划与章纲 | [计划](../implementation-plan.md#g0010) | 待实施 |
| G0011 | 单章生成检查提交 | [计划](../implementation-plan.md#g0011) | 待实施 |
| G0012 | 连续运行、预算与恢复 | [计划](../implementation-plan.md#g0012) | 待实施 |
| G0013 | 材料方法化与文风校准 | [计划](../implementation-plan.md#g0013) | 待实施 |
| G0014 | 改稿、影响与批量定稿 | [计划](../implementation-plan.md#g0014) | 待实施 |
| G0015 | 交互与宿主命令 | [计划](../implementation-plan.md#g0015) | 待实施 |
| G0016 | P1 完整产品验收 | [计划](../implementation-plan.md#g0016) | 待实施 |
| G0017 | 历史状态、知情与伏笔 | [计划](../implementation-plan.md#g0017) | 待实施 |
| G0018 | 反馈候选与规范回退 | [计划](../implementation-plan.md#g0018) | 待实施 |
| G0019 | 长篇性能与迁移恢复 | [计划](../implementation-plan.md#g0019) | 待实施 |
| G0020 | P2 长篇质量验收 | [计划](../implementation-plan.md#g0020) | 待实施 |
| G0021 | 单本参考提炼模板 | [计划](../implementation-plan.md#g0021) | 待选做 |
| G0022 | 多本参考组合 | [计划](../implementation-plan.md#g0022) | 待选做 |
| G0023 | Workflow Provider | [计划](../implementation-plan.md#g0023) | 待选做 |
| G0024 | 更多材料格式 | [计划](../implementation-plan.md#g0024) | 待选做 |
| G0025 | EPUB/DOCX 导出 | [计划](../implementation-plan.md#g0025) | 待选做 |
| G0026 | 语义检索收益验证 | [计划](../implementation-plan.md#g0026) | 待选做 |
| G0027 | 故事可视化 | [计划](../implementation-plan.md#g0027) | 待选做 |
| G0028 | 选定版本交付 | [计划](../implementation-plan.md#g0028) | 待交付任务启动 |

## 开工与归档规则

1. 读取总计划、需求映射、质量基线和前置阶段实际结果，核实当前源码；无结果不能假定前置已完成。
2. 建立阶段 `plan.md`，保留总计划工作项编号，补充真实依赖、选定格式或模型、实现判断和非目标。
3. 随实现更新 `implementation.md`，把重要事务、模型协议和交互细节放入必要专项，避免重复多份可变规则。
4. `result.md` 只记录已经发生的事实。未执行的真实模型、Host、原生交互或发行检查单列，不填“通过”。
5. 更新本索引和总计划状态，增加实际存在的阶段文档链接；阶段拆分或范围变化说明原因、依赖和后续归属。
6. 已归档的编号和结果保留；新增工作不复用旧编号，也不把当前结果覆盖到旧源码验证记录上。
7. 原始日志、临时作品和截图建议保存在明确的测试产物目录，文档保存可复跑命令及脱敏摘要；个人作品和密钥不作为公共夹具。

## 结果模板

```markdown
# Gxxxx 实施结果

日期：待填写实际日期
状态：实施中 / 本地已验证、待实机验收 / 阶段已验收
基线：实际提交及工作树，或无提交时的文件摘要

## 已完成工作
逐项列 Gxxxx-xx、修改位置及对应 FR/A 编号。

## 实际验证
记录实际命令、环境、源版本、样本、结果与证据路径。
真实模型记录连接配置版本、样本范围和已知/未知用量，不含 Key。

## 偏差与未完成项
说明失败、未执行、环境缺失或不适用，及其对阶段出口的影响。

## 下一步
明确本阶段剩余工作或可进入的下一阶段。
```

当前结果见 G0001–G0004；下一执行入口为 G0005。
