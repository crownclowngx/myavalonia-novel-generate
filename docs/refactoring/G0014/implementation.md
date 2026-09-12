# G0014 实施方案

领域 `EditingRules` 负责差异、选区边界、审校有效期、影响传播与连续定稿；不依赖 UI、数据库或模型。
`ChapterGenerationService` 在原流程中增加改写/只审校入口，共用上下文、预算、四维检查、摘要、事实证据与候选恢复。
`MainDocument.Editing` 负责选区快照、作者动作和展示；保存仍通过 `ProjectSession` 原子提交。

新增全报告 `ReviewPolicyStamp`，避免“没有提取事实”的章节失去审校时规范证明。候选上下文加入本章修订指针，
即使两次修订正文相同，旧候选也不能覆盖新父修订。schema 9 迁移先备份，旧 Passed 空指纹保留但需复核，不伪造证明。

批量定稿先在不可变快照中依次执行原 `FinalizeChapter`，任何缺章、过期或正文变化均整批拒绝；成功后一次 SQLite 事务写入。
正文导出按所选版本计算审校提示，不把编辑缓冲修改冒充未改变的正式稿失效。

见 [改稿与影响契约](editing-finalization-impact.md)、[阶段结果](result.md)。
