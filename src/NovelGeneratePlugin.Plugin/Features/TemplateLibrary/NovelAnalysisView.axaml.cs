using Avalonia.Controls;

namespace NovelGeneratePlugin.Features.TemplateLibrary;

/// <summary>View 只建立原生绑定；加载、任务和退出由模板库模型及根服务协调，移除控件不会取消任务。</summary>
public sealed partial class NovelAnalysisView : UserControl { public NovelAnalysisView() => InitializeComponent(); }
