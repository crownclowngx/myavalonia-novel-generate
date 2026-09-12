using MyAvaloniaManagement.Icons;
using MyAvaloniaManagement.PluginSdk.UI;
using NovelGeneratePlugin.Constants;
using NovelGeneratePlugin.Features.Main;
namespace NovelGeneratePlugin.Plugin;

public sealed class NovelGeneratePluginModule : IPluginModule
{
    public void Configure(IPluginRegistration registration)
    {
        ArgumentNullException.ThrowIfNull(registration);
        registration.Services.AddNovelGeneratePluginServices();
        var asset = CommonIcons.TextCheck;
        var icon = registration.AddIcon("main-document", new VectorIconDefinition(asset.PathData, asset.ViewBoxWidth, asset.ViewBoxHeight));
        // 贡献根只通过 SDK 登记，不在 Services 重复登记；项目正文由插件保存。
        registration.AddDocument<MainDocument, MainView>(new DocumentDescriptor(
            PluginIds.MainDocument, "小说创作", "管理本书创意、章节与创作过程", "小说创作", icon));
    }
}
