using Avalonia.Controls;
using Avalonia.Controls.Primitives;
namespace NovelGeneratePlugin.Features.Main;

public partial class MainView : UserControl
{
    public MainView()
    {
        InitializeComponent();
        SizeChanged += (_, _) => UpdateLayoutMode();
        InspectorToggle.IsCheckedChanged += (_, _) => UpdateLayoutMode();
    }
    /// <summary>
    /// 布局只由 View 拥有。窄窗切换正文与侧栏，不移动或重建子控件，因此选区、表单和进行中的任务仍绑定同一实例。
    /// 宽窗恢复并排显示；没有对 Host Dock 或窗口主题作全局修改。
    /// </summary>
    private void UpdateLayoutMode()
    {
        var narrow = Bounds.Width < 1040;
        InspectorToggle.IsVisible = narrow;
        AdaptiveWorkspace.ColumnDefinitions[1].Width = narrow ? new GridLength(0) : new GridLength(390);
        AdaptiveWorkspace.ColumnSpacing = narrow ? 0 : 12;
        Grid.SetColumn(InspectorPanel, narrow ? 0 : 1);
        ManuscriptPanel.IsVisible = !narrow || InspectorToggle.IsChecked != true;
        InspectorPanel.IsVisible = !narrow || InspectorToggle.IsChecked == true;
    }
}
