using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Domain;
namespace NovelGeneratePlugin.Features.Main;

public sealed record RevisionChoice(ChapterRevision Revision)
{
    public override string ToString() => $"{Revision.CreatedAt.ToLocalTime():MM-dd HH:mm} · {Revision.Id.ToString("N")[..8]} · {Revision.Title}";
}
public sealed partial class MainDocument
{
    [ObservableProperty] private string _rewriteInstruction = "保留事实与剧情，改善表达和节奏。";
    [ObservableProperty] private string _candidateDifference = "生成或复核候选后，在这里查看正文差异。";
    [ObservableProperty] private string _revisionDifference = "选择本章历史版本，与当前编辑稿对比。";
    [ObservableProperty] private string _impactReport = "尚无待复核修改。";
    [ObservableProperty] private RevisionChoice? _selectedRevision;
    [ObservableProperty] private int _finalizeFirstChapter = 1;
    [ObservableProperty] private int _finalizeLastChapter = 1;
    [ObservableProperty] private bool _confirmFinalizeRange;
    public ObservableCollection<RevisionChoice> RevisionChoices { get; } = [];
    private void NotifyEditingCommands()
    {
        RewriteSelectionCommand.NotifyCanExecuteChanged(); ContinueTextCommand.NotifyCanExecuteChanged(); ReviewCurrentTextCommand.NotifyCanExecuteChanged();
        RejectCandidateCommand.NotifyCanExecuteChanged(); RefreshRevisionsCommand.NotifyCanExecuteChanged(); FinalizeRangeCommand.NotifyCanExecuteChanged();
    }
    private void UpdateEditingStatus()
    {
        if (_session is null) return;
        var impacts = EditingRules.Impacts(_session.Current);
        ImpactReport = impacts.Length == 0 ? "尚无待复核修改。" : string.Join('\n', impacts.Select(i => i.Title + "：" + i.Reason));
        if (_currentGeneration is not null) CandidateDifference = EditingRules.Difference(ChapterText, _currentGeneration.Text).Describe();
        if (SelectedRevision is not null) RevisionDifference = EditingRules.Difference(SelectedRevision.Revision.Text, ChapterText).Describe();
    }
    partial void OnSelectedRevisionChanged(RevisionChoice? value) => UpdateEditingStatus();
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void RefreshRevisions()
    {
        RevisionChoices.Clear(); SelectedRevision = null;
        foreach (var revision in _session!.Current.Revisions.History.Where(r => r.ChapterId == SelectedChapter!.Id).Reverse()) RevisionChoices.Add(new(revision));
        RevisionDifference = "选择历史修订查看与编辑稿的差异；回退和放弃不会删除历史。";
    }
    [RelayCommand(CanExecute = nameof(CanEdit))] private Task RewriteSelection() => EditTextAsync(false, false);
    [RelayCommand(CanExecute = nameof(CanEdit))] private Task ContinueText() => EditTextAsync(true, false);
    [RelayCommand(CanExecute = nameof(CanEdit))] private Task ReviewCurrentText() => EditTextAsync(false, true);
    private Task EditTextAsync(bool append, bool review) => RunAsync(async () =>
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_closing.Token); _generationCancellation = cancellation; NotifyGenerationCommands();
        var book = _session!.Current; var chapter = SelectedChapter!.Id; var epoch = ++_generationViewEpoch; var run = book.Revisions.ActiveRunId ?? Guid.NewGuid();
        var start = append ? ChapterText.Length : Math.Min(EditorSelectionStart, EditorSelectionEnd);
        var edit = new SelectionEdit(start, append ? 0 : Math.Abs(EditorSelectionEnd - EditorSelectionStart), RewriteInstruction, append);
        var budget = new RequestBudget(Guid.NewGuid(), review ? 1 : 2, 500000);
        var progress = new Progress<ChapterWork>(work => { if (epoch == _generationViewEpoch) ShowGeneration(work); });
        try
        {
            var result = review ? await generation.ReviewTextAsync(book, chapter, run, GenerationTargetCharacters, budget, progress, cancellation.Token)
                : await generation.RewriteAsync(book, chapter, run, GenerationTargetCharacters, edit, budget, progress, cancellation.Token);
            ShowGeneration(result);
        }
        finally { _generationCancellation = null; NotifyGenerationCommands(); }
    });
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void RejectCandidate()
    {
        ResetGenerationView(); CandidateDifference = "已放弃当前候选的采用，作者正文未变；任务记录仍可恢复查看。";
    }
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task FinalizeRange() => RunAsync(async () =>
    {
        if (!ConfirmFinalizeRange) throw new InvalidOperationException("请先勾选已逐章审阅，再明确执行连续范围定稿。");
        var first = FinalizeFirstChapter; var last = FinalizeLastChapter;
        await _session!.CommitRevisionChangeAsync(book => EditingRules.FinalizeRange(book, first, last, true), _closing.Token);
        ConfirmFinalizeRange = false; UpdateRevisionStatus(); Notice = $"第 {first}–{last} 章已一次性定稿。";
    });
}
