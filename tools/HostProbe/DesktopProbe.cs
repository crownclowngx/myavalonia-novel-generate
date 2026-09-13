using System.Text.Json;
using Avalonia;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using MyAvaloniaManagement.Business.Commands.Catalog;
using MyAvaloniaManagement.Business.Composition;
using MyAvaloniaManagement.Business.Diagnostics;
using MyAvaloniaManagement.Business.Documents.Ownership;
using MyAvaloniaManagement.Business.Lifecycle;
using MyAvaloniaManagement.Business.Plugins.Discovery;
using MyAvaloniaManagement.Business.Plugins.Registration;
using MyAvaloniaManagement.Business.Workspace;
using MyAvaloniaManagement.Business.WorkflowActions;
using MyAvaloniaManagement.PluginSdk.UI;
using NovelGeneratePlugin.Application.Analysis;
using NovelGeneratePlugin.Application.Connections;
using NovelGeneratePlugin.Constants;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Infrastructure.Persistence;

/// <summary>
/// G0044 原生桌面夹具。复用生产 App、Shell、菜单、Dock、选择器和关闭链；
/// 只替换插件发现目录、数据根及 ITextModel。所有界面动作由验收者实际操作。
/// 本程序不会部署插件，也不接触用户已有 Host 的布局、连接或作品。
/// </summary>
internal static class DesktopProbe
{
    private sealed record Configuration(string OutputDirectory, string AnalysisSampleRoot);
    internal static int? TryRun()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "desktop-probe.json");
        if (!File.Exists(path)) return null;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("原生验收仅在交互式 Windows 桌面运行。");
        var configuration = JsonSerializer.Deserialize<Configuration>(File.ReadAllText(path)) ?? throw new InvalidDataException("缺少隔离配置。");
        var result = 1;
        var thread = new Thread(() =>
        {
            try { result = Run(configuration); }
            catch (Exception error)
            {
                Directory.CreateDirectory(configuration.OutputDirectory);
                File.WriteAllText(Path.Combine(configuration.OutputDirectory, "desktop-error.txt"), error.ToString());
                File.WriteAllText(Path.Combine(configuration.OutputDirectory, "desktop-exit.json"),
                    JsonSerializer.Serialize(new { ExitCode = 1, RuntimeShutdownSucceeded = false, ErrorType = error.GetType().Name, FinishedAt = DateTimeOffset.UtcNow }));
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start(); thread.Join();
        return result;
    }

    private static int Run(Configuration configuration)
    {
        var root = Path.GetFullPath(configuration.OutputDirectory);
        var sample = Path.GetFullPath(configuration.AnalysisSampleRoot);
        if (root == sample || root.StartsWith(sample + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("验收输出必须与来源档案分离。");
        Directory.CreateDirectory(root);
        Environment.SetEnvironmentVariable("MYAVALONIA_DATA_DIRECTORY", Path.Combine(root, "host"));
        var paths = new WorkspacePaths(Path.Combine(root, "novel"));
        Directory.CreateDirectory(paths.Root);
        foreach (var name in new[] { "reference-analysis.db", "reference-runs.db", "model-requests.db" })
        {
            var target = Path.Combine(paths.Root, name);
            // 重开只读取上次测试成果，不重置转换历史或覆盖已编辑草案。
            if (File.Exists(target)) continue;
            using var input = ProjectStore.Connect(Path.Combine(sample, name), SqliteOpenMode.ReadOnly);
            using var output = ProjectStore.Connect(target, SqliteOpenMode.ReadWriteCreate);
            input.BackupDatabase(output);
        }
        using var diagnostics = HostDiagnosticSession.Start(Path.Combine(root, "host"));
        var builder = new PluginRegistryBuilder();
        var owners = new PluginProviderOwner();
        var scopes = new DocumentScopeRegistry();
        var participants = new HostShutdownParticipants();
        var services = new ServiceCollection();
        services.AddApplicationServices(builder, owners, scopes, participants);
        services.AddViewModels();
        services.AddSingleton(diagnostics);
        services.AddSingleton<IHostDiagnosticSink>(diagnostics);
        var model = new ConversionFixtureModel();
        var catalog = PluginModuleCatalog.CreateForTests([(PluginIds.Plugin, (IPluginModule)new IsolatedModule(paths, model))]);
        services.AddSingleton(catalog);
        var provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
        var shutdown = new HostRuntimeShutdown(owners, provider, scopes.CloseAll, participants, HostResourceRetention.ProcessLifetime, diagnostics);
        provider.GetRequiredService<HostDesktopClosePreparation>().Bind(shutdown.PrepareAsync);
        var runtime = HostRuntime.Initialize(new HostRuntime(provider, shutdown), () =>
        {
            owners.Compose(catalog, provider, builder, scopes, diagnostics);
            var registry = provider.GetRequiredService<PluginRegistry>();
            provider.GetRequiredService<WorkbenchCommandCatalog>();
            provider.GetRequiredService<WorkflowActionCatalogStore>().Commit(registry, provider.GetRequiredService<PluginAvailabilityReadModel>());
            provider.GetRequiredService<PluginLifecycleCoordinator>().InitializeAllAsync().GetAwaiter().GetResult();
            provider.GetRequiredService<WorkspaceSession>();
            var connections = (ConnectionService)owners.GetRequiredService(PluginIds.Plugin, typeof(ConnectionService));
            if (connections.ListAsync().GetAwaiter().GetResult().Connections.Count == 0)
            {
                var preset = new ModelPreset("本地转换夹具", 8192, "low");
                connections.SaveAsync(null, new("本地隔离验收（不联网）", ModelProvider.CodexCli, "",
                    Path.Combine(root, "unused-codex.exe"), preset, preset, preset)).GetAwaiter().GetResult();
            }
            var sources = new ReferenceSourceStore(paths);
            var runs = new AnalysisRunStore(paths);
            var reports = new NovelAnalysisReportService(sources, runs);
            var complete = sources.List().SelectMany(book => runs.List(book.Id)).Where(run => run.State == AnalysisRunState.Completed)
                .Select(run => reports.Read(run.Id)).First(report => report.IsComplete);
            var conversions = new ReportTemplateConversionStore(paths).List(complete.RunId);
            var templates = new TemplateStore(paths).List();
            File.WriteAllText(Path.Combine(root, "desktop-input.json"), JsonSerializer.Serialize(new
            {
                complete.RunId, complete.Version, complete.SourceCharacters, complete.Model,
                PluginAssembly = typeof(WorkspacePaths).Assembly.GetName().Version?.ToString(),
                NativeShell = "Production Host App/Shell/Dock/Picker", ModelReplacement = "ConversionFixtureModel",
                CredentialFilesCopied = false,
                SavedConversions = conversions.Select(c => new { c.Id, c.TemplateId, State = c.State.ToString(), c.Source.ReportVersion,
                    Requests = new ModelRequestStore(paths).List(c.Budget.Id).Count }).ToArray(),
                SavedTemplates = templates.Select(t => new { t.Id, Versions = t.Versions.Length, ConversionId = t.Draft.Provenance?.ConversionId }).ToArray()
            }, new JsonSerializerOptions { WriteIndented = true }));
        }, diagnostics);
        int exit;
        try { exit = runtime.BuildAvaloniaApp().StartWithClassicDesktopLifetime([]); }
        finally { runtime.Dispose(); }
        // 退出收据必须晚于真正的 Runtime 释放。消息循环返回不能替代插件关闭成功。
        File.WriteAllText(Path.Combine(root, "desktop-exit.json"), JsonSerializer.Serialize(new
        { ExitCode = exit, RuntimeShutdownSucceeded = true, FixtureRequests = model.Requests, NetworkCalls = 0, FinishedAt = DateTimeOffset.UtcNow }));
        return exit;
    }
}
