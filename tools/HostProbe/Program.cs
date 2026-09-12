// 本地 Host 组合探针：仅验证真实宿主类的对象图与控件适配，系统选择器和网络用例使用隔离替身。
// 使用 Host 既有测试友元程序集身份访问内部验收面；本项目不加入插件交付物或普通门禁。
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Platform.Storage;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Microsoft.Extensions.DependencyInjection;
using MyAvaloniaManagement;
using MyAvaloniaManagement.Business.Composition;
using MyAvaloniaManagement.Business.Lifecycle;
using MyAvaloniaManagement.Business.Diagnostics;
using MyAvaloniaManagement.Business.Docking;
using MyAvaloniaManagement.Business.Documents.Ownership;
using MyAvaloniaManagement.Business.Plugins.Discovery;
using MyAvaloniaManagement.Business.Plugins.Registration;
using MyAvaloniaManagement.Business.Workspace;
using MyAvaloniaManagement.PluginSdk;
using MyAvaloniaManagement.PluginSdk.UI;
using NovelGeneratePlugin.Application.Projects;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Application.Connections;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Constants;
using NovelGeneratePlugin.Features.Main;
using NovelGeneratePlugin.Features.TemplateLibrary;
using NovelGeneratePlugin.Features.ModelConnections;
using NovelGeneratePlugin.Infrastructure.Persistence;
using NovelGeneratePlugin.Plugin;
using System.Reflection;
[assembly: AvaloniaTestApplication(typeof(ProbeApp))]
var session = HeadlessUnitTestSession.GetOrStartForAssembly(Assembly.GetExecutingAssembly());
await session.Dispatch<bool>(async () =>
{
    var root = Path.GetFullPath(Environment.GetEnvironmentVariable("NOVEL_HOST_PROBE_OUTPUT") ?? Path.Combine(AppContext.BaseDirectory, "probe-data-" + Guid.NewGuid().ToString("N"))); Directory.CreateDirectory(root);
    using var diagnostics = HostDiagnosticSession.Start(root); var builder = new PluginRegistryBuilder(); using var owners = new PluginProviderOwner(); var scopes = new DocumentScopeRegistry();
    var picker = new ProbePicker(); var model = new BlockingModel(); var services = new ServiceCollection(); services.AddApplicationServices(builder, owners, scopes); services.AddViewModels(); services.AddSingleton(diagnostics); services.AddSingleton<IHostDiagnosticSink>(diagnostics); services.AddSingleton<IPluginWindowInteraction>(picker);
    var catalog = PluginModuleCatalog.CreateForTests([(PluginIds.Plugin, (IPluginModule)new IsolatedModule(new WorkspacePaths(Path.Combine(root, "novel")), model))]); services.AddSingleton(catalog);
    await using var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true }); owners.Compose(catalog, provider, builder, scopes, diagnostics);
    var registry = provider.GetRequiredService<PluginRegistry>(); Check(registry.DocumentDescriptors.Count == 1, "one Document"); Check(registry.ToolDescriptors.Count == 2, "two Tools");
    var hostLifecycle = provider.GetRequiredService<PluginLifecycleCoordinator>(); await hostLifecycle.InitializeAllAsync();
    var activator = provider.GetRequiredService<PluginContributionActivator>(); using var first = activator.ActivateDocument(PluginIds.MainDocument); using var second = activator.ActivateDocument(PluginIds.MainDocument);
    var a = (MainDocument)first.Model; var b = (MainDocument)second.Model; await a.InitializeAsync(new NewDocumentActivation("甲"), default); await b.InitializeAsync(new NewDocumentActivation("乙"), default);
    var tool1 = activator.ActivateTool(PluginIds.Templates); var tool2 = activator.ActivateTool(PluginIds.Templates); Check(ReferenceEquals(tool1.Model, tool2.Model), "template singleton");
    var connection1 = activator.ActivateTool(PluginIds.Connections); var connection2 = activator.ActivateTool(PluginIds.Connections); Check(ReferenceEquals(connection1.Model, connection2.Model), "connection singleton");
    await ((TemplateLibraryTool)tool1.Model).InitializeAsync(); await ((ModelConnectionsTool)connection1.Model).InitializeAsync();
    a.BookTitle = "Host甲书"; picker.Next = Path.Combine(root, "甲书.noveldb"); await a.NewProjectCommand.ExecuteAsync(null); a.ChapterText = "由真实 Host Provider 创建的甲书正文。"; await a.SaveCommand.ExecuteAsync(null);
    b.BookTitle = "Host乙书"; picker.Next = Path.Combine(root, "乙书.noveldb"); await b.NewProjectCommand.ExecuteAsync(null); b.ChapterText = "乙书独立正文"; await b.SaveCommand.ExecuteAsync(null); Check(a.ChapterText != b.ChapterText, "book isolation");
    using var dockA = new ManagedDocumentDockable(first, "甲"); using var dockB = new ManagedDocumentDockable(second, "乙"); var locator = new ViewLocator(provider.GetRequiredService<WorkspaceCatalog>()); locator.Prepare(dockA); locator.Prepare(dockB);
    var recycle = new DocumentControlRecycling(); var viewA = (Control)recycle.Build(dockA, null, null)!; var viewB = (Control)recycle.Build(dockB, null, null)!;
    Check(ReferenceEquals(viewA.DataContext, a), "Host view binding"); Check(!ReferenceEquals(viewA, viewB), "Host view isolation");
    var tabs = new TabControl { ItemsSource = new[] { new TabItem { Header = "甲书", Content = viewA }, new TabItem { Header = "乙书", Content = viewB } } }; var window = new Window { Width = 1280, Height = 900, Content = tabs };
    try
    {
        window.Show(); Dispatcher.UIThread.RunJobs(); window.UpdateLayout(); Check(viewA.Bounds.Width > 1000, "Host adapter view layout"); AvaloniaHeadlessPlatform.ForceRenderTimerTick(); Dispatcher.UIThread.RunJobs(); using var frame = window.CaptureRenderedFrame(); frame!.Save(Path.Combine(root, "host-composition.png"), Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        var target = (IWorkbenchDocumentCommandTarget)a; Check(target.CanExecute(NovelCommands.Export), "Host command target"); await target.ExecuteAsync(NovelCommands.Export, default);
        var connectionService = (ConnectionService)owners.GetRequiredService(PluginIds.Plugin, typeof(ConnectionService));
        var preset = new ModelPreset("gpt-6-astra", 8192, "low"); var connection = await connectionService.SaveAsync(null, new("Host 取消夹具", ModelProvider.CodexCli, "", Path.Combine(root, "codex.exe"), preset, preset, preset));
        await a.RefreshConnectionsCommand.ExecuteAsync(null); a.SelectedBookConnection = a.ConnectionChoices.Single(); await a.BindConnectionCommand.ExecuteAsync(null);
        a.PlanningMainline = "林舟进入邮局寻找旧信。"; a.VolumeGoal = "找到旧信"; a.PlanGoal = "进入邮局"; a.PlanConflict = "门锁生锈"; a.PlanViewpoint = "有限第三人称"; a.PlanTimePlace = "雨夜邮局"; a.PlanEvents = "打开门锁"; a.PlanStateChanges = "进入屋内"; a.PlanForeshadow = "旧信"; a.PlanBridge = "查看抽屉"; await a.SaveChapterPlanCommand.ExecuteAsync(null);
        Console.WriteLine("PRECHECK " + a.Status + " / " + a.PlanningStatus + " / model=" + owners.GetRequiredService(PluginIds.Plugin, typeof(ITextModel)).GetType().Name);
        var generating = target.ExecuteAsync(NovelCommands.Generate, default).AsTask(); if (await Task.WhenAny(model.Started.Task, generating) != model.Started.Task) { await generating; throw new InvalidOperationException("生成未启动模型：" + a.Status + " / " + a.GenerationStatus); }
        await model.Started.Task;
        tabs.ItemsSource = new[] { new TabItem { Header = "乙书", Content = viewB } }; dockA.Dispose(); await a.DisposeAsync(); await generating; Check(model.Stopped, "Host close drains in-flight model"); Check(b.CanEdit, "close one preserves other"); Check(ReferenceEquals(tool1.Model, activator.ActivateTool(PluginIds.Templates).Model), "close preserves shared tool");
        using var reopened = activator.ActivateDocument(PluginIds.MainDocument); var c = (MainDocument)reopened.Model; await c.InitializeAsync(new NewDocumentActivation("重开"), default); picker.Next = Path.Combine(root, "甲书.noveldb"); await ((IWorkbenchDocumentCommandTarget)c).ExecuteAsync(NovelCommands.Open, default); Check(c.ChapterText.Contains("真实 Host Provider"), "reopen persisted text"); await c.DisposeAsync();
    }
    finally { window.Close(); }
    await hostLifecycle.ShutdownAllAsync(); scopes.CloseAll();
    Console.WriteLine("PASS actual Host Provider/Registry/Activator/DocumentScope/Dock view adapter; two books, two singleton tools, save/reopen, command target, close/shutdown. System picker replaced; full desktop Dock not claimed."); return true;
}, default);
static void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); Console.WriteLine("PASS " + label); }
public sealed class ProbeApp : Avalonia.Application { public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<ProbeApp>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }); public override void Initialize() => Styles.Add(new FluentTheme()); }
sealed class IsolatedModule(WorkspacePaths paths, ITextModel model) : IPluginModule { public void Configure(IPluginRegistration registration) { registration.Services.AddSingleton(paths); new NovelGeneratePluginModule().Configure(registration); registration.Services.AddSingleton(model); } }
sealed class ProbePicker : IPluginWindowInteraction { public string? Next { get; set; } public Task<IReadOnlyList<string>> PickOpenFilesAsync(FilePickerOpenOptions options, CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<string>>(Next is null ? [] : [Next]); public Task<string?> PickSaveFileAsync(FilePickerSaveOptions options, CancellationToken cancellationToken = default) => Task.FromResult(Next); public Task<bool> TrySetClipboardTextAsync(string text, CancellationToken cancellationToken = default) => Task.FromResult(false); }

// 替身挂起直到取消，不发起网络；验证 Host 的同步 Dispose 能通过插件 Shutdown 正确排空异步任务。
sealed class BlockingModel : ITextModel { public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public bool Stopped { get; private set; } public async Task<TextModelResponse> GenerateAsync(TextModelRequest request, IProgress<string>? progress, CancellationToken cancellationToken) { Started.TrySetResult(); try { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); throw new InvalidOperationException(); } finally { Stopped = true; } } }
