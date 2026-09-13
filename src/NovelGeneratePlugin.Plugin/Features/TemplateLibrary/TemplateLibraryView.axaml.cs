using Avalonia.Controls;
using Avalonia.Interactivity;
namespace NovelGeneratePlugin.Features.TemplateLibrary;

public sealed partial class TemplateLibraryView : UserControl
{
    public TemplateLibraryView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        DataContextChanged += OnDataContextChanged;
    }
    // Host 恢复 Dock 时可能先把 View 挂入视觉树，再绑定 Tool。
    // Loaded 和后置 DataContext 都桥接到模型的幂等初始化，防止重开后只显示空表单。
    private async void OnLoaded(object? sender, RoutedEventArgs args) => await InitializeModelAsync();
    private async void OnDataContextChanged(object? sender, EventArgs args)
    {
        if (IsLoaded) await InitializeModelAsync();
    }
    private async Task InitializeModelAsync()
    {
        if (DataContext is TemplateLibraryTool model)
        {
            try { await model.InitializeAsync(); }
            catch (Exception exception) { model.Status = "模板面板初始化失败：" + exception.Message; }
        }
    }
}
