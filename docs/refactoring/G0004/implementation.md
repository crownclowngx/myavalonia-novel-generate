# G0004 实施方案

基线为 G0003 `eef8dec`；需求关联 FR-02、A02、A16。

## 职责与设计思路

`Domain/Templates.cs` 表达命名模板、不可变版本、四种规范维度与采用来源，不访问数据库和 UI。
`Application/Templates/TemplateLibrary.cs` 组织创建、保存、归档、发布、采用和恢复用例，依赖持久化端口。
`Infrastructure/Persistence/TemplateStore.cs` 负责 SQLite 并发版本比较、历史保护及恢复文件。
仅以不可变记录、普通服务和端口隔离职责，不增加通用事件总线、通用仓储或模式框架。

共享 `TemplateLibraryTool` 负责表单和命令；根服务持久化模板。`MainDocument.Templates.cs` 负责本书操作，直接调用用例，
不依赖 Tool 实例。`NovelGeneratePluginModule` 只注册一个 Hide 行为的模板 Tool，两个文档作用域共享此 Tool。
Standalone 通过实际模块贡献创建预览页，关闭先等待 Tool 保存准备，再关闭 Document 和释放服务。

## 产品操作

模板库可按名称或标签查找、编辑草案、保存新版本、复制、归档及恢复归档。草案未保存时阻止切换，保留明确放弃入口。
本书可选世界观、文风、方法、规则四个维度；既有作品采用前展示当前与目标内容，修改本书或选择项会使预览失效。
按模板新建先校验和组装新聚合，再创建作品文件；书卷章身份独立。本书另存模板一次事务创建包含 v1 的新资产。
正文、事实账本和运行状态不会被拷贝到模板。

## 保存与验证

详细契约见 [模板快照与恢复](template-snapshot-contract.md)。SQLite、两书隔离、故障和并发测试覆盖领域/应用/存储边界；
原生 Headless 控件测试输入中文并重建视图，补充真实绑定验证。真实 Host Dock 生命周期不以 Headless 代替。
模型审查仅用现有 Codex 套餐 GPT-6，指定文本通过 stdin 送审；未执行小说生成或 DeepSeek 请求。

## 范围说明

本阶段提供文字规范和整维度对比；逐行合并、材料提炼、模板试写与检查失效规则分别由后续阶段扩展。
旧版命名模板支持来源文本，本阶段不解析其中任意占位符或执行脚本。
