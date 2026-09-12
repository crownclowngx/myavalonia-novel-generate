using Avalonia.Controls;
using Avalonia.Platform.Storage;
using MyAvaloniaManagement.PluginSdk.UI;
namespace NovelGeneratePlugin.Standalone;

/// <summary>只为开发窗口提供与公开 SDK 相同的文件选择语义，不进入生产程序集。</summary>
public sealed class PreviewWindowInteraction(Window window) : IPluginWindowInteraction
{
    public async Task<IReadOnlyList<string>> PickOpenFilesAsync(FilePickerOpenOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var files = await window.StorageProvider.OpenFilePickerAsync(options);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToArray();
        }
        finally { foreach (var file in files) file.Dispose(); }
    }
    public async Task<string?> PickSaveFileAsync(FilePickerSaveOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var file = await window.StorageProvider.SaveFilePickerAsync(options);
        cancellationToken.ThrowIfCancellationRequested();
        return file?.TryGetLocalPath();
    }
    public Task<bool> TrySetClipboardTextAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // G0002 没有剪贴板功能；不用开发替身伪造成功。
        return Task.FromResult(false);
    }
}
