using CommunityToolkit.Mvvm.ComponentModel;
using MyAvaloniaManagement.PluginSdk;

namespace NovelGeneratePlugin.Features.Main;

/// <summary>一本小说的展示入口。项目数据不占用 Host 文档信封。</summary>
public sealed partial class MainDocument : ObservableObject, IPluginDocument, IAsyncDisposable
{
    private DocumentPresentationState _presentation = new("小说创作");
    private bool _disposed;
    [ObservableProperty] private string _status = "本地项目功能准备中";
    [ObservableProperty] private string _bookTitle = "未命名小说";
    [ObservableProperty] private string _idea = "";
    public DocumentPresentationState Presentation => _presentation;
    public event EventHandler? PresentationChanged;
    public ValueTask InitializeAsync(DocumentActivation activation, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(activation);
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        if (activation is not NewDocumentActivation)
            throw new NotSupportedException("请从小说工作区打开 .noveldb 项目。");
        _presentation = new DocumentPresentationState(string.IsNullOrWhiteSpace(activation.Title) ? "小说创作" : activation.Title);
        PresentationChanged?.Invoke(this, EventArgs.Empty);
        return ValueTask.CompletedTask;
    }
    public ValueTask DisposeAsync() { _disposed = true; return ValueTask.CompletedTask; }
}
