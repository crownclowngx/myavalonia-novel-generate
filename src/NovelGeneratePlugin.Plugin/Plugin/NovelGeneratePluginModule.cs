using MyAvaloniaManagement.Icons;
using MyAvaloniaManagement.PluginSdk.UI;
using NovelGeneratePlugin.Constants;
using NovelGeneratePlugin.Features.Main;
using NovelGeneratePlugin.Features.TemplateLibrary;
using NovelGeneratePlugin.Features.ModelConnections;
using NovelGeneratePlugin.Application.Projects;
namespace NovelGeneratePlugin.Plugin;

public sealed class NovelGeneratePluginModule : IPluginModule
{
    public void Configure(IPluginRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        registration.Services.AddNovelGeneratePluginServices();
        registration.UseLifecycle<PluginCloseCoordinator>();
        var asset = CommonIcons.TextCheck;
        var icon = registration.AddIcon("main-document", new VectorIconDefinition(asset.PathData, asset.ViewBoxWidth, asset.ViewBoxHeight));
        // 贡献根只通过 SDK 登记，不在 Services 重复登记；项目正文由插件保存。
        registration.AddDocument<MainDocument, MainView>(new DocumentDescriptor(
            PluginIds.MainDocument, "小说创作", "管理本书创意、章节与创作过程", "小说创作", icon));
        foreach (var (command, label) in NovelCommands.All)
        {
            registration.AddDocumentCommand(new CommandDescriptor(command, label, "对当前小说工作区执行：" + label), PluginIds.MainDocument);
            registration.AddMenuCommandContribution(new MenuCommandContributionDescriptor(
                new(command.Value.Replace(".command.", ".menu.")), command, WorkbenchMenuLocations.ToolsShared,
                group: "novel", order: NovelCommands.All.ToList().FindIndex(c => c.Id == command) * 10,
                targetUnavailableBehavior: MenuCommandTargetUnavailableBehavior.Hide));
        }
        registration.AddTool<TemplateLibraryTool, TemplateLibraryView>(new ToolDescriptor(
            PluginIds.Templates, "创作模板库", "管理命名模板、草案与不可变版本", ToolDockSide.Right, ToolCloseBehavior.Hide, icon));
        registration.AddTool<ModelConnectionsTool, ModelConnectionsView>(new ToolDescriptor(
            PluginIds.Connections, "模型连接", "管理模型预设与独立凭据", ToolDockSide.Right, ToolCloseBehavior.Hide, icon));
    }
}
