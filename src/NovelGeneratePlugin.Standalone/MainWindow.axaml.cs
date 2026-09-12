using Avalonia.Controls;
using Microsoft.Extensions.DependencyInjection;
using MyAvaloniaManagement.PluginSdk;
using NovelGeneratePlugin.Plugin;
namespace NovelGeneratePlugin.Standalone;

public sealed partial class MainWindow : Window
{
    private ServiceProvider? _services;
    private AsyncServiceScope _scope;
    private IPluginDocument? _document;
    private readonly CancellationTokenSource _closing = new();
    private Task _initialization = Task.CompletedTask;
    private bool _mayClose;
    public MainWindow()
    {
        InitializeComponent();
        var registration = new PreviewRegistration();
        new NovelGeneratePluginModule().Configure(registration);
        _services = registration.Services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        _scope = _services.CreateAsyncScope();
        var entry = registration.Documents.Single();
        _document = (IPluginDocument)_scope.ServiceProvider.GetRequiredService(entry.Model);
        var view = (Control)_scope.ServiceProvider.GetRequiredService(entry.View);
        view.DataContext = _document;
        PreviewHost.Content = view;
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
        if (_closing.IsCancellationRequested) return;
        _closing.Cancel();
        try
        {
            await _initialization;
            PreviewHost.Content = null;
            await _scope.DisposeAsync();
            if (_services is not null) await _services.DisposeAsync();
            _services = null;
        }
        catch (Exception exception) { System.Diagnostics.Debug.WriteLine(exception.Message); }
        finally { _document = null; _mayClose = true; _closing.Dispose(); Close(); }
    }
}
