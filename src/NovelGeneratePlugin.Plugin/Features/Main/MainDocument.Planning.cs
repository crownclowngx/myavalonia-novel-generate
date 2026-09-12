using System.Collections.Immutable;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Domain;
namespace NovelGeneratePlugin.Features.Main;

public sealed partial class MethodApplicationEditor(Action changed, Action<MethodApplicationEditor> remove) : ObservableObject
{
    [ObservableProperty] private string _sourceQuote = "";
    [ObservableProperty] private string _setupEvent = "";
    [ObservableProperty] private string _pressureEvent = "";
    [ObservableProperty] private string _payoffEvent = "";
    partial void OnSourceQuoteChanged(string value) => changed();
    partial void OnSetupEventChanged(string value) => changed();
    partial void OnPressureEventChanged(string value) => changed();
    partial void OnPayoffEventChanged(string value) => changed();
    [RelayCommand] private void Remove() => remove(this);
    public MethodApplication Capture() => new(SourceQuote, SetupEvent, PressureEvent, PayoffEvent);
}
public sealed partial class MainDocument
{
    [ObservableProperty] private string _planningMainline = "";
    [ObservableProperty] private string _volumeGoal = "";
    [ObservableProperty] private string _planGoal = "";
    [ObservableProperty] private string _planConflict = "";
    [ObservableProperty] private string _planViewpoint = "";
    [ObservableProperty] private string _planTimePlace = "";
    [ObservableProperty] private string _planEvents = "";
    [ObservableProperty] private string _planStateChanges = "";
    [ObservableProperty] private string _planForeshadow = "";
    [ObservableProperty] private string _planBridge = "";
    [ObservableProperty] private bool _planLocked;
    [ObservableProperty] private int _planningChapterCount = 3;
    [ObservableProperty] private PlanningMode _selectedPlanningMode;
    [ObservableProperty] private string _planningStatus = "填写创意、绑定连接后，可从当前章起生成 3–5 章近期规划。";
    [ObservableProperty] private string _planningPreview = "尚无待采用规划";
    [ObservableProperty] private string _planningHistoryText = "尚无规划修订";
    private CancellationTokenSource? _planningCancellation;
    public ObservableCollection<MethodApplicationEditor> PlanMethods { get; } = [];
    public IReadOnlyList<PlanningMode> PlanningModes { get; } = Enum.GetValues<PlanningMode>();
    public bool CanCancelPlanning => _planningCancellation is not null;
    public bool CanApplyPlanning => CanEdit && _session?.Current.Planning.Pending is not null;
    private void NotifyPlanningCommands()
    {
        NotifyWorkbenchCommands();
        GeneratePlanningCommand.NotifyCanExecuteChanged(); CancelPlanningCommand.NotifyCanExecuteChanged(); ApplyPlanningCommand.NotifyCanExecuteChanged();
        SaveChapterPlanCommand.NotifyCanExecuteChanged(); DiscardPlanningCandidateCommand.NotifyCanExecuteChanged(); AddPlanMethodCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanCancelPlanning)); OnPropertyChanged(nameof(CanApplyPlanning));
    }
    partial void OnPlanningMainlineChanged(string value) => CapturePlanEditor();
    partial void OnVolumeGoalChanged(string value) => CapturePlanEditor();
    partial void OnPlanGoalChanged(string value) => CapturePlanEditor();
    partial void OnPlanConflictChanged(string value) => CapturePlanEditor();
    partial void OnPlanViewpointChanged(string value) => CapturePlanEditor();
    partial void OnPlanTimePlaceChanged(string value) => CapturePlanEditor();
    partial void OnPlanEventsChanged(string value) => CapturePlanEditor();
    partial void OnPlanStateChangesChanged(string value) => CapturePlanEditor();
    partial void OnPlanForeshadowChanged(string value) => CapturePlanEditor();
    partial void OnPlanBridgeChanged(string value) => CapturePlanEditor();
    partial void OnPlanLockedChanged(bool value) => CapturePlanEditor();
    private void CapturePlanEditor()
    {
        if (_loading || _session is null || SelectedChapter is null || _closing.IsCancellationRequested) return;
        var book = _session.Current; var chapter = book.Chapters.Single(c => c.Id == SelectedChapter.Id); var volume = book.Volumes.Single(v => v.Id == chapter.VolumeId);
        var plan = new ChapterPlan(PlanGoal, PlanConflict, PlanViewpoint, PlanTimePlace, PlanEvents, PlanStateChanges, PlanForeshadow, PlanBridge,
            PlanMethods.Select(m => m.Capture()).ToImmutableArray(), PlanLocked, chapter.Plan.ReadyStamp);
        _session.Update(book with
        {
            Planning = book.Planning with { Mainline = PlanningMainline },
            Volumes = book.Volumes.Replace(volume, volume with { Goal = VolumeGoal }),
            Chapters = book.Chapters.Replace(chapter, chapter with { Plan = plan })
        });
        UpdatePlanningStatus(); UpdateStoryContextStatus();
    }
    private void LoadPlanning()
    {
        if (_session is null || SelectedChapter is null) return;
        var book = _session.Current; var chapter = book.Chapters.Single(c => c.Id == SelectedChapter.Id); var plan = chapter.Plan;
        _loading = true;
        try
        {
            PlanningMainline = book.Planning.Mainline; VolumeGoal = book.Volumes.Single(v => v.Id == chapter.VolumeId).Goal;
            PlanGoal = plan.Goal; PlanConflict = plan.Conflict; PlanViewpoint = plan.Viewpoint; PlanTimePlace = plan.TimePlace;
            PlanEvents = plan.Events; PlanStateChanges = plan.StateChanges; PlanForeshadow = plan.Foreshadow; PlanBridge = plan.Bridge; PlanLocked = plan.Locked;
            PlanMethods.Clear(); foreach (var method in plan.Methods) PlanMethods.Add(new(CapturePlanEditor, RemovePlanMethod) { SourceQuote = method.SourceQuote, SetupEvent = method.SetupEvent, PressureEvent = method.PressureEvent, PayoffEvent = method.PayoffEvent });
            PlanningHistoryText = book.Planning.History.Length == 0 ? "尚无规划修订" : string.Join('\n', book.Planning.History.TakeLast(10).Reverse().Select(r => $"v{r.Number} · {r.CreatedAt.LocalDateTime:g} · {r.Source}"));
            PlanningPreview = book.Planning.Pending is { } pending ? RenderPlanning(pending.Proposal) : "尚无待采用规划";
        }
        finally { _loading = false; }
        UpdatePlanningStatus(); NotifyPlanningCommands();
    }
    private void UpdatePlanningStatus()
    {
        if (_session is null || SelectedChapter is null || _planningCancellation is not null) return;
        var book = _session.Current;
        try { PlanningRules.RequireReady(book, SelectedChapter.Id); PlanningStatus = "本章规划已通过结构与来源检查，可进入章节生成。"; }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException) { PlanningStatus = exception.Message; }
        if (book.Planning.Pending is { } pending && pending.SourceStamp != PlanningRules.SourceStamp(book)) PlanningStatus += " 待采用候选已过期，请重新生成或放弃。";
    }
    private void RemovePlanMethod(MethodApplicationEditor editor) { if (!CanEdit) return; PlanMethods.Remove(editor); CapturePlanEditor(); }
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void AddPlanMethod() { PlanMethods.Add(new(CapturePlanEditor, RemovePlanMethod)); CapturePlanEditor(); }
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task SaveChapterPlan() => RunAsync(async () =>
    {
        var book = _session!.Current; var chapter = book.Chapters.Single(c => c.Id == SelectedChapter!.Id);
        // 手工字段保存为一个明确版本；普通输入只自动保存草案，不为每次按键膨胀修订历史。
        book = book with { Chapters = book.Chapters.Replace(chapter, chapter with { Outline = chapter.Plan.Render() }) };
        book = PlanningRules.Record(PlanningRules.MarkReady(book, chapter.Id), Guid.NewGuid(), "作者手工规划");
        _session.Update(book); var saved = await _session.SaveAsync(); Status = saved.Message; LoadChapter(chapter.Id);
    });
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task GeneratePlanning() => RunAsync(async () =>
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(OperationToken); _planningCancellation = cancellation; NotifyPlanningCommands();
        var mode = SelectedPlanningMode;
        try
        {
            var snapshot = _session!.Current; var chapterId = SelectedChapter!.Id;
            PlanningStatus = "正在生成分层规划；将消耗所绑定连接的额度。";
            var candidate = await planning.GenerateAsync(snapshot, chapterId, PlanningChapterCount, new(Guid.NewGuid(), 1, 250000), cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (candidate.SourceStamp != PlanningRules.SourceStamp(_session.Current)) throw new InvalidOperationException("生成期间作品已变化，未覆盖当前内容；模型候选保留在请求记录中。");
            _session.Update(_session.Current with { Planning = _session.Current.Planning with { Pending = candidate } });
            // 即使自动采用时发现卷归属或其他冲突，候选也先进入本书保存与恢复链路。
            var saved = await _session.SaveAsync();
            if (!saved.Saved && !saved.RecoveryAvailable) throw new IOException(saved.Message);
            cancellation.Token.ThrowIfCancellationRequested();
            if (mode == PlanningMode.Automatic) await ApplyPlanningCoreAsync(); else LoadPlanning();
        }
        finally { _planningCancellation = null; NotifyPlanningCommands(); UpdatePlanningStatus(); }
    });
    [RelayCommand(CanExecute = nameof(CanCancelPlanning))]
    private void CancelPlanning() => _planningCancellation?.Cancel();
    [RelayCommand(CanExecute = nameof(CanApplyPlanning))]
    private Task ApplyPlanning() => RunAsync(ApplyPlanningCoreAsync);
    private async Task ApplyPlanningCoreAsync()
    {
        var candidate = _session!.Current.Planning.Pending ?? throw new InvalidOperationException("没有待采用规划。");
        var updated = PlanningRules.Apply(_session.Current, candidate); _session.Update(updated);
        var result = await _session.SaveAsync(); Status = result.Message; ReloadChapters(candidate.StartChapterId); LoadStory();
        Notice = result.Saved ? "分层规划已采用并保存；未来状态仍是计划，不是已发生事实。"
            : result.RecoveryAvailable ? "规划仅写入恢复副本，原作品尚未保存，请修复存储后重试保存。" : "规划只在内存中，作品和恢复副本均未保存，请保留窗口并重试保存。";
        if (!result.Saved && !result.RecoveryAvailable) throw new IOException(result.Message);
    }
    [RelayCommand(CanExecute = nameof(CanApplyPlanning))]
    private void DiscardPlanningCandidate() { _session!.Update(_session.Current with { Planning = _session.Current.Planning with { Pending = null } }); LoadPlanning(); }
    private static string RenderPlanning(PlanningProposal proposal) => proposal.Mainline + "\n\n" + string.Join("\n\n", proposal.Volumes.Select((v, i) => $"第 {i + 1} 卷 {v.Title}：{v.Goal}")) +
        "\n\n" + string.Join("\n\n", proposal.Chapters.Select(c => $"{c.Title}\n{c.ToPlan().Render()}"));
}
