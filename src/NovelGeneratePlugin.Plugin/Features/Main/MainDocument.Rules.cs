using System.Collections.Immutable;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovelGeneratePlugin.Domain;
namespace NovelGeneratePlugin.Features.Main;

public sealed record WritingRuleItem(Guid Id, string Label) { public override string ToString() => Label; }
public sealed record FindingItem(RuleFinding Finding)
{ public override string ToString() => $"{(Finding.Strength == WritingRuleStrength.Hard ? "硬规则" : "建议")} · {Finding.Message} · {(Finding.Start < 0 ? "无出现位置" : $"位置 {Finding.Start + 1}")}：{Finding.Evidence}"; }
public sealed partial class MainDocument
{
    private readonly object _ruleStatusSync = new();
    private (Guid BookId, Guid ChapterId, LocalRuleCheck? Check, bool Current)? _displayedRuleCheck;
    [ObservableProperty] private WritingRuleItem? _selectedWritingRule;
    [ObservableProperty] private string _ruleOriginal = "";
    [ObservableProperty] private string _ruleInterpretation = "";
    [ObservableProperty] private WritingRuleKind _ruleKind;
    [ObservableProperty] private WritingRuleScope _ruleScope = WritingRuleScope.Book;
    [ObservableProperty] private WritingRuleStrength _ruleStrength;
    [ObservableProperty] private string _rulePattern = "";
    [ObservableProperty] private bool _ruleIgnoreSeparators;
    [ObservableProperty] private string _ruleExceptions = "";
    [ObservableProperty] private string _ruleSource = "作者输入";
    [ObservableProperty] private bool _ruleEnabled = true;
    [ObservableProperty] private string _localRuleStatus = "尚未执行本地规则检查";
    [ObservableProperty] private FindingItem? _selectedFinding;
    [ObservableProperty] private int _editorSelectionStart;
    [ObservableProperty] private int _editorSelectionEnd;
    public ObservableCollection<WritingRuleItem> WritingRules { get; } = [];
    public ObservableCollection<FindingItem> RuleFindings { get; } = [];
    public bool HasRuleDraftChanges => _session is not null && _session.Current.RuleEditor !=
        (_session.Current.Rules.Items.FirstOrDefault(r => r.Id == _session.Current.RuleEditor.EditingId) is { } rule ? RuleDraft.From(rule) : RuleDraft.Empty);
    public bool CanSelectRule => CanEdit && !HasRuleDraftChanges;
    private void NotifyRuleDraft()
    { OnPropertyChanged(nameof(HasRuleDraftChanges)); OnPropertyChanged(nameof(CanSelectRule)); NewRuleCommand.NotifyCanExecuteChanged(); }
    public IReadOnlyList<WritingRuleKind> RuleKinds { get; } = Enum.GetValues<WritingRuleKind>();
    public IReadOnlyList<WritingRuleScope> RuleScopes { get; } = Enum.GetValues<WritingRuleScope>();
    public IReadOnlyList<WritingRuleStrength> RuleStrengths { get; } = Enum.GetValues<WritingRuleStrength>();
    partial void OnRuleOriginalChanged(string value) => CaptureRuleEditor();
    partial void OnRuleInterpretationChanged(string value) => CaptureRuleEditor();
    partial void OnRuleKindChanged(WritingRuleKind value) => CaptureRuleEditor();
    partial void OnRuleScopeChanged(WritingRuleScope value) => CaptureRuleEditor();
    partial void OnRuleStrengthChanged(WritingRuleStrength value) => CaptureRuleEditor();
    partial void OnRulePatternChanged(string value) => CaptureRuleEditor();
    partial void OnRuleIgnoreSeparatorsChanged(bool value) => CaptureRuleEditor();
    partial void OnRuleExceptionsChanged(string value) => CaptureRuleEditor();
    partial void OnRuleSourceChanged(string value) => CaptureRuleEditor();
    partial void OnRuleEnabledChanged(bool value) => CaptureRuleEditor();
    private RuleDraft CaptureRuleDraft() => new(SelectedWritingRule?.Id, RuleOriginal, RuleInterpretation, RuleKind, RuleScope, RuleStrength, RulePattern, RuleIgnoreSeparators, RuleExceptions, RuleSource, RuleEnabled);
    private void CaptureRuleEditor()
    {
        if (_loading || _session is null || _closing.IsCancellationRequested) return;
        _session.Update(_session.Current with { RuleEditor = CaptureRuleDraft() });
        NotifyRuleDraft();
        LocalRuleStatus = "规则表单草案已进入自动保存；点击“保存规则版本”后才改变启用规则。";
    }
    partial void OnSelectedWritingRuleChanged(WritingRuleItem? oldValue, WritingRuleItem? newValue)
    {
        if (_loading || _session is null) return;
        if (!CanSelectRule)
        {
            _loading = true; try { SelectedWritingRule = oldValue; } finally { _loading = false; }
            Notice = "请先保存规则版本或明确放弃规则草案，再切换规则。"; return;
        }
        if (newValue is null) { LoadRuleDraft(RuleDraft.Empty); CaptureRuleEditor(); return; }
        var rule = _session.Current.Rules.Items.Single(r => r.Id == newValue.Id);
        LoadRuleDraft(RuleDraft.From(rule));
        CaptureRuleEditor();
    }
    private void LoadRules()
    {
        if (_session is null) return;
        _loading = true;
        try
        {
            WritingRules.Clear();
            foreach (var rule in _session.Current.Rules.Items) WritingRules.Add(new WritingRuleItem(rule.Id, $"{(rule.Enabled ? "启用" : "停用")} · v{rule.Version} · {rule.Original}"));
            SelectedWritingRule = WritingRules.SingleOrDefault(r => r.Id == _session.Current.RuleEditor.EditingId);
        }
        finally { _loading = false; }
        LoadRuleDraft(_session.Current.RuleEditor); NotifyRuleDraft(); UpdateLocalRuleStatus();
    }
    private void LoadRuleDraft(RuleDraft draft)
    {
        _loading = true;
        try
        {
            RuleOriginal = draft.Original; RuleInterpretation = draft.Interpretation; RuleKind = draft.Kind; RuleScope = draft.Scope;
            RuleStrength = draft.Strength; RulePattern = draft.Pattern; RuleIgnoreSeparators = draft.IgnoreSeparators; RuleExceptions = draft.ExceptionsText;
            RuleSource = draft.Source; RuleEnabled = draft.Enabled;
        }
        finally { _loading = false; }
    }
    [RelayCommand(CanExecute = nameof(CanSelectRule))]
    private void NewRule()
    {
        if (!CanSelectRule) return;
        _loading = true; try { SelectedWritingRule = null; } finally { _loading = false; }
        LoadRuleDraft(RuleDraft.Empty); CaptureRuleEditor();
    }
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void DiscardRuleDraft()
    {
        var previous = _session!.Current.Rules.Items.FirstOrDefault(r => r.Id == SelectedWritingRule?.Id);
        LoadRuleDraft(previous is null ? RuleDraft.Empty : RuleDraft.From(previous)); CaptureRuleEditor();
        Notice = "已明确放弃当前规则未提交的草案，已保存版本不变。";
    }
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task SaveRuleVersion() => RunAsync(async () =>
    {
        var updated = WritingRuleSet.Commit(_session!.Current, CaptureRuleDraft(), SelectedChapter!.Id);
        _session.Update(updated); var result = await _session.SaveAsync(); Status = result.Message; LoadRules(); UpdateStoryContextStatus();
        Notice = "规则版本已更新；尚未用于模型请求，本地检查也不等于语义审校。";
    });
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task CheckLocalRules() => RunAsync(async () =>
    {
        var snapshot = _session!.Current; var chapterId = SelectedChapter!.Id;
        var result = await Task.Run(() => RuleEvaluation.Check(snapshot, chapterId, snapshot.Revisions.ActiveRunId));
        var current = _session.Current;
        if (!RuleEvaluation.IsCurrent(current, result)) throw new InvalidOperationException("检查期间正文、规则或运行已变化，请重新检查。");
        _session.Update(current with { RuleChecks = current.RuleChecks.Where(c => c.ChapterId != chapterId).Append(result).ToImmutableArray() });
        var saved = await _session.SaveAsync(); Status = saved.Message; UpdateLocalRuleStatus();
    });
    private void UpdateLocalRuleStatus()
    {
        // 存储事件可能从后台到达无 UI 上下文的调用方；串行更新，且相同检查不重建列表或清掉选择。
        lock (_ruleStatusSync) UpdateLocalRuleStatusCore();
    }
    private void UpdateLocalRuleStatusCore()
    {
        if (_session is null || SelectedChapter is null) return;
        var book = _session.Current; var check = book.RuleChecks.LastOrDefault(c => c.ChapterId == SelectedChapter.Id);
        var isCurrent = check is not null && RuleEvaluation.IsCurrent(book, check);
        var key = (book.Id, SelectedChapter.Id, check, isCurrent);
        if (_displayedRuleCheck == key) return;
        _displayedRuleCheck = key;
        RuleFindings.Clear(); SelectedFinding = null;
        if (check is null) { LocalRuleStatus = "尚未检查；启用规则也尚未用于模型请求。"; return; }
        if (!isCurrent) { LocalRuleStatus = "旧检查已过期：正文、规则或运行发生变化，请重新检查。"; return; }
        foreach (var finding in check.Findings) RuleFindings.Add(new FindingItem(finding));
        LocalRuleStatus = $"本地检查：{(check.HasHardFailure ? "存在硬规则问题" : "未发现确定性硬规则问题")}；命中 {check.Findings.Length}，冲突 {check.Conflicts.Length}；语义待检查 {check.GuidanceCount}。";
        if (!check.Complete) LocalRuleStatus = "检查未完成：命中或例外超过 1000 项，请调整规则或缩短正文后重试。";
        if (check.Conflicts.Length > 0) LocalRuleStatus += " " + check.Conflicts[0].Message;
    }
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void LocateRuleFinding()
    {
        if (SelectedFinding?.Finding is not { } finding || finding.Start < 0) return;
        var check = _session!.Current.RuleChecks.LastOrDefault(c => c.ChapterId == SelectedChapter!.Id);
        if (check is null || !RuleEvaluation.IsCurrent(_session.Current, check)) { UpdateLocalRuleStatus(); return; }
        EditorSelectionStart = finding.Start; EditorSelectionEnd = finding.Start + finding.Length;
    }
}
