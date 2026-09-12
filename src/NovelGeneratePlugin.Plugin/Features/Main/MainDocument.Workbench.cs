using System.Windows.Input;
using CommunityToolkit.Mvvm.Input;
using MyAvaloniaManagement.PluginSdk;
using NovelGeneratePlugin.Plugin;
namespace NovelGeneratePlugin.Features.Main;

/// <summary>宿主命令是按钮用例的窄适配。Host 在执行时选择活动 Document，插件不查找或反射 Dock。</summary>
public sealed partial class MainDocument : IWorkbenchDocumentCommandTarget
{
    private CancellationTokenSource? _workbenchCancellation;
    private CancellationToken OperationToken => _workbenchCancellation?.Token ?? _closing.Token;
    private Exception? _lastOperationError;
    public event EventHandler<WorkbenchCommandStateChangedEventArgs>? CommandStateChanged;
    public bool CanCancelCreation => !_disposed && !_closing.IsCancellationRequested && (CanCancelPlanning || CanCancelGeneration || CanPauseRun);
    public string ActivityStatus => _runCancellation is not null ? "任务：" + RunStatus : _generationCancellation is not null ? "任务：" + GenerationStatus : _planningCancellation is not null ? "任务：正在规划" : IsBusy ? "任务：正在处理本地操作" : "任务：空闲；保存状态与稿件状态分别显示";
    private ICommand? FindWorkbenchCommand(CommandId id) => id == NovelCommands.Open ? OpenProjectCommand : id == NovelCommands.Start ? StartContinuousCommand :
        id == NovelCommands.Pause ? PauseContinuousCommand : id == NovelCommands.Cancel ? CancelCreationCommand : id == NovelCommands.Generate ? GenerateChapterCommand :
        id == NovelCommands.Finalize ? FinalizeChapterCommand : id == NovelCommands.Export ? PreviewExportCommand : null;
    bool IWorkbenchDocumentCommandTarget.CanExecute(CommandId commandId) => !_disposed && !_closing.IsCancellationRequested && FindWorkbenchCommand(commandId)?.CanExecute(null) == true;
    async ValueTask IWorkbenchDocumentCommandTarget.ExecuteAsync(CommandId commandId, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var command = FindWorkbenchCommand(commandId) ?? throw new ArgumentOutOfRangeException(nameof(commandId), "命令不属于小说 Document。");
        if (!((IWorkbenchDocumentCommandTarget)this).CanExecute(commandId)) throw new InvalidOperationException("当前小说 Document 无法执行此命令。");
        // 暂停/取消可以在另一个异步命令运行中调用，不能覆盖该命令正在使用的令牌。
        if (command is not IAsyncRelayCommand asynchronous) { command.Execute(null); return; }
        if (_workbenchCancellation is not null) throw new InvalidOperationException("已有宿主命令正在执行。");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _closing.Token); _workbenchCancellation = linked;
        try
        {
            await asynchronous.ExecuteAsync(null);
            cancellationToken.ThrowIfCancellationRequested();
            if (_lastOperationError is not null) throw new InvalidOperationException(_lastOperationError.Message, _lastOperationError);
        }
        finally { _workbenchCancellation = null; }
    }
    [RelayCommand(CanExecute = nameof(CanCancelCreation))]
    private void CancelCreation() { _planningCancellation?.Cancel(); _generationCancellation?.Cancel(); _runCancellation?.Cancel(); }
    private void NotifyWorkbenchCommands()
    {
        CancelCreationCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(CanCancelCreation)); OnPropertyChanged(nameof(ActivityStatus));
        if (_disposed) return;
        foreach (var (id, _) in NovelCommands.All)
        {
            // 某个显示订阅者异常不能阻止作者的保存或关闭排空。
            var handlers = CommandStateChanged;
            if (handlers is null) continue;
            foreach (EventHandler<WorkbenchCommandStateChangedEventArgs> handler in handlers.GetInvocationList())
                try { handler(this, new(id)); } catch (Exception error) when (error is not OutOfMemoryException) { }
        }
    }
}
