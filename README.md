# NovelGeneratePlugin

产品规划见 [网络小说创作工作台产品需求文档](docs/product/novel-generation-product-requirements.md)，包含用户场景、
功能流程、首版范围与验收标准。当前已开始实施，阶段进度见下方实施档案。

也可直接打开 [HTML 图文版](docs/product/novel-generation-product-requirements.html)，查看完整正文、产品界面示意、
创作流程和版本路线；支持离线阅读与打印。

后续开发按 [G0001–G0028 实施总计划](docs/implementation-plan.md)推进；首个可用版本对应 G0001–G0016。
阶段状态和归档规则见 [G 编号实施档案](docs/refactoring/README.md)。G0001–G0015 已完成本地实现：可编辑和保存小说项目、恢复失败副本、提交工作稿、人工定稿及回退修订、管理模板、本书独立规范、模型连接、本地规则检查与备份/导出。已接入模型协议与单句连接检测；已有实体/别名、有效前文检索和上下文预览，现支持分层规划、近期章纲、方法事件和自动/协作采用；已支持单章生成、审校、有限修正和工作稿提交；已支持 3–5 章连续工作稿、共享预算、暂停取消和中断恢复；已接入短材料方法化、文风校准及本书/模板采用；已接入改稿、手写复核、差异、影响清单与连续范围定稿；工作区与宿主命令已整合，完整产品验收继续推进。

这是由 `myavalonia-plugin` 创建的 Managed Plugin 解决方案。真实交付物是
`src/NovelGeneratePlugin.Plugin`；`Standalone` 只负责快速预览同一份 View、ViewModel 与业务代码。

> 第一次开始开发前，请先阅读 [项目文档与快速开始](docs/README.md)。其中说明了三个子项目和
> Standalone 窗口的职责、接入真实 Host 的边界，以及临时部署和正式 ZIP 发布流程。

```powershell
dotnet restore
dotnet build
dotnet run --project src/NovelGeneratePlugin.Standalone
dotnet msbuild src/NovelGeneratePlugin.Plugin/NovelGeneratePlugin.Plugin.csproj -t:BuildManagedPluginPackage -p:Configuration=Release
```

要在真实 Host 中调试，请显式提供 Host 的 `Controls` 目录：

```powershell
dotnet msbuild src/NovelGeneratePlugin.Plugin/NovelGeneratePlugin.Plugin.csproj `
  -t:DeployManagedPlugin `
  -p:ManagedPluginDeployRoot=C:\Path\To\Host\Controls
```

Standalone 只能验证界面和插件自身对象图；manifest、加载上下文、Document Scope、Dock、Tool 和
生命周期必须使用真实 Host 做最终验收。

演示 Document Command 已移除；后续命令边界、Target 适配和测试清单见
[Workbench Command 开发说明](docs/workbench-commands.md)。

本地验证运行 `./tools/verify-local.ps1`。操作与保存边界见 [G0002 存储契约](docs/refactoring/G0002/project-storage-contract.md)。
运行 Standalone 后输入书名与创意，点击“新建项目”选择一个新的 `.noveldb` 文件；之后的正文输入会自动保存。
“保存本书”是插件自己的保存入口，不依赖 Host 的 Ctrl+S。

工作稿、正式稿与记忆隔离见 [G0003 修订契约](docs/refactoring/G0003/revision-and-commit-contract.md)。旧项目首次升级前自动生成一致性备份。

模板草案、版本、采用及失败恢复见 [G0004 模板快照契约](docs/refactoring/G0004/template-snapshot-contract.md)。
Standalone 的“创作模板库”页可创建和保存版本；作品展开“本书规范与模板”后可按模板新建，或预览差异后采用。

模型配置、Codex 登录与 API Key 边界见 [G0005 连接凭据契约](docs/refactoring/G0005/connection-and-credential-contract.md)。

长期规则编辑、草案保护与本地检测见 [G0006 规则契约](docs/refactoring/G0006/writing-rule-contract.md)。

P0 离线能力及未验项见 [G0007 验收矩阵](docs/refactoring/G0007/p0-acceptance-matrix.md)，文件语义见 [备份导出契约](docs/refactoring/G0007/backup-export-contract.md)。

模型接入能力与已知限制见 [G0008 协议记录](docs/refactoring/G0008/model-protocol-and-capabilities.md)，预算及候选恢复见 [请求用量契约](docs/refactoring/G0008/request-usage-contract.md)。

故事状态和请求上下文见 [G0009 记忆契约](docs/refactoring/G0009/memory-and-context-contract.md)，中文检索与迁移验证见 [夹具说明](docs/refactoring/G0009/retrieval-fixtures.md)。
