using Avalonia.Platform.Storage;
using NovelGeneratePlugin.Application.Models;
using MyAvaloniaManagement.PluginSdk.UI;
using NovelGeneratePlugin.Application.Projects;
using NovelGeneratePlugin.Application.Templates;
using NovelGeneratePlugin.Features.Main;
using NovelGeneratePlugin.Infrastructure.Persistence;
using NovelGeneratePlugin.Application.Connections;
using NovelGeneratePlugin.Infrastructure.Credentials;
using NovelGeneratePlugin.Application.Export;
using NovelGeneratePlugin.Infrastructure.Export;
namespace NovelGeneratePlugin.Tests;

/// <summary>所有测试文件均在单次创建的随机临时目录，避免读取或写入作者的真实作品。</summary>
public sealed class TestWorkspace : IAsyncDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "NovelGenerateTests", Guid.NewGuid().ToString("N"));
    public WorkspacePaths Paths { get; }
    public ProjectStore Store { get; } = new();
    public CatalogStore Catalog { get; }
    public RecoveryStore Recovery { get; }
    public TemplateLibrary Templates { get; }
    public UserCredentialVault Vault { get; }
    public ConnectionService Connections { get; }
    public ModelRequestService Models { get; }
    public ArtifactService Artifacts { get; }
    public ProjectSessions Sessions { get; }
    public PluginCloseCoordinator Closing { get; }
    public TestWindowInteraction Interaction { get; } = new();
    public TestWorkspace()
    {
        Directory.CreateDirectory(Root);
        Paths = new WorkspacePaths(Path.Combine(Root, "user-data"));
        Models = new ModelRequestService(new ScriptedTextModel(), new ModelRequestStore(Paths));
        Catalog = new CatalogStore(Paths); Recovery = new RecoveryStore(Paths);
        Templates = new TemplateLibrary(new TemplateStore(Paths));
        Vault = new UserCredentialVault(Paths); Connections = new ConnectionService(new ConnectionStore(Paths), Vault);
        Artifacts = new ArtifactService(new ArtifactFiles(Store, new FileProjectLeaseProvider()), Templates);
        Sessions = new ProjectSessions(Store, Catalog, Recovery, new FileProjectLeaseProvider());
        Closing = new PluginCloseCoordinator(Sessions);
    }
    public string ProjectPath(string name = "作品") => Path.Combine(Root, name + ".noveldb");
    public MainDocument CreateDocument(PlanningService? planning = null) => new(Sessions, Catalog, Recovery, Interaction, Templates, Connections, Artifacts, Closing, planning ?? new PlanningService(Connections, Models));
    public async ValueTask DisposeAsync()
    {
        await Closing.ShutdownAsync(CancellationToken.None);
        Vault.Dispose();
        Directory.Delete(Root, recursive: true);
    }
}
public sealed class TestWindowInteraction : IPluginWindowInteraction
{
    public string? NextPath { get; set; }
    public Task<IReadOnlyList<string>> PickOpenFilesAsync(FilePickerOpenOptions options, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult<IReadOnlyList<string>>(NextPath is null ? [] : [NextPath]); }
    public Task<string?> PickSaveFileAsync(FilePickerSaveOptions options, CancellationToken cancellationToken = default)
    { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(NextPath); }
    public Task<bool> TrySetClipboardTextAsync(string text, CancellationToken cancellationToken = default) => Task.FromResult(false);
}
