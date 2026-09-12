using MyAvaloniaManagement.PluginSdk;
namespace NovelGeneratePlugin.Constants;

public static class PluginIds
{
    public static readonly PluginId Plugin = new("myavalonia.plugin.novel.generate");
    public static readonly ToolTypeId Templates = new("myavalonia.plugin.novel.generate.tool.templates");
    public static readonly DocumentTypeId MainDocument = new("myavalonia.plugin.novel.generate.document.main");
}
