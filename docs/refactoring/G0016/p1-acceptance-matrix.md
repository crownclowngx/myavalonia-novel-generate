# P1 / A01–A16 验收矩阵

日期：2026-09-12。结果分为本地工程、原生 Headless、真实模型、Host 组合与完整桌面/作者签收。
完整本地入口：`./tools/verify-local.ps1`。单场景可用 `dotnet test tests/NovelGeneratePlugin.Tests -c Debug --filter FullyQualifiedName~类名`。
每个测试的中文名称就是触发、预期和断言范围；夹具使用独立临时目录，不读取作者作品。

| 场景 | 操作与预期 | 可重跑证据 | 实际状态 |
| --- | --- | --- | --- |
| A01 | 无密钥离线创建、编辑、保存、选版导出 | DocumentEditingTests / ProjectStorageTests / ArtifactTests | 本地通过；系统选择器待桌面验收 |
| A02 | 双书采用同模板，版本/归档不覆盖本书 | TemplateTests / CompositionTests；Host 双 Scope | 通过工程验证 |
| A03 | 预算内自动形成连续工作稿 | PlanningTests / ContinuousRunTests / ProductAcceptanceTests；真实 GPT-6 三章 | 三章生成通过，作者质量签收待补 |
| A04 | 多连接归属、凭据隔离、同连接排队 | ConnectionTests / ModelProtocolTests / ContinuousRunTests | 本地通过 |
| A05 | 生成与改写的本地硬规则优先 | WritingRuleTests / ChapterGenerationTests / ProductAcceptanceTests | 通过；违规候选不提交 |
| A06 | 重开保留规则，连接改变需重绑定 | ConnectionTests / WritingRuleTests / StoryMemoryTests | 本地通过 |
| A07 | 事实或检查不完整不推进下一章 | ChapterGenerationTests / ContinuousRunTests | 通过；保留候选与报告 |
| A08 | 短材料提炼证据与章纲方法事件 | MaterialCalibrationTests / PlanningTests；G0013 实际方法卡 | 短文本范围通过；完整课件扩展未纳入 |
| A09 | 同题 A/B 样稿、批注与版本采用 | MaterialCalibrationTests / NativeViewTests；G0013 实际 A/B | 流程通过，作者风格偏好评测待补 |
| A10 | 正文/规范/父修订变化拒绝旧候选 | EditingTests / ChapterGenerationTests / SaveRaceTests / WorkbenchTests | 通过；忙碌切章回归已补 |
| A11 | 预算和修正上限阻止新增请求 | ModelProtocolTests / ContinuousRunTests / ChapterGenerationTests | 通过；未知费用不清零 |
| A12 | SSE 断流/取消/中断，恢复不自动收费或重复提交 | ModelProtocolTests / ProductAcceptanceTests / ContinuousRunTests | 本地与 Host 在途关闭通过 |
| A13 | 保存失败保留恢复，不能以成功推进 | ProjectStorageTests / SaveRaceTests / RevisionTests / ChapterGenerationTests | 实际 SQLite 故障通过 |
| A14 | 连续范围定稿、回退和放弃与记忆一致 | RevisionTests / StoryMemoryTests / EditingTests / WorkbenchTests | 通过；失败范围无半提交，摘要保留 |
| A15 | 明确选稿、重新检查、备份与升级无秘密 | ArtifactTests / EditingTests / ConnectionTests / ProductAcceptanceTests | 本地通过；schema 9 迁移先备份 |
| A16 | 关闭本书与共享工具任务隔离、最终退出排空 | PluginCloseTests / MaterialCalibrationTests / WorkbenchTests；HostProbe | 组合通过；桌面 Tool 隐藏/再开待验 |

真实模型配置、范围、用量、产物和哈希见[样稿评测](sample-evaluation.md)。
原生控件路径见 [G0015 UI 矩阵](../G0015/ui-acceptance-matrix.md)，Host 证据见[组合记录](host-integration.md)。

首版工程功能已具备；作者文学质量签收及完整桌面交互缺失，**不得据此标记 P1 产品验收完成**。
