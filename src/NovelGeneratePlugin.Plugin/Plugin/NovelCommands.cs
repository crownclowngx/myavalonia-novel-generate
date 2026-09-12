using MyAvaloniaManagement.PluginSdk;
namespace NovelGeneratePlugin.Plugin;

/// <summary>仅声明稳定身份和展示文字；不保存文档、服务、ICommand 或执行回调。</summary>
public static class NovelCommands
{
    public static readonly CommandId Open = new("myavalonia.plugin.novel.generate.command.open");
    public static readonly CommandId Start = new("myavalonia.plugin.novel.generate.command.start");
    public static readonly CommandId Pause = new("myavalonia.plugin.novel.generate.command.pause");
    public static readonly CommandId Cancel = new("myavalonia.plugin.novel.generate.command.cancel");
    public static readonly CommandId Generate = new("myavalonia.plugin.novel.generate.command.generate");
    public static readonly CommandId Finalize = new("myavalonia.plugin.novel.generate.command.finalize");
    public static readonly CommandId Export = new("myavalonia.plugin.novel.generate.command.export");
    public static IReadOnlyList<(CommandId Id, string Label)> All { get; } = Array.AsReadOnly(new[]
    {
        (Open, "打开小说项目"), (Start, "开始连续创作"), (Pause, "本章完成后暂停"), (Cancel, "立即取消创作请求"),
        (Generate, "生成并审校本章"), (Finalize, "人工定稿本章"), (Export, "预览小说正文导出")
    });
}
