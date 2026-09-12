using Avalonia.Controls;
using Avalonia.Interactivity;
namespace NovelGeneratePlugin.Features.TemplateLibrary;

public sealed partial class TemplateLibraryView : UserControl
{
    public TemplateLibraryView() { InitializeComponent(); Loaded += OnLoaded; }
    // SDK 的 Tool 无初始化契约。控件只桥接一次可等待的加载，业务异常由模型统一报告。
    private async void OnLoaded(object? sender, RoutedEventArgs args)
    {
        if (DataContext is TemplateLibraryTool model)
        {
            try { await model.InitializeAsync(); }
            catch (Exception exception) { model.Status = "模板面板初始化失败：" + exception.Message; }
        }
    }
}
