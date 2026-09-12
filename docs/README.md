# NovelGeneratePlugin 开发快速开始

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
验收和阶段出口；首版、长篇增强、选做扩展与正式交付分别编排。G0001–G0007 已完成本地实现与验证；当前可离线编辑、保存作品和管理基础修订、共享模板、本书规范与独立模型连接、本地规则检查及备份导出，后续阶段依赖验证结果推进。
阶段档案约定见 [实施索引](refactoring/README.md)，共同验证要求见 [质量基线](refactoring/quality-baseline.md)。

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
