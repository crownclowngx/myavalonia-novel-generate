using Avalonia.Controls;
using Avalonia.Interactivity;
namespace NovelGeneratePlugin.Features.ModelConnections;

public sealed partial class ModelConnectionsView : UserControl
{
    public ModelConnectionsView() { InitializeComponent(); Loaded += OnLoaded; }
    private async void OnLoaded(object? sender, RoutedEventArgs args)
    {
        if (DataContext is not ModelConnectionsTool model) return;
        try { await model.InitializeAsync(); }
        catch (Exception exception) { model.Status = "连接面板初始化失败：" + exception.Message; }
    }
}
