using Avalonia.Platform.Storage;
using MyAvaloniaManagement.PluginSdk.UI;
using NovelGeneratePlugin.Application.Projects;
using NovelGeneratePlugin.Features.Main;
using NovelGeneratePlugin.Infrastructure.Persistence;
namespace NovelGeneratePlugin.Tests;

/// <summary>所有测试文件均在单次创建的随机临时目录，避免读取或写入作者的真实作品。</summary>
public sealed class TestWorkspace : IAsyncDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "NovelGenerateTests", Guid.NewGuid().ToString("N"));
    public WorkspacePaths Paths { get; }
    public ProjectStore Store { get; } = new();
    public CatalogStore Catalog { get; }
    public RecoveryStore Recovery { get; }
    public ProjectSessions Sessions { get; }
    public TestWindowInteraction Interaction { get; } = new();
    public TestWorkspace()
    {
        Directory.CreateDirectory(Root);
        Paths = new WorkspacePaths(Path.Combine(Root, "user-data"));
        Catalog = new CatalogStore(Paths); Recovery = new RecoveryStore(Paths);
        Sessions = new ProjectSessions(Store, Catalog, Recovery, new FileProjectLeaseProvider());
    }
    public string ProjectPath(string name = "作品") => Path.Combine(Root, name + ".noveldb");
    public MainDocument CreateDocument() => new(Sessions, Catalog, Recovery, Interaction);
    public async ValueTask DisposeAsync()
    {
        await Sessions.DisposeAsync();
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
