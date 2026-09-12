using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovelGeneratePlugin.Application.Models;
namespace NovelGeneratePlugin.Features.Main;

public sealed partial class MainDocument
{
    [ObservableProperty] private int _runChapterCount = 3;
    [ObservableProperty] private int _runMaximumRequests = 20;
    [ObservableProperty] private long _runMaximumTokens = 1000000;
    [ObservableProperty] private bool _runPlanIfMissing = true;
    [ObservableProperty] private bool _acknowledgeRunRetry;
    [ObservableProperty] private string _runStatus = "可连续生成 3–5 章，检查通过后自动提交工作稿；正式稿仍需作者定稿。";
    [ObservableProperty] private string _runUsage = "尚无运行用量";
    private ContinuousRun? _currentRun;
    private RunControl? _runControl;
    private CancellationTokenSource? _runCancellation;
    private long _runViewEpoch;
    public bool CanPauseRun => _runControl is not null;
    public bool CanResumeRun => CanEdit && _currentRun is not null && _currentRun.BookId == _session?.Id && _currentRun.State != ContinuousRunState.Completed;
    private void NotifyRunCommands()
    {
        NotifyWorkbenchCommands();
        StartContinuousCommand.NotifyCanExecuteChanged(); ResumeContinuousCommand.NotifyCanExecuteChanged(); LoadContinuousCommand.NotifyCanExecuteChanged();
        PauseContinuousCommand.NotifyCanExecuteChanged(); CancelContinuousCommand.NotifyCanExecuteChanged(); AbandonContinuousCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanPauseRun)); OnPropertyChanged(nameof(CanResumeRun));
    }
    private void ShowRun(ContinuousRun run)
    {
        if (_disposed || run.BookId != _session?.Id || _currentRun?.Id == run.Id && _currentRun.Sequence > run.Sequence) return;
        _currentRun = run;
        var state = run.State switch
        {
            ContinuousRunState.Completed => "已完成",
            ContinuousRunState.Cancelled => "已取消",
            ContinuousRunState.Paused => "已暂停",
            ContinuousRunState.NeedsAttention => "待处理",
            ContinuousRunState.Committing => "正在提交",
            ContinuousRunState.Running => "运行中",
            ContinuousRunState.Queued => "已排队",
            _ => "中断/失败"
        };
        if (_runControl is null && run.State is ContinuousRunState.Running or ContinuousRunState.Queued or ContinuousRunState.Committing) state = "中断待恢复";
        RunStatus = $"{state} · 已完成 {run.NextChapter}/{run.Policy.ChapterCount} 章 · {run.Message}";
        try { var usage = continuous.Usage(run); RunUsage = $"请求 {usage.Count}/{run.Budget.MaximumRequests} · 已计/保守预留 {usage.Sum(r => r.ChargedTokens)}/{run.Budget.MaximumTokens} token"; }
        catch (Exception error) when (error is not OutOfMemoryException) { RunUsage = "用量暂不可读，请先检查本地账本。"; }
        NotifyRunCommands();
    }
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task StartContinuous() => RunContinuousOperation(false);
    [RelayCommand(CanExecute = nameof(CanResumeRun))]
    private Task ResumeContinuous() => RunContinuousOperation(true);
    private Task RunContinuousOperation(bool resume) => RunAsync(async () =>
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(OperationToken); _runCancellation = cancellation; _runControl = new(); var epoch = ++_runViewEpoch; NotifyRunCommands();
        var progress = new Progress<ContinuousRun>(run => { if (epoch == _runViewEpoch) ShowRun(run); });
        try
        {
            var result = resume ? await continuous.ResumeAsync(_session!, _currentRun!.Id, RunMaximumRequests, RunMaximumTokens, AcknowledgeRunRetry, _runControl, progress, cancellation.Token)
                : await continuous.StartAsync(_session!, SelectedChapter!.Id, new(RunChapterCount, GenerationTargetCharacters, GenerationMaximumRepairs, RunMaximumRequests, RunMaximumTokens, RunPlanIfMissing), _runControl, progress, cancellation.Token);
            ShowRun(result); ReloadChapters(SelectedChapter!.Id); LoadStory(); AcknowledgeRunRetry = false;
        }
        finally { _runControl = null; _runCancellation = null; NotifyRunCommands(); }
    });
    [RelayCommand(CanExecute = nameof(CanPauseRun))] private void PauseContinuous() { _runControl?.RequestPause(); RunStatus = "已请求暂停，将在当前章完整检查与提交后停止。"; }
    [RelayCommand(CanExecute = nameof(CanPauseRun))] private void CancelContinuous() => _runCancellation?.Cancel();
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task LoadContinuous() => RunAsync(async () =>
    {
        var run = await continuous.LoadAsync(_session!.Id); _currentRun = run;
        if (run is null) { RunStatus = "本书尚无连续运行记录。"; RunUsage = "尚无运行用量"; }
        else { RunMaximumRequests = run.Budget.MaximumRequests; RunMaximumTokens = run.Budget.MaximumTokens; ShowRun(run); }
        NotifyRunCommands();
    });
    [RelayCommand(CanExecute = nameof(CanResumeRun))]
    private void AbandonContinuous() => ShowRun(continuous.Abandon(_session!.Id, _currentRun!.Id));
}
