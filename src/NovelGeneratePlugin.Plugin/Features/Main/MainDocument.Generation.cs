using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovelGeneratePlugin.Domain;
namespace NovelGeneratePlugin.Features.Main;

public sealed record GenerationIssueChoice(ChapterIssue Issue) { public override string ToString() => Issue.Message; }
public sealed record ChapterWorkChoice(ChapterWork Work)
{
    public override string ToString() => $"{Work.Id.ToString("N")[..8]} · {Work.Characters} 字 · {Work.Message}";
}
public sealed partial class MainDocument
{
    [ObservableProperty] private int _generationTargetCharacters = 2000;
    [ObservableProperty] private int _generationMaximumRepairs = 1;
    [ObservableProperty] private string _generationText = "";
    [ObservableProperty] private string _generationStatus = "保存有效章纲后可生成单章候选；检查通过后提交工作稿。";
    [ObservableProperty] private string _generationReport = "尚无审校报告";
    [ObservableProperty] private ChapterWorkChoice? _selectedGeneration;
    [ObservableProperty] private GenerationIssueChoice? _selectedGenerationIssue;
    [ObservableProperty] private int _generationSelectionStart;
    [ObservableProperty] private int _generationSelectionEnd;
    public ObservableCollection<GenerationIssueChoice> GenerationIssues { get; } = [];
    private ChapterWork? _currentGeneration;
    private long _generationViewEpoch;
    private CancellationTokenSource? _generationCancellation;
    public ObservableCollection<ChapterWorkChoice> GenerationChoices { get; } = [];
    public bool CanCancelGeneration => _generationCancellation is not null;
    public bool CanCommitGeneration => CanEdit && _currentGeneration?.State == ChapterWorkState.Ready && _currentGeneration.ChapterId == SelectedChapter?.Id;
    private void NotifyGenerationCommands()
    {
        GenerateChapterCommand.NotifyCanExecuteChanged(); CancelGenerationCommand.NotifyCanExecuteChanged(); RefreshGenerationCommand.NotifyCanExecuteChanged(); CommitGenerationCommand.NotifyCanExecuteChanged();
        LocateGenerationIssueCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanCancelGeneration)); OnPropertyChanged(nameof(CanCommitGeneration));
    }
    partial void OnSelectedGenerationChanged(ChapterWorkChoice? value) { if (value is not null && CanEdit) ShowGeneration(value.Work); }
    private void ResetGenerationView()
    {
        _generationViewEpoch++; _currentGeneration = null; GenerationText = ""; GenerationReport = "点击恢复候选可查看本章已保存的任务结果。"; GenerationChoices.Clear(); GenerationIssues.Clear(); SelectedGenerationIssue = null; SelectedGeneration = null; NotifyGenerationCommands();
    }
    private void ShowGeneration(ChapterWork work)
    {
        if (_disposed || _session?.Id != work.BookId || SelectedChapter?.Id != work.ChapterId) return;
        if (_currentGeneration?.Id == work.Id && _currentGeneration.UpdateSequence > work.UpdateSequence) return;
        _currentGeneration = work; GenerationText = work.Text; GenerationStatus = $"{work.SourceDescription} · {work.Message} 本地 {work.Characters} 字（目标 {work.TargetCharacters}，±20%）。";
        GenerationReport = string.Join('\n', work.Issues.Select(i => $"{i.Severity switch { ReviewSeverity.Hard => "硬失败", ReviewSeverity.Uncertain => "待核实", _ => "建议" }} · {i.Message}\n证据：{i.Evidence}"));
        GenerationIssues.Clear(); foreach (var issue in work.Issues) GenerationIssues.Add(new(issue));
        if (work.Review is { } review) GenerationReport = "摘要：" + review.Summary + "\n" + GenerationReport;
        CandidateDifference = EditingRules.Difference(ChapterText, work.Text).Describe();
        NotifyGenerationCommands();
    }
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void LocateGenerationIssue()
    {
        if (SelectedGenerationIssue is null) return;
        var start = SelectedGenerationIssue.Issue.Locate(GenerationText);
        if (start < 0) { GenerationStatus = "此问题没有可定位正文证据，请阅读完整报告。"; return; }
        GenerationSelectionStart = start; GenerationSelectionEnd = start + SelectedGenerationIssue.Issue.Evidence.Length;
    }
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task GenerateChapter() => RunAsync(async () =>
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token); _generationCancellation = cancellation; NotifyGenerationCommands();
        var book = _session!.Current; var chapter = SelectedChapter!.Id; var epoch = ++_generationViewEpoch;
        try
        {
            // Progress 捕获窗口上下文；候选文本独立显示，绝不把流片段赋给 ChapterText。
            var result = await generation.GenerateAsync(book, chapter, book.Revisions.ActiveRunId ?? Guid.NewGuid(), GenerationTargetCharacters,
                GenerationMaximumRepairs, new(Guid.NewGuid(), 2 + GenerationMaximumRepairs * 2, 500000), new Progress<ChapterWork>(work => { if (epoch == _generationViewEpoch) ShowGeneration(work); }), cancellation.Token);
            ShowGeneration(result);
        }
        finally { _generationCancellation = null; NotifyGenerationCommands(); }
    });
    [RelayCommand(CanExecute = nameof(CanCancelGeneration))] private void CancelGeneration() => _generationCancellation?.Cancel();
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task RefreshGeneration() => RunAsync(async () =>
    {
        var entries = await generation.RecentAsync(_session!.Id, SelectedChapter!.Id); GenerationChoices.Clear();
        foreach (var entry in entries) GenerationChoices.Add(new(entry));
        if (entries.Count > 0) ShowGeneration(entries[0]);
    });
    [RelayCommand(CanExecute = nameof(CanCommitGeneration))]
    private Task CommitGeneration() => RunAsync(async () =>
    {
        var candidate = _currentGeneration!;
        await _session!.CommitGeneratedChapterAsync(candidate, _closing.Token);
        LoadChapter(candidate.ChapterId); ShowGeneration(candidate);
        Notice = "正文、摘要与事实增量已一起保存为工作稿；正式稿仍由作者主动定稿。";
    });
}
