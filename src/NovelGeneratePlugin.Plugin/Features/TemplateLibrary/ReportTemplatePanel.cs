using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovelGeneratePlugin.Application.Analysis;
using NovelGeneratePlugin.Application.Connections;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Application.Projects;
using NovelGeneratePlugin.Application.Templates;
using NovelGeneratePlugin.Domain;

namespace NovelGeneratePlugin.Features.TemplateLibrary;

public sealed record TemplateConversionChoice(ReportTemplateConversion Value)
{ public override string ToString() => $"{Value.CreatedAt.LocalDateTime:MM-dd HH:mm} · {Value.Name} · {ReportTemplatePanel.StateName(Value.State)}"; }
public sealed record TemplateSourceChoice(ReportTemplateClaim Value)
{ public override string ToString() => $"S{Value.Id} · {Value.Topic}"; }

/// <summary>
/// 面板负责当前报告、转换配置和预览，不拥有模型任务。切换报告以代数阻止迟到加载覆盖，
/// 已保存转换按运行归属列出；打开模板交给共享编辑器处理，避免直接改写其他草案的四个字段。
/// </summary>
public sealed partial class ReportTemplatePanel(ReportTemplateConversionService service, ReportTemplateActivity activity,
    ConnectionService connections, PluginCloseCoordinator shutdown) : ObservableObject, IAsyncDisposable, IDisposable
{
    private NovelAnalysisReport? _report;
    private ReportTemplateConversion? _prepared;
    private bool _loading, _subscribed, _closing, _disposed;
    private long _generation;
    private SynchronizationContext? _ui;
    private Task? _initialization;
    private Task _operation = Task.CompletedTask;
    private Task _usageRead = Task.CompletedTask;
    private long _usageGeneration;
    private CloseRegistration? _registration;
    public Func<Guid, Task>? OpenDraftAsync { get; set; }
    public Func<Guid, Task>? DraftCreatedAsync { get; set; }
    [ObservableProperty] private string _name = "小说创作模板";
    [ObservableProperty] private string _model = "";
    [ObservableProperty] private string _reasoning = "low";
    [ObservableProperty] private int _maximumOutput = 8192;
    [ObservableProperty] private long _contextTokens = 131072;
    [ObservableProperty] private int _maximumRequests = 8;
    [ObservableProperty] private long _maximumTokens = 500000;
    [ObservableProperty] private bool _useWorld = true;
    [ObservableProperty] private bool _useStyle = true;
    [ObservableProperty] private bool _useMethods = true;
    [ObservableProperty] private bool _useRules;
    [ObservableProperty] private bool _acknowledgeCosts;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasMore;
    [ObservableProperty] private ModelConnection? _selectedConnection;
    [ObservableProperty] private TemplateConversionChoice? _selectedConversion;
    [ObservableProperty] private TemplateSourceChoice? _selectedSource;
    [ObservableProperty] private string _reportStatus = "先读取一份完整报告。";
    [ObservableProperty] private string _planSummary = "可先预览输入规模与请求计划；生成会先保存任务，再自动保存新模板草案。";
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private string _previewText = "";
    [ObservableProperty] private string _sourceText = "";
    [ObservableProperty] private string _usageSummary = "转换费用与全文分析分开记录。";
    [ObservableProperty] private string _failureCandidate = "";
    public ObservableCollection<ModelConnection> Connections { get; } = [];
    public ObservableCollection<TemplateConversionChoice> History { get; } = [];
    public ObservableCollection<TemplateSourceChoice> Sources { get; } = [];
    public IReadOnlyList<string> ReasoningOptions { get; } = ["none", "low", "medium", "high", "max"];
    public ProfileDimensions Dimensions => (UseWorld ? ProfileDimensions.World : 0) | (UseStyle ? ProfileDimensions.Style : 0) |
        (UseMethods ? ProfileDimensions.Methods : 0) | (UseRules ? ProfileDimensions.Rules : 0);
    public bool CanEdit => !IsBusy && !_closing && !_disposed;
    public bool CanGenerate => CanEdit && _report?.IsComplete == true && SelectedConnection is not null && Dimensions != ProfileDimensions.None;
    public bool CanContinue => CanEdit && SelectedConversion is { } choice && !activity.IsActive(choice.Value.Id) && choice.Value.State != TemplateConversionState.DraftSaved;
    public bool CanCancel => SelectedConversion is { } choice && activity.IsActive(choice.Value.Id);
    public bool CanOpen => CanEdit && SelectedConversion?.Value.State == TemplateConversionState.DraftSaved && OpenDraftAsync is not null;
    public Task InitializeAsync() => _initialization ??= InitializeCoreAsync();
    private async Task InitializeCoreAsync()
    {
        _ui = SynchronizationContext.Current; _registration ??= shutdown.Register(CloseCoreAsync, _ui);
        if (!_subscribed) { activity.Changed += OnActivity; _subscribed = true; }
        await RefreshConnectionsCoreAsync();
    }
    private async Task RefreshConnectionsCoreAsync()
    {
        var catalog = await connections.ListAsync(); if (_disposed) return;
        var selected = SelectedConnection?.Id; Connections.Clear(); foreach (var value in catalog.Connections) Connections.Add(value);
        SelectedConnection = Connections.FirstOrDefault(c => c.Id == selected) ?? Connections.FirstOrDefault(c => c.Id == catalog.DefaultConnectionId) ?? Connections.FirstOrDefault();
    }
    public void SetReport(NovelAnalysisReport? report)
    {
        _report = report; _generation++; _prepared = null;
        _loading = true;
        try
        {
            History.Clear(); SelectedConversion = null; Sources.Clear(); SelectedSource = null;
            PreviewText = SourceText = FailureCandidate = ""; HasMore = false;
            if (report is not null) Name = report.Name[..Math.Min(report.Name.Length, 112)] + " · 创作模板";
            ReportStatus = report is null ? "先读取一份完整报告。" : report.IsComplete ?
                "报告已保存到本机，可以直接提炼模板；不会重新分析整本 TXT。" :
                $"当前是部分报告（专题 {report.Parts.Length}/6，综合结论{(report.Synthesis is null ? "未完成" : "已保存")}），完成后可生成模板。";
        }
        finally { _loading = false; }
        Notify();
    }
    public async Task LoadHistoryAsync()
    {
        var id = _report?.RunId; var generation = _generation; if (id is null) return;
        var values = await Task.Run(() => service.List(id.Value));
        if (_disposed || _report?.RunId != id || generation != _generation) return;
        var selected = SelectedConversion?.Value.Id;
        History.Clear(); foreach (var value in values) History.Add(new(value));
        HasMore = values.Count == 20; SelectedConversion = History.FirstOrDefault(c => c.Value.Id == selected) ?? History.FirstOrDefault(); Notify();
        await _usageRead;
    }
    private void Invalidate() { if (_loading) return; _prepared = null; _generation++; PlanSummary = "参数已变化；生成前会重新核对输入和预算。"; Notify(); }
    partial void OnNameChanged(string value) => Invalidate();
    partial void OnModelChanged(string value) => Invalidate();
    partial void OnReasoningChanged(string value) => Invalidate();
    partial void OnMaximumOutputChanged(int value) => Invalidate();
    partial void OnContextTokensChanged(long value) => Invalidate();
    partial void OnMaximumRequestsChanged(int value) => Invalidate();
    partial void OnMaximumTokensChanged(long value) => Invalidate();
    partial void OnUseWorldChanged(bool value) => Invalidate();
    partial void OnUseStyleChanged(bool value) => Invalidate();
    partial void OnUseMethodsChanged(bool value) => Invalidate();
    partial void OnUseRulesChanged(bool value) => Invalidate();
    partial void OnIsBusyChanged(bool value) => Notify();
    partial void OnSelectedConnectionChanged(ModelConnection? value)
    {
        if (value is not null && !_loading) { Model = value.Settings.Checking.Model; Reasoning = value.Settings.Checking.ReasoningEffort; MaximumOutput = Math.Min(value.Settings.Checking.MaxOutputTokens, 8192); }
        Invalidate();
    }
    partial void OnSelectedConversionChanged(TemplateConversionChoice? value)
    {
        if (_loading || value is null) { Notify(); return; }
        var run = value.Value;
        _loading = true;
        try
        {
            MaximumRequests = run.Budget.MaximumRequests; MaximumTokens = run.Budget.MaximumTokens; AcknowledgeCosts = false;
        }
        finally { _loading = false; }
        Show(run); FailureCandidate = ""; UsageSummary = "正在读取此转换的独立费用账本。";
        _usageRead = ReadUsageAsync(run); Notify();
    }
    private async Task ReadUsageAsync(ReportTemplateConversion run)
    {
        var generation = ++_usageGeneration;
        try
        {
            var usage = await Task.Run(() => service.Usage(run.Id));
            if (!_disposed && SelectedConversion?.Value.Id == run.Id && generation == _usageGeneration) ShowUsage(run, usage);
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { if (!_disposed && SelectedConversion?.Value.Id == run.Id) UsageSummary = "费用账本读取失败，请刷新后再复核。"; }
    }
    private void ShowUsage(ReportTemplateConversion run, IReadOnlyList<ModelRequestEntry> usage)
    {
        _usageGeneration++;
        var known = usage.Where(e => e.Usage.InputTokens is not null && e.Usage.OutputTokens is not null).ToArray();
        UsageSummary = $"转换请求 {usage.Count}/{run.Budget.MaximumRequests}；已知 {known.Sum(e => e.ChargedTokens)} token，未知 {usage.Count - known.Length} 次。\n" +
            $"已计/预留 {usage.Sum(e => e.ChargedTokens)}/{run.Budget.MaximumTokens} token，金额未知。";
        var failed = usage.LastOrDefault(e => e.State != RequestState.Completed || e.Usage.InputTokens is null || e.Usage.OutputTokens is null);
        FailureCandidate = failed is null ? "" : (failed.Diagnostic?.Message ?? "请求或费用尚未确认。") + "\n原始候选（最多显示 20000 字符）：\n" + failed.PartialText[..Math.Min(20000, failed.PartialText.Length)];
    }
    partial void OnSelectedSourceChanged(TemplateSourceChoice? value)
    {
        SourceText = value is null ? "" : $"{value.Value.Topic} · {ReportTemplateDeliveryService.Basis(value.Value.Basis)}\n{value.Value.Text}\n\n" +
            string.Join("\n", value.Value.Evidence.Select(e => $"原报告 F{e.FactId}：{e.Quote}"));
    }
    private async Task<ReportTemplateConversion> PrepareCoreAsync()
    {
        var id = _report?.RunId ?? throw new InvalidOperationException("请先读取报告。"); var generation = _generation;
        var run = await service.PrepareAsync(id, ConnectionService.Bind(SelectedConnection!), Name, Dimensions, MaximumRequests, MaximumTokens,
            new ModelPreset(Model, MaximumOutput, Reasoning), ContextTokens);
        if (generation != _generation || _report?.RunId != id) throw new InvalidOperationException("准备期间报告或参数已改变，请重新生成。");
        _prepared = run;
        var first = ReportTemplateRequests.Prepare(run, run.Steps[0]); var estimate = ModelInputCapacity.Estimate(first);
        PlanSummary = $"固定报告 {run.Source.ReportVersion[..12]}；{run.Source.Claims.Length} 条结论/疑问，计划 {run.Steps.Length} 次请求（含归纳，纠正另计）。\n" +
            $"首次保守输入 {estimate.EstimatedInputTokens}，输出预留 {estimate.MaximumOutputTokens}，单次容量 {ContextTokens} token；总预算 {MaximumRequests} 次/{MaximumTokens} token。\n" +
            string.Join("\n", run.Source.Notes);
        return run;
    }
    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private Task Prepare() => RunAsync(async () => { await PrepareCoreAsync(); Status = "转换计划已准备，尚未发送模型请求。"; });
    [RelayCommand(CanExecute = nameof(CanGenerate))]
    private Task Generate() => RunAsync(async () =>
    {
        var run = _prepared ?? await PrepareCoreAsync(); var reportId = run.Source.RunId;
        await service.CreateAsync(run); _prepared = null;
        if (_report?.RunId == reportId) { History.Insert(0, new(run)); SelectedConversion = History[0]; }
        var completed = await activity.StartAsync(run.Id);
        if (_report?.RunId == reportId) Show(completed);
    });
    [RelayCommand(CanExecute = nameof(CanContinue))]
    private Task Continue() => RunAsync(async () =>
    {
        var id = SelectedConversion!.Value.Id;
        var result = await activity.StartAsync(id, AcknowledgeCosts, MaximumRequests, MaximumTokens);
        if (SelectedConversion?.Value.Id == id) Show(result);
    });
    [RelayCommand(CanExecute = nameof(CanCancel))] private void Cancel() { activity.Cancel(SelectedConversion!.Value.Id); Status = "已请求取消；成功步骤仍会保留。"; }
    [RelayCommand(CanExecute = nameof(CanOpen))]
    private Task OpenDraft() => RunAsync(() => OpenDraftAsync!(SelectedConversion!.Value.TemplateId));
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task Refresh() => RunAsync(async () => { await RefreshConnectionsCoreAsync(); await LoadHistoryAsync(); });
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task More() => RunAsync(async () =>
    {
        if (_report is null) return; var id = _report.RunId;
        var values = await Task.Run(() => service.List(id, History.Count)); if (_report?.RunId != id) return;
        foreach (var value in values) if (!History.Any(c => c.Value.Id == value.Id)) History.Add(new(value)); HasMore = values.Count == 20;
    });
    private void Show(ReportTemplateConversion run)
    {
        PreviewText = ReportTemplateDeliveryService.Preview(run);
        Status = $"{StateName(run.State)} · 已保存 {run.Steps.Count(s => s.State == TemplateConversionStepState.Completed)}/{run.Steps.Length} 步\n" +
            $"实际配置：{run.Preset.Model}/{run.Preset.ReasoningEffort}，输出 {run.Preset.MaxOutputTokens}；更新 {run.UpdatedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}\n{run.Message}";
        var selected = SelectedSource?.Value.Id; Sources.Clear(); foreach (var source in run.Source.Claims) Sources.Add(new(source));
        SelectedSource = Sources.FirstOrDefault(s => s.Value.Id == selected) ?? Sources.FirstOrDefault(); Notify();
    }
    private void OnActivity(TemplateConversionUpdate update)
    {
        void Apply()
        {
            if (_disposed) return;
            var run = update.Conversion;
            if (_report?.RunId == run.Source.RunId)
            {
                var selected = SelectedConversion?.Value.Id;
                var index = Enumerable.Range(0, History.Count).FirstOrDefault(i => History[i].Value.Id == run.Id, -1);
                _loading = true;
                try { if (index >= 0) History[index] = new(run); if (selected == run.Id) SelectedConversion = index >= 0 ? History[index] : new(run); }
                finally { _loading = false; }
                if (selected == run.Id)
                {
                    Show(run); if (update.Error is not null) Status = update.Error;
                    ShowUsage(run, update.Usage);
                }
            }
            if (!update.Active && run.State == TemplateConversionState.DraftSaved && DraftCreatedAsync is not null) _ = NotifyCreatedAsync(run.TemplateId);
            Notify();
        }
        if (_ui is null || ReferenceEquals(_ui, SynchronizationContext.Current)) Apply(); else _ui.Post(_ => Apply(), null);
    }
    private async Task NotifyCreatedAsync(Guid id)
    { try { await DraftCreatedAsync!(id); } catch (Exception error) when (error is not OutOfMemoryException) { Status = "草案已保存，模板列表刷新失败，可稍后打开：" + error.Message; } }
    private void Notify()
    {
        foreach (var property in new[] { nameof(CanEdit), nameof(CanGenerate), nameof(CanContinue), nameof(CanCancel), nameof(CanOpen) }) OnPropertyChanged(property);
        PrepareCommand.NotifyCanExecuteChanged(); GenerateCommand.NotifyCanExecuteChanged(); ContinueCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged();
        OpenDraftCommand.NotifyCanExecuteChanged(); RefreshCommand.NotifyCanExecuteChanged(); MoreCommand.NotifyCanExecuteChanged();
    }
    private Task RunAsync(Func<Task> action) => !CanEdit ? Task.CompletedTask : _operation = RunCoreAsync(action);
    private async Task RunCoreAsync(Func<Task> action)
    {
        IsBusy = true;
        try { await InitializeAsync(); await action(); }
        catch (Exception error) when (error is not OutOfMemoryException) { Status = error.Message; }
        finally { IsBusy = false; Notify(); }
    }
    public async Task<bool> SaveBeforeCloseAsync()
    {
        _closing = true; Notify();
        try { await activity.DrainAsync(); await _operation; await _usageRead; return true; }
        catch (Exception error) when (error is not OutOfMemoryException) { Status = "转换关闭收尾失败：" + error.Message; return false; }
        finally { _closing = false; Notify(); }
    }
    private async Task CloseCoreAsync()
    {
        if (_disposed) return; if (!await SaveBeforeCloseAsync()) throw new IOException(Status);
        _disposed = true; if (_subscribed) activity.Changed -= OnActivity; OpenDraftAsync = DraftCreatedAsync = null; Notify();
    }
    public ValueTask DisposeAsync() => _disposed ? ValueTask.CompletedTask : new(_registration?.CloseAsync() ?? CloseCoreAsync());
    public void Dispose() { if (!_disposed) _ = _registration?.CloseAsync() ?? CloseCoreAsync(); }
    public static string StateName(TemplateConversionState state) => state switch
    {
        TemplateConversionState.Queued => "任务已保存",
        TemplateConversionState.Running => "生成中",
        TemplateConversionState.NeedsAttention => "待处理/恢复",
        TemplateConversionState.CandidateSaved => "规范候选已保存",
        TemplateConversionState.DraftSaved => "模板草案已保存",
        _ => "已取消"
    };
}
