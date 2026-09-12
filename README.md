# NovelGeneratePlugin

产品规划见 [网络小说创作工作台产品需求文档](docs/product/novel-generation-product-requirements.md)，包含用户场景、
功能流程、首版范围与验收标准。当前已开始实施，阶段进度见下方实施档案。

也可直接打开 [HTML 图文版](docs/product/novel-generation-product-requirements.html)，查看完整正文、产品界面示意、
创作流程和版本路线；支持离线阅读与打印。

后续开发按 [G0001–G0028 实施总计划](docs/implementation-plan.md)推进；首个可用版本对应 G0001–G0016。
阶段状态和归档规则见 [G 编号实施档案](docs/refactoring/README.md)。G0001 已完成演示清理、产品入口和开发窗口 Scope 改造。

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
