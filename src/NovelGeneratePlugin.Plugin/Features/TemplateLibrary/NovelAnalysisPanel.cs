using System.Collections.ObjectModel;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using MyAvaloniaManagement.PluginSdk.UI;
using NovelGeneratePlugin.Application.Analysis;
using NovelGeneratePlugin.Application.Connections;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Application.Projects;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Domain.Analysis;

namespace NovelGeneratePlugin.Features.TemplateLibrary;

public sealed record AnalysisBookChoice(ReferenceBook Book) { public override string ToString() => $"{Book.Name} · {Book.Characters} 字符"; }
public sealed record AnalysisRunChoice(AnalysisRun Run) { public override string ToString() => $"{Run.UpdatedAt.ToLocalTime():MM-dd HH:mm} · {NovelAnalysisPanel.StateName(Run.State)} · {Run.Id.ToString()[..8]}"; }
public sealed record AnalysisSectionChoice(ReferenceSection Section) { public override string ToString() => $"{Section.Number}. {Section.Title} · {Section.Range.Length} 字符"; }

/// <summary>
/// 面板只协调用户命令和展示状态，长任务生命周期由 NovelAnalysisActivity 持有。选择书目/运行不会取消另一任务。
/// 短操作使用独立取消令牌并可等待关闭；模型事件回到初始化时的 UI 上下文，界面不拥有线程或数据库连接。
/// </summary>
public sealed partial class NovelAnalysisPanel(NovelImportService importer, IReferenceSourceStore sources, NovelAnalysisRunService runner,
    NovelAnalysisActivity activity, NovelAnalysisCandidateService candidates, ConnectionService connections, NovelReportReader reader,
    IPluginWindowInteraction interaction, PluginCloseCoordinator shutdown) : ObservableObject, IAsyncDisposable, IDisposable
{
    private NovelImportPreview? _preview;
    private SynchronizationContext? _ui;
    private bool _subscribed, _disposed, _closing, _loading;
    private Task? _initialization;
    private Task _operation = Task.CompletedTask;
    private CancellationTokenSource? _cancellation;
    private CloseRegistration? _registration;
    public NovelReportReader Report => reader;
    [ObservableProperty] private string _filePath = "";
    [ObservableProperty] private string _encodingName = "自动";
    [ObservableProperty] private int _chunkCharacters = 6000;
    [ObservableProperty] private int _maximumRequests = 200;
    [ObservableProperty] private long _maximumTokens = 8000000;
    [ObservableProperty] private bool _acknowledgeCosts;
    [ObservableProperty] private bool _useStageSettings = true;
    [ObservableProperty] private long _contextTokens;
    [ObservableProperty] private int _maximumSplitDepth = 3;
    [ObservableProperty] private string _capacitySummary = "单次容量预检使用保守估算；实际 token 以请求用量为准。";
    public ObservableCollection<AnalysisStageEditor> StageParameters { get; } = [];
    [ObservableProperty] private string _reviewGuidance = "";
    [ObservableProperty] private string _previewSummary = "选择 TXT，预览编码、分章和全文范围后导入。";
    [ObservableProperty] private string _previewText = "";
    [ObservableProperty] private string _progressSummary = "尚未选择分析运行。";
    [ObservableProperty] private string _usageSummary = "所有阶段和失败请求共用总预算；货币费用尚未估算。";
    [ObservableProperty] private string _candidateText = "";
    [ObservableProperty] private string _status = "无需新建创作项目，即可独立分析 TXT 小说。";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _hasMoreBooks;
    [ObservableProperty] private bool _hasMoreRuns;
    [ObservableProperty] private AnalysisBookChoice? _selectedBook;
    [ObservableProperty] private AnalysisRunChoice? _selectedRun;
    [ObservableProperty] private AnalysisSectionChoice? _selectedSection;
    [ObservableProperty] private ModelConnection? _selectedConnection;
    public ObservableCollection<AnalysisBookChoice> Books { get; } = [];
    public ObservableCollection<AnalysisRunChoice> Runs { get; } = [];
    public ObservableCollection<AnalysisSectionChoice> Sections { get; } = [];
    public ObservableCollection<ModelConnection> Connections { get; } = [];
    public ObservableCollection<string> Nodes { get; } = [];
    public IReadOnlyList<string> Encodings { get; } = ["自动", "utf-8", "utf-16le", "utf-16be", "utf-32le", "utf-32be", "gb18030"];
    public bool CanEdit => !IsBusy && !_disposed && !_closing;
    public bool CanImport => CanEdit && _preview is not null;
    public bool CanStart => CanEdit && SelectedBook is not null && SelectedConnection is not null;
    public bool CanUseRun => CanEdit && SelectedRun is not null;
    public bool CanChangeRun => CanUseRun && !activity.IsActive(SelectedRun!.Run.Id);
    public bool CanResume => CanChangeRun && SelectedRun!.Run.State is not (AnalysisRunState.Completed or AnalysisRunState.ExtractionCompleted or AnalysisRunState.IntegrationCompleted);
    public bool CanStop => SelectedRun is not null && activity.IsActive(SelectedRun.Run.Id);
    public Task InitializeAsync() => _initialization ??= RunAsync(async ct =>
    {
        _ui = SynchronizationContext.Current;
        if (!_subscribed) { activity.Changed += OnActivityChanged; _subscribed = true; }
        await RefreshCoreAsync(ct);
    });
    partial void OnFilePathChanged(string value) => InvalidatePreview();
    partial void OnEncodingNameChanged(string value) => InvalidatePreview();
    partial void OnChunkCharactersChanged(int value) => InvalidatePreview();
    private void InvalidatePreview() { _preview = null; Sections.Clear(); SelectedSection = null; PreviewText = ""; PreviewSummary = "输入已变化，请重新预览。"; Notify(); }
    partial void OnIsBusyChanged(bool value) => Notify();
    partial void OnSelectedConnectionChanged(ModelConnection? value)
    {
        if (value is not null && !_loading)
            LoadStageParameters(AnalysisStageSettings.Default(new(Guid.NewGuid(), value, ModelTask.Checking, value.Settings.Checking)));
        Notify();
    }
    private void LoadStageParameters(AnalysisStageSettings settings)
    {
        StageParameters.Clear();
        foreach (var kind in Enum.GetValues<AnalysisNodeKind>()) StageParameters.Add(new(kind, settings.For(kind)));
    }
    private AnalysisStageSettings? BuildStageSettings()
    {
        if (!UseStageSettings) return null;
        if (StageParameters.Count == 0 && SelectedConnection is not null)
            LoadStageParameters(AnalysisStageSettings.Default(new(Guid.NewGuid(), SelectedConnection, ModelTask.Checking, SelectedConnection.Settings.Checking)));
        ModelPreset Read(AnalysisNodeKind kind) => StageParameters.Single(p => p.Kind == kind).Build();
        return new(Read(AnalysisNodeKind.Extraction), Read(AnalysisNodeKind.Integration), Read(AnalysisNodeKind.Summary), Read(AnalysisNodeKind.Dimension), Read(AnalysisNodeKind.Synthesis));
    }
    partial void OnSelectedBookChanged(AnalysisBookChoice? value)
    { if (_loading) return; Runs.Clear(); HasMoreRuns = false; SelectedRun = null; reader.Clear(); Notify(); }
    partial void OnSelectedRunChanged(AnalysisRunChoice? value)
    {
        if (_loading) return; Nodes.Clear(); CandidateText = ""; AcknowledgeCosts = false; ReviewGuidance = ""; reader.Clear();
        ProgressSummary = value is null ? "尚未选择分析运行。" : "点击读取进度，或读取已保存报告。"; Notify();
    }
    private void Notify()
    {
        foreach (var name in new[] { nameof(CanEdit), nameof(CanImport), nameof(CanStart), nameof(CanUseRun), nameof(CanChangeRun), nameof(CanResume), nameof(CanStop) }) OnPropertyChanged(name);
        ChooseFileCommand.NotifyCanExecuteChanged(); PreviewCommand.NotifyCanExecuteChanged(); ImportCommand.NotifyCanExecuteChanged();
        RefreshCommand.NotifyCanExecuteChanged(); MoreBooksCommand.NotifyCanExecuteChanged(); LoadBookCommand.NotifyCanExecuteChanged();
        MoreRunsCommand.NotifyCanExecuteChanged();
        StartCommand.NotifyCanExecuteChanged(); ReadRunCommand.NotifyCanExecuteChanged(); ResumeCommand.NotifyCanExecuteChanged(); ReviseCommand.NotifyCanExecuteChanged();
        PauseCommand.NotifyCanExecuteChanged(); CancelCommand.NotifyCanExecuteChanged(); ReviewCandidateCommand.NotifyCanExecuteChanged(); AdoptCandidateCommand.NotifyCanExecuteChanged(); ReadReportCommand.NotifyCanExecuteChanged();
    }
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task ChooseFile() => RunAsync(async ct =>
    {
        var paths = await interaction.PickOpenFilesAsync(new FilePickerOpenOptions { Title = "选择小说 TXT", AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("TXT 小说") { Patterns = ["*.txt"] }] }, ct);
        if (paths.Count > 0) FilePath = paths[0];
    });
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task Preview() => RunAsync(async ct =>
    {
        var path = FilePath; var encoding = EncodingName; var size = ChunkCharacters;
        var preview = await importer.PreviewAsync(path, encoding == "自动" ? null : encoding, new(size), ct);
        if (path != FilePath || encoding != EncodingName || size != ChunkCharacters) throw new InvalidOperationException("预览期间输入改变，请重新预览。");
        _preview = preview; Sections.Clear(); foreach (var section in preview.Import.Sections) Sections.Add(new(section)); SelectedSection = Sections.FirstOrDefault();
        PreviewSummary = $"编码 {preview.Import.Source.EncodingName} · {preview.Import.Source.Bytes.Length} 字节 · {preview.Import.Source.Text.Length} UTF-16 字符\n{Sections.Count} 个章段，{preview.Import.Chunks.Length} 个全文提取单元；无排除、无截断。\n首轮至少 {preview.Import.Chunks.Length} 次请求，另为整合与报告预留 32 次/1200000 token；实际计划随内容密度增长。\n" + string.Join("\n", preview.Warnings);
        ShowSection(); Status = "预览已就绪。导入将保存这份完整来源快照。";
    });
    partial void OnSelectedSectionChanged(AnalysisSectionChoice? value) => ShowSection();
    private void ShowSection()
    {
        if (_preview is null || SelectedSection is null) return;
        var range = SelectedSection.Section.Range; var length = Math.Min(range.Length, 4000);
        if (range.Start + length < _preview.Import.Source.Text.Length && char.IsHighSurrogate(_preview.Import.Source.Text[range.Start + length - 1])) length--;
        PreviewText = _preview.Import.Source.Text.Substring(range.Start, length) + (length < range.Length ? "\n（本章预览到此，分析仍覆盖完整章段。）" : "");
    }
    [RelayCommand(CanExecute = nameof(CanImport))]
    private Task Import() => RunAsync(async ct =>
    {
        var book = await importer.ImportAsync(_preview!, ct); await RefreshCoreAsync(ct);
        if (!Books.Any(b => b.Book.Id == book.Id)) Books.Insert(0, new(book)); SelectedBook = Books.Single(b => b.Book.Id == book.Id);
        _preview = null; Sections.Clear(); PreviewText = ""; Status = "全文来源已保存。选择连接和预算后可开始分析。";
    });
    [RelayCommand(CanExecute = nameof(CanEdit))] private Task Refresh() => RunAsync(RefreshCoreAsync);
    private async Task RefreshCoreAsync(CancellationToken ct)
    {
        var selected = SelectedBook?.Book.Id; var books = await Task.Run(() => sources.List(), ct); var catalog = await connections.ListAsync();
        _loading = true;
        try
        {
            Books.Clear(); foreach (var book in books) Books.Add(new(book)); HasMoreBooks = books.Count == 50;
            SelectedBook = Books.FirstOrDefault(b => b.Book.Id == selected) ?? Books.FirstOrDefault();
            var connectionId = SelectedConnection?.Id; Connections.Clear(); foreach (var connection in catalog.Connections) Connections.Add(connection);
            SelectedConnection = Connections.FirstOrDefault(c => c.Id == connectionId) ?? Connections.FirstOrDefault();
        }
        finally { _loading = false; }
        await LoadBookCoreAsync(ct);
    }
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task MoreBooks() => RunAsync(async ct => { var books = await Task.Run(() => sources.List(Books.Count, 50), ct); foreach (var book in books) if (!Books.Any(b => b.Book.Id == book.Id)) Books.Add(new(book)); HasMoreBooks = books.Count == 50; });
    [RelayCommand(CanExecute = nameof(CanEdit))] private Task LoadBook() => RunAsync(LoadBookCoreAsync);
    private async Task LoadBookCoreAsync(CancellationToken ct)
    {
        var id = SelectedBook?.Book.Id; if (id is null) return; var runs = await Task.Run(() => runner.List(id.Value), ct);
        if (SelectedBook?.Book.Id != id) return;
        var selected = SelectedRun?.Run.Id; Runs.Clear(); foreach (var run in runs) Runs.Add(new(run)); HasMoreRuns = runs.Count == 100;
        SelectedRun = Runs.FirstOrDefault(r => r.Run.Id == selected) ?? Runs.FirstOrDefault();
        if (SelectedRun is not null) await ReadRunCoreAsync(SelectedRun.Run.Id, ct);
    }
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task MoreRuns() => RunAsync(async ct =>
    {
        if (SelectedBook is null) return; var id = SelectedBook.Book.Id;
        var values = await Task.Run(() => runner.List(id, Runs.Count), ct); if (SelectedBook?.Book.Id != id) return;
        foreach (var value in values) if (!Runs.Any(r => r.Run.Id == value.Id)) Runs.Add(new(value)); HasMoreRuns = values.Count == 100;
    });
    [RelayCommand(CanExecute = nameof(CanStart))]
    private Task Start() => CreateRunAsync(false);
    [RelayCommand(CanExecute = nameof(CanChangeRun))]
    private Task Revise() => CreateRunAsync(true);
    private Task CreateRunAsync(bool revision) => RunAsync(async ct =>
    {
        if (SelectedBook is null || SelectedConnection is null) throw new InvalidOperationException("请选择参考小说和可用模型连接。");
        var old = revision ? SelectedRun!.Run : null;
        if (old is not null && runner.Usage(old.Id).Any(e => !e.RetryAcknowledged && (e.State != RequestState.Completed || e.Usage.InputTokens is null || e.Usage.OutputTokens is null)))
            await Task.Run(() => runner.Resume(old.Id, AcknowledgeCosts, MaximumRequests, MaximumTokens, ReviewGuidance), ct);
        var run = await runner.CreateAsync(SelectedBook.Book.Id, ConnectionService.Bind(SelectedConnection), MaximumRequests, MaximumTokens,
            old?.ReportReserve ?? new(32, 1200000), ct, revision ? new(ChunkCharacters) : null, old?.Id, AnalysisTarget.Report, BuildStageSettings(),
            new(ContextTokens == 0 ? null : ContextTokens, MaximumSplitDepth));
        Runs.Insert(0, new(run)); SelectedRun = Runs[0]; Begin(run.Id); Status = "全文分析已开始，隐藏面板不影响任务。";
    });
    private void Begin(Guid id) { _ = ObserveAsync(activity.StartAsync(id), id); Notify(); }
    private async Task ObserveAsync(Task<AnalysisRun> task, Guid id)
    {
        try { await task; }
        catch (Exception error) when (error is not OutOfMemoryException) { if (!_disposed && SelectedRun?.Run.Id == id) Status = error.Message; }
        finally { if (!_disposed) Notify(); }
    }
    [RelayCommand(CanExecute = nameof(CanResume))]
    private Task Resume() => RunAsync(async ct =>
    {
        var id = SelectedRun!.Run.Id; var current = await Task.Run(() => runner.Read(id), ct);
        // 崩溃可能留下 Running。先让调度器核对已保存的操作账本，不能直接改操作 ID 重发未知请求。
        if (current.State != AnalysisRunState.Running) await Task.Run(() => runner.Resume(id, AcknowledgeCosts, MaximumRequests, MaximumTokens, ReviewGuidance), ct);
        AcknowledgeCosts = false; Begin(id);
    });
    [RelayCommand(CanExecute = nameof(CanStop))] private void Pause() { activity.Pause(SelectedRun!.Run.Id); Status = "已请求暂停；当前完整结果保存后停止。"; }
    [RelayCommand(CanExecute = nameof(CanStop))] private void Cancel() { activity.Cancel(SelectedRun!.Run.Id); Status = "正在取消；已发送请求可能产生费用，账本和已完成结果会保留。"; }
    [RelayCommand(CanExecute = nameof(CanUseRun))] private Task ReadRun() => RunAsync(ct => ReadRunCoreAsync(SelectedRun!.Run.Id, ct));
    private async Task ReadRunCoreAsync(Guid id, CancellationToken ct)
    {
        var snapshot = await Task.Run(() => new AnalysisActivityUpdate(runner.Read(id), runner.Usage(id), activity.IsActive(id)), ct);
        if (SelectedRun?.Run.Id == id)
        {
            Apply(snapshot); MaximumRequests = snapshot.Run.Budget.MaximumRequests; MaximumTokens = snapshot.Run.Budget.MaximumTokens;
            UseStageSettings = snapshot.Run.StageSettings is not null;
            ContextTokens = snapshot.Run.Capacity?.ContextTokens ?? 0; MaximumSplitDepth = snapshot.Run.Capacity?.MaximumSplitDepth ?? 3;
            LoadStageParameters(snapshot.Run.StageSettings ?? AnalysisStageSettings.Default(snapshot.Run.Connection));
        }
    }
    private void OnActivityChanged(AnalysisActivityUpdate update)
    {
        void ApplyIfCurrent() { if (!_disposed) Apply(update); }
        if (_ui is null || ReferenceEquals(_ui, SynchronizationContext.Current)) ApplyIfCurrent(); else _ui.Post(_ => ApplyIfCurrent(), null);
    }
    private void Apply(AnalysisActivityUpdate update)
    {
        var run = update.Run; var selectedId = SelectedRun?.Run.Id; var index = Enumerable.Range(0, Runs.Count).FirstOrDefault(i => Runs[i].Run.Id == run.Id, -1);
        _loading = true;
        try
        {
            if (index >= 0) Runs[index] = new(run);
            // ComboBox 在集合 Replace 时可能同步回写空选择；用更新前的稳定身份恢复选中项。
            if (selectedId != run.Id) return;
            SelectedRun = index >= 0 ? Runs[index] : new(run);
        }
        finally { _loading = false; }
        var completed = run.Nodes.Count(n => n.State == AnalysisNodeState.Completed);
        var characters = run.Chunks.Where(c => run.Nodes.Any(n => n.ChunkId == c.Id && n.State == AnalysisNodeState.Completed)).Sum(c => c.Body.Length);
        ProgressSummary = $"已保存到本机 · {run.UpdatedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}\n{StateName(run.State)} · 已完成 {completed}/{run.Nodes.Length} 节点\n正文覆盖 {characters}/{run.Chunks.Sum(c => c.Body.Length)} UTF-16 字符；{run.Message}";
        var known = update.Usage.Where(e => e.Usage.InputTokens is not null && e.Usage.OutputTokens is not null).ToArray();
        var unknown = update.Usage.Except(known).ToArray();
        var estimate = update.Usage.LastOrDefault(e => e.InputEstimate is not null)?.InputEstimate;
        CapacitySummary = $"已局部拆分 {run.Splits.Length} 次；最多 {run.Capacity?.MaximumSplitDepth ?? 3} 层。\n" +
            (estimate is null ? "尚无单次请求估算记录。" :
            $"最近单次保守输入估算 {estimate.EstimatedInputTokens}，输出预留 {estimate.MaximumOutputTokens}，上下文容量 {(estimate.ContextTokens?.ToString() ?? "未配置")} token；估算不是实测用量。");
        UsageSummary = $"全部累计请求 {update.Usage.Count}/{run.Budget.MaximumRequests}，已知用量 {known.Sum(e => e.ChargedTokens)} token；未知 {unknown.Length} 次，保守预留 {unknown.Sum(e => e.ReservedTokens)} token。\n已知加预留 {update.Usage.Sum(e => e.ChargedTokens)}/{run.Budget.MaximumTokens} token；金额未知。";
        Nodes.Clear(); foreach (var node in run.Nodes)
        {
            var failed = update.Usage.Any(e => e.Id == node.OperationId && e.State is RequestState.Uncertain or RequestState.Truncated or RequestState.Rejected);
            var preset = node.ExecutionPreset ?? (node.ExecutionConnection ?? run.Connection).Preset;
            Nodes.Add($"{NodeName(node.Kind)} · {node.Key} · {(node.State == AnalysisNodeState.Completed ? "已保存" : failed ? "失败/待复核" : node.State == AnalysisNodeState.Running ? "正在处理/待核对" : "未处理")} · {preset.Model}/{preset.ReasoningEffort}/{preset.MaxOutputTokens}");
        }
        Status = update.Error ?? (update.Active ? "分析仍在运行，可以切换书目或隐藏工具。" : run.Message); Notify();
    }
    [RelayCommand(CanExecute = nameof(CanChangeRun))]
    private Task ReviewCandidate() => RunAsync(async ct =>
    {
        var review = await Task.Run(() => candidates.Read(SelectedRun!.Run.Id), ct);
        CandidateText = $"节点 {review.Node}\n失败类别：{review.Failure}\n本地复核：{review.Validation}\n已计/预留 {review.Entry.ChargedTokens} token；原始候选（最多显示前 20000 字符）：\n" + review.Entry.PartialText[..Math.Min(20000, review.Entry.PartialText.Length)];
    });
    [RelayCommand(CanExecute = nameof(CanChangeRun))]
    private Task AdoptCandidate() => RunAsync(async ct =>
    {
        if (!AcknowledgeCosts) throw new InvalidOperationException("请先查看候选并勾选费用复核，再明确采纳。");
        var id = SelectedRun!.Run.Id; await Task.Run(() => runner.AdoptReviewedCandidate(id), ct); AcknowledgeCosts = false;
        await ReadRunCoreAsync(id, ct); Status = "当前契约通过的原候选已保存；尚未发起新请求，可继续运行。";
    });
    [RelayCommand(CanExecute = nameof(CanUseRun))] private Task ReadReport() => RunAsync(_ => reader.LoadAsync(SelectedRun!.Run.Id));
    private Task RunAsync(Func<CancellationToken, Task> operation)
    { if (!CanEdit) return Task.CompletedTask; _registration ??= shutdown.Register(CloseCoreAsync, SynchronizationContext.Current); return _operation = RunCoreAsync(operation); }
    private async Task RunCoreAsync(Func<CancellationToken, Task> operation)
    {
        IsBusy = true; using var cancellation = new CancellationTokenSource(); _cancellation = cancellation;
        try { await operation(cancellation.Token); }
        catch (OperationCanceledException) { Status = "操作已取消，已保存结果保留。"; }
        catch (Exception error) when (error is not OutOfMemoryException) { Status = error.Message; }
        finally { _cancellation = null; IsBusy = false; }
    }
    public async Task<bool> SaveBeforeCloseAsync()
    {
        _closing = true; _cancellation?.Cancel(); Notify();
        try
        {
            await _operation;
            await Task.WhenAll(reader.ExportCommand.ExecutionTask ?? Task.CompletedTask, reader.LocateCommand.ExecutionTask ?? Task.CompletedTask);
            await activity.DrainAsync();
            if (reader.Conversion is not null && !await reader.Conversion.SaveBeforeCloseAsync()) { Status = reader.Conversion.Status; return false; }
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException) { Status = "分析关闭收尾失败：" + error.Message; return false; }
        finally { _closing = false; Notify(); }
    }
    public void Dispose() { if (!_disposed) _ = _registration?.CloseAsync() ?? CloseCoreAsync(); }
    public ValueTask DisposeAsync() => _disposed ? ValueTask.CompletedTask : new(_registration?.CloseAsync() ?? CloseCoreAsync());
    private async Task CloseCoreAsync()
    {
        if (_disposed) return;
        if (!await SaveBeforeCloseAsync()) throw new IOException(Status);
        if (reader.Conversion is not null) await reader.Conversion.DisposeAsync();
        _disposed = true; if (_subscribed) activity.Changed -= OnActivityChanged; reader.Clear(); Notify();
    }
    public static string StateName(AnalysisRunState state) => state switch
    { AnalysisRunState.Queued => "等待运行", AnalysisRunState.Running => "运行中", AnalysisRunState.Paused => "已暂停", AnalysisRunState.NeedsAttention => "待处理", AnalysisRunState.Completed => "报告候选完成", AnalysisRunState.Cancelled => "已取消", AnalysisRunState.ExtractionCompleted => "全文提取完成", _ => "跨章整合完成" };
    private static string NodeName(AnalysisNodeKind kind) => kind switch
    { AnalysisNodeKind.Extraction => "全文提取", AnalysisNodeKind.Integration => "跨章整合", AnalysisNodeKind.Summary => "阶段汇总", AnalysisNodeKind.Dimension => "专题", _ => "综合" };
}
