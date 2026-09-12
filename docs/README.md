# NovelGeneratePlugin 开发快速开始

面向作者的入口：[HTML 用户说明书](product/novel-workbench-user-manual.html) · [说明书 Markdown 原文](product/novel-workbench-user-manual.md)。五种使用场景含操作路径、填写示例、实际截图、完成标志与问题排查；文档维护方式见[说明书维护记录](product/user-manual-maintenance.md)。

本解决方案用于开发 `myavalonia.plugin.novel.generate` Managed Plugin。它把真实插件、独立 Avalonia 开发窗口和
自动化测试放在同一个解决方案中，使界面与业务代码既能快速预览，也能由 MyAvaloniaManagement Host
按正式插件协议加载。

产品视角先读 [网络小说创作工作台产品需求文档](product/novel-generation-product-requirements.md)：说明产品定位、
目标用户、创作流程、功能需求、首版范围、验收标准与版本路线。需求完成度以实施索引中的验证结果为准。

产品与架构设计见 [网络小说生成器插件设计方案](design/novel-generation-plugin-design.md)：记录 GitHub 调研、
能力边界、Host 接入、SQLite、DeepSeek、创作记忆与修订恢复；已结合创作反馈补充自动连续生成、
长期规则执行、课件转写作方法、文风校准和反馈改进。现明确以两个 Tool 管理可复用命名模板与模型连接/API Key 配置，
Document 使用本书模板快照和连接绑定；另有单本/多本参考小说分析成模板、再初始化新书的可选扩展。
技术设计是目标方案，实际实现见阶段档案。

[产品文档 HTML 图文版](product/novel-generation-product-requirements.html)保留完整产品需求，补充五组产品示意，
支持目录导航、概念界面切换和打印。可直接用浏览器离线打开；更新 Markdown 后，在项目根目录运行
`python docs/product/render-product-html.py` 重新生成。

实施入口为 [G0001–G0028 实施总计划](implementation-plan.md)，按步骤写明目标、依赖、工作项、修改位置、
验收和阶段出口；首版、长篇增强、选做扩展与正式交付分别编排。G0001–G0015 已完成本地实现与验证；当前可离线编辑、保存作品和管理基础修订、共享模板、本书规范与独立模型连接、本地规则检查及备份导出，新增模型连接的单句生成检测与用量账本，以及故事实体、别名、有效前文检索和上下文预览；新增分层规划与自动/协作采用，Codex GPT-6 三章规划实测通过；已接入单章生成审校与工作稿提交，连续运行、共享预算、暂停取消与检查点恢复已接入，材料提炼、对比试写和本书/模板采用已接入，完整 P1 验收的待补证据见 G0016。
G0016 工程验收与真实三章样稿已完成；P1 产品验收仍待作者质量签收与完整桌面交互。
入口：[使用说明](usage-guide.md) · [验收结果](refactoring/G0016/result.md) · [真实样稿](refactoring/G0016/samples/rain-letter-working-draft.md)。

阶段档案约定见 [实施索引](refactoring/README.md)，共同验证要求见 [质量基线](refactoring/quality-baseline.md)。

当前新增产品方向见 [整本小说分析可行性与实施计划](novel-analysis-feasibility-plan.md)：使用 G0029–G0036，覆盖 TXT 全文导入、多维提取、跨章整合、预算与恢复、综合报告及最终质量验收；附逐阶段进度和目标达成条件。G0029–G0034 已验收，当前 6/8 阶段、34/40 工作项，M2 报告候选可用；G0035 原生界面已接通，完整桌面和 G0036 质量验收待补。原 G0021 的模板采用和新书初始化保留为后续范围。

## 项目结构

```text
NovelGeneratePlugin/
├─ NovelGeneratePlugin.slnx
├─ src/
│  ├─ NovelGeneratePlugin.Plugin/       # 唯一真实插件程序集和正式交付内容
│  └─ NovelGeneratePlugin.Standalone/   # 只供本地开发的 Avalonia 窗口
├─ tests/
│  └─ NovelGeneratePlugin.Tests/        # 插件业务、状态和注册行为测试
└─ docs/                       # 当前项目随模板生成的开发说明
```

`NovelGeneratePlugin.Plugin` 是唯一正式插件项目。Standalone 和 Tests 都直接引用它，不能各自复制一套 View、
ViewModel、服务或贡献清单。

## 最短开发流程

在解决方案根目录打开 PowerShell：

```powershell
dotnet restore
dotnet build -c Debug -warnaserror
dotnet test -c Debug --no-build
dotnet run --project src/NovelGeneratePlugin.Standalone
```

Standalone 适合快速检查 AXAML、编译绑定、命令和插件自身对象图。写到可以联调时，再把干净的插件目录
部署到真实 Host；发布前则必须生成正式 ZIP。不要把 Standalone 能运行当成 Host 验收已经通过。

## 接下来阅读

1. [项目、Host 与 Standalone 窗口职责](project-and-window-responsibilities.md)
2. [临时部署、正式发布与验收](deployment-and-release.md)
3. [Workflow Action Provider 与 Consumer 接入](workflow-actions.md)
4. [Workbench Command 开发说明](workbench-commands.md)

## 开发前记住

- `myavalonia.plugin.novel.generate` 是持久身份，发布后不要因为显示名、项目名或文件夹改名而改变它。
- manifest 由 Build 包生成，不要手写或复制一份长期维护。
- 插件只通过公开 Plugin SDK 接入 Host，不引用 Host 内部项目。
- 新增插件运行时 NuGet 包时，要同时更新根目录 `Directory.Packages.props`、Plugin 项目的
  `PackageReference` 和 `ManagedPluginPrivatePackage`；完整示例见部署文档。
- 当前交付目标是 Windows x64；插件替换后必须完整重启 Host，不支持热更新。
- Workflow 可登记双角色，但禁止自调用和 Handler 内嵌套调用；先阅读专项文档。
- Workbench Command 只提升跨工作台有价值的用户意图；当前不占用全局快捷键。


- [公共资源与专属图标](plugin-icons.md)：注册、画布、独立预览与私有包交付。

实际模型协议、Codex 套餐限制及 DeepSeek 配置依据见 [G0008 能力记录](refactoring/G0008/model-protocol-and-capabilities.md)。
