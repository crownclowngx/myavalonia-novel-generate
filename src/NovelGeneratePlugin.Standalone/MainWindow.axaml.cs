using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using MyAvaloniaManagement.PluginSdk;
using MyAvaloniaManagement.PluginSdk.UI;
using NovelGeneratePlugin.Plugin;
using NovelGeneratePlugin.Features.Main;
using NovelGeneratePlugin.Features;
using NovelGeneratePlugin.Application.Projects;
namespace NovelGeneratePlugin.Standalone;

public sealed partial class MainWindow : Window
{
    private ServiceProvider? _services;
    private AsyncServiceScope _scope;
    private IPluginDocument? _document;
    private readonly CancellationTokenSource _closing = new();
    private Task _initialization = Task.CompletedTask;
    private bool _mayClose;
    private bool _isClosing;
    private bool _finalizing;
    private readonly PluginCloseCoordinator _shutdown;
    private readonly List<(TabItem Tab, IClosePreparation Model)> _toolClosers = [];
    private readonly TabControl _tabs = new();
    private TabItem? _documentTab;
    public MainWindow()
    {
        InitializeComponent();
        var registration = new PreviewRegistration();
        registration.Services.AddSingleton<IPluginWindowInteraction>(new PreviewWindowInteraction(this));
        new NovelGeneratePluginModule().Configure(registration);
        _services = registration.Services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        _shutdown = _services.GetRequiredService<PluginCloseCoordinator>();
        _scope = _services.CreateAsyncScope();
        var entry = registration.Documents.Single();
        _document = (IPluginDocument)_scope.ServiceProvider.GetRequiredService(entry.Model);
        var view = (Control)_scope.ServiceProvider.GetRequiredService(entry.View);
        view.DataContext = _document;
        _documentTab = new TabItem { Header = entry.Descriptor.DisplayName, Content = view };
        var tabs = new List<TabItem> { _documentTab };
        foreach (var tool in registration.Tools)
        {
            var model = _services.GetRequiredService(tool.Model);
            var toolView = (Control)_services.GetRequiredService(tool.View); toolView.DataContext = model;
            var tab = new TabItem { Header = tool.Descriptor.DisplayName, Content = toolView }; tabs.Add(tab);
            if (model is IClosePreparation closer) _toolClosers.Add((tab, closer));
        }
        _tabs.ItemsSource = tabs; _tabs.SelectedIndex = 0;
        PreviewHost.Content = _tabs;
        Opened += (_, _) => _initialization = InitializeDocumentAsync();
        Closing += OnClosing;
    }
    private async Task InitializeDocumentAsync()
    {
        try { await _document!.InitializeAsync(new NewDocumentActivation("小说创作"), _closing.Token); }
        catch (OperationCanceledException) when (_closing.IsCancellationRequested) { }
        catch (Exception exception) { Title = "初始化失败：" + exception.Message; }
    }
    // 事件桥的异步异常必须全部观察；先排空 Scope，再发出最终关闭。
    private async void OnClosing(object? sender, WindowClosingEventArgs args)
    {
        if (_mayClose) return;
        args.Cancel = true;
        if (_isClosing) return;
        _isClosing = true;
        _closing.Cancel();
        PreviewHost.IsEnabled = false;
        try
        {
            await _initialization;
            foreach (var tool in _toolClosers)
                if (!await tool.Model.SaveBeforeCloseAsync()) { _tabs.SelectedItem = tool.Tab; Title = "未关闭：工具面板尚有未处理的修改"; return; }
            if (_document is IClosePreparation novel && !await novel.SaveBeforeCloseAsync()) { _tabs.SelectedItem = _documentTab; return; }
            // 预检查失败仍可编辑；进入 Scope 释放后对象可能已部分释放，不能重新启用一个失效编辑窗口。
            _finalizing = true;
            await _scope.DisposeAsync();
            await _shutdown.ShutdownAsync(CancellationToken.None);
            if (_services is not null) await _services.DisposeAsync();
            _services = null;
            PreviewHost.Content = null;
            _document = null;
            _mayClose = true;
            _closing.Dispose();
            Close();
        }
        catch (Exception exception) { Title = (_finalizing ? "关闭收尾失败（可再次关闭；稿件已保存或留有恢复副本）：" : "未关闭：") + exception.Message; }
        finally { _isClosing = false; PreviewHost.IsEnabled = !_finalizing; }
    }
}
