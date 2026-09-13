using System.Collections.Immutable;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovelGeneratePlugin.Application.Templates;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Application.Projects;
namespace NovelGeneratePlugin.Features.TemplateLibrary;

public sealed record TemplateItem(Guid Id, string Name, bool Archived)
{ public override string ToString() => Name + (Archived ? "（已归档）" : ""); }

/// <summary>
/// 一个共享模板面板，只管理草案表单。库用例由根服务提供，隐藏面板不会销毁草案或改变本书。
/// 切换模板前要求保存或显式放弃草案；退出时尝试保存，失败草案写入独立恢复文件。
/// </summary>
public sealed partial class TemplateLibraryTool(Application.Templates.TemplateLibrary library, PluginCloseCoordinator shutdown, MaterialCalibrationPanel? calibration = null, NovelAnalysisPanel? analysis = null) : ObservableObject, IAsyncDisposable, IDisposable, IClosePreparation
{
    public NovelAnalysisPanel? Analysis => analysis;
    public bool HasAnalysis => analysis is not null;
    public MaterialCalibrationPanel? Calibration => calibration;
    public bool HasCalibration => calibration is not null;
    private CloseRegistration? _closeRegistration;
    private CloseRegistration EnsureCloseRegistration() => _closeRegistration ??= shutdown.Register(CloseCoreAsync, SynchronizationContext.Current);
    private TemplateAsset? _current;
    private IReadOnlyList<TemplateAsset> _all = [];
    private bool _loading, _disposed, _preparingClose;
    private long _editGeneration, _savedGeneration, _recoveredGeneration = -1, _closeSafeGeneration = -1;
    private readonly Guid _recoveryId = Guid.NewGuid();
    private Task _operation = Task.CompletedTask;
    private Task? _initialization;
    private Task<bool>? _closeTask;
    [ObservableProperty] private TemplateItem? _selectedTemplate;
    [ObservableProperty] private TemplateRecoveryEntry? _selectedRecovery;
    [ObservableProperty] private string _name = "新模板";
    [ObservableProperty] private string _tags = "";
    [ObservableProperty] private string _world = "";
    [ObservableProperty] private string _style = "";
    [ObservableProperty] private string _methods = "";
    [ObservableProperty] private string _rules = "";
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private bool _showArchived;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "创建命名模板，保存版本后可供作品采用。";
    [ObservableProperty] private string _versionStatus = "尚无已保存版本";
    [ObservableProperty] private string _sourceStatus = "手工模板";
    public ObservableCollection<TemplateItem> Templates { get; } = [];
    public ObservableCollection<TemplateRecoveryEntry> Recoveries { get; } = [];
    public bool IsDirty => _editGeneration != _savedGeneration;
    public bool CanManage => !IsBusy && !_disposed && !_preparingClose;
    public bool CanNavigate => CanManage && !IsDirty;
    public bool CanEdit => CanManage && _current?.Archived != true;
    public Task InitializeAsync() => _initialization ??= RunAsync(async () =>
    {
        if (analysis?.Report.Conversion is { } conversion)
        {
            conversion.DraftCreatedAsync = RefreshGeneratedListAsync;
            conversion.OpenDraftAsync = OpenGeneratedDraftAsync;
        }
        await RefreshCoreAsync(null); if (calibration is not null) await calibration.InitializeAsync(); if (analysis is not null) await analysis.InitializeAsync();
    });
    public Task OpenGeneratedDraftAsync(Guid id)
    {
        if (!CanManage || IsDirty) { Status = "生成草案已保存在模板库；请先保存或放弃当前编辑，再打开它。"; return Task.FromException(new InvalidOperationException(Status)); }
        return RunAsync(async () =>
        {
            var asset = await library.ReadAsync(id);
            Search = ""; if (asset.Archived) ShowArchived = true;
            await RefreshCoreAsync(id); Status = "已打开生成草案，可编辑后保存新版本。";
        });
    }
    private async Task RefreshGeneratedListAsync(Guid id)
    {
        await _operation;
        if (CanManage) await RunAsync(() => RefreshCoreAsync(_current?.Id));
    }
    partial void OnNameChanged(string value) => MarkDirty();
    partial void OnTagsChanged(string value) => MarkDirty();
    partial void OnWorldChanged(string value) => MarkDirty();
    partial void OnStyleChanged(string value) => MarkDirty();
    partial void OnMethodsChanged(string value) => MarkDirty();
    partial void OnRulesChanged(string value) => MarkDirty();
    partial void OnSearchChanged(string value) => Filter();
    partial void OnShowArchivedChanged(bool value) => Filter();
    partial void OnIsBusyChanged(bool value) => NotifyCommands();
    private void MarkDirty()
    { if (_loading) return; EnsureCloseRegistration(); _editGeneration++; _closeSafeGeneration = -1; Status = "草案有未保存修改；保存草案不会改变已保存版本。"; NotifyCommands(); }
    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(IsDirty)); OnPropertyChanged(nameof(CanManage)); OnPropertyChanged(nameof(CanNavigate)); OnPropertyChanged(nameof(CanEdit));
        NewDraftCommand.NotifyCanExecuteChanged(); SaveDraftCommand.NotifyCanExecuteChanged(); PublishVersionCommand.NotifyCanExecuteChanged();
        CopyCommand.NotifyCanExecuteChanged(); ToggleArchiveCommand.NotifyCanExecuteChanged(); RefreshCommand.NotifyCanExecuteChanged();
        ResetDraftCommand.NotifyCanExecuteChanged(); RestoreRecoveryCommand.NotifyCanExecuteChanged();
    }
    partial void OnSelectedTemplateChanged(TemplateItem? oldValue, TemplateItem? newValue)
    {
        if (_loading) return;
        if (!CanManage || IsDirty)
        {
            _loading = true; try { SelectedTemplate = oldValue; } finally { _loading = false; }
            Status = "请先保存草案，或明确放弃草案更改，再切换模板。"; return;
        }
        Load(newValue is null ? null : _all.Single(a => a.Id == newValue.Id));
    }
    private void Load(TemplateAsset? asset)
    {
        _loading = true;
        try
        {
            _current = asset; var draft = asset?.Draft;
            Name = draft?.Name ?? "新模板"; Tags = draft is null ? "" : string.Join("，", draft.Tags);
            World = draft?.Content.World ?? ""; Style = draft?.Content.Style ?? ""; Methods = draft?.Content.Methods ?? ""; Rules = draft?.Content.Rules ?? "";
            _editGeneration = _savedGeneration = 0; _recoveredGeneration = _closeSafeGeneration = -1;
            VersionStatus = asset is null || asset.Versions.IsEmpty ? "尚无已保存版本" : $"已保存 {asset.Versions.Length} 个不可变版本；最新 v{asset.Versions[^1].Number}";
            SourceStatus = draft?.Provenance is { } source ? $"{draft.Source}\n首次生成 {source.GeneratedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss}；后续编辑与来源分析独立保存。" : draft?.Source ?? "手工模板";
        }
        finally { _loading = false; }
        NotifyCommands();
    }
    private TemplateDraft CaptureDraft() => new(string.IsNullOrWhiteSpace(Name) ? "未命名模板" : Name.Trim(),
        Tags.Split([',', '，', ';', '；'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct().ToImmutableArray(),
        new WritingProfile(World, Style, Methods, Rules), _current?.Draft.Source ?? "手工创建")
    { Provenance = _current?.Draft.Provenance };
    private void Filter()
    {
        if (_loading) return;
        _loading = true;
        try
        {
            var selectedId = SelectedTemplate?.Id;
            Templates.Clear();
            foreach (var asset in _all.Where(a => (ShowArchived || !a.Archived) &&
                (string.IsNullOrWhiteSpace(Search) || a.Draft.Name.Contains(Search, StringComparison.OrdinalIgnoreCase) || a.Draft.Tags.Any(t => t.Contains(Search, StringComparison.OrdinalIgnoreCase)))))
                Templates.Add(new TemplateItem(asset.Id, asset.Draft.Name, asset.Archived));
            SelectedTemplate = Templates.FirstOrDefault(a => a.Id == selectedId);
        }
        finally { _loading = false; }
    }
    private async Task RefreshCoreAsync(Guid? selection)
    {
        _all = await library.ListAsync(); Filter();
        _loading = true; try { SelectedTemplate = Templates.FirstOrDefault(t => t.Id == selection); } finally { _loading = false; }
        if (!IsDirty) Load(selection is Guid id ? _all.FirstOrDefault(a => a.Id == id) : null);
        Recoveries.Clear(); foreach (var recovery in await library.ListRecoveryAsync()) Recoveries.Add(recovery);
    }
    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void NewDraft() { _loading = true; try { SelectedTemplate = null; } finally { _loading = false; } Load(null); Status = "填写模板名称与规范，然后保存草案或保存新版本。"; }
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task SaveDraft() => RunAsync(async () => { await SaveCoreAsync(); });
    private async Task<bool> SaveCoreAsync()
    {
        if (!IsDirty && _current is not null) return true;
        var draft = CaptureDraft(); var generation = _editGeneration;
        try { _current = _current is null ? await library.CreateAsync(draft) : await library.SaveDraftAsync(_current, draft); }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            Status = "草案保存失败：" + exception.Message;
            try
            {
                var asset = _current is null ? TemplateAsset.Create(draft) : _current with { Draft = draft };
                await library.WriteRecoveryAsync(new TemplateDraftRecovery(_recoveryId, 1, asset, generation));
                _recoveredGeneration = generation; Status += "；已保留独立恢复草案。";
            }
            catch (Exception backupError) when (backupError is not OutOfMemoryException) { Status += "；恢复草案写入失败：" + backupError.Message; }
            NotifyCommands(); return false;
        }
        _savedGeneration = generation; Status = "草案已保存；已保存版本保持不变。";
        try { await library.DeleteRecoveryAsync(_recoveryId); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { Status += "旧恢复草案未能清理。"; }
        try { await RefreshCoreAsync(_current.Id); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { Status += "列表刷新失败：" + exception.Message; }
        NotifyCommands(); return true;
    }
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task PublishVersion() => RunAsync(async () =>
    {
        if (!await SaveCoreAsync()) return;
        _current = await library.PublishAsync(_current!); await RefreshCoreAsync(_current.Id); Status = "新版本已保存，已采用旧版的作品不变。";
    });
    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private Task Copy() => RunAsync(async () =>
    { if (_current is null) return; var copy = await library.CopyAsync(_current); await RefreshCoreAsync(copy.Id); Status = "已复制为独立草案；保存新版本后可供作品采用。"; });
    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private Task ToggleArchive() => RunAsync(async () =>
    { if (_current is null) return; _current = await library.ArchiveAsync(_current, !_current.Archived); await RefreshCoreAsync(_current.Id); Status = "归档状态已更新，已有作品快照不受影响。"; });
    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private Task Refresh() => RunAsync(() => RefreshCoreAsync(_current?.Id));
    [RelayCommand(CanExecute = nameof(CanManage))]
    private void ResetDraft() { Load(_current); Status = "已放弃尚未保存的草案更改。"; }
    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private Task RestoreRecovery() => RunAsync(async () =>
    { if (SelectedRecovery is null) return; var restored = await library.RestoreAsCopyAsync(SelectedRecovery.Id); await RefreshCoreAsync(restored.Id); Status = "已恢复为独立模板，原恢复草案保留。"; });
    private Task RunAsync(Func<Task> operation)
    { if (!CanManage) return Task.CompletedTask; EnsureCloseRegistration(); return _operation = RunCoreAsync(operation); }
    private async Task RunCoreAsync(Func<Task> operation)
    {
        IsBusy = true;
        try { await operation(); }
        catch (Exception exception) when (exception is not OutOfMemoryException) { Status = exception.Message; }
        finally { IsBusy = false; NotifyCommands(); }
    }
    public Task<bool> SaveBeforeCloseAsync()
    {
        // 多个关闭入口共享在途操作。关闭期间禁用命令；程序侧仍可能更新表单，故还须核对实际落盘代数。
        if (_closeTask is { IsCompleted: false }) return _closeTask;
        return _closeTask = PrepareCloseCoreAsync();
    }
    private async Task<bool> PrepareCloseCoreAsync()
    {
        _preparingClose = true; NotifyCommands();
        try
        {
            await _operation;
            if (analysis is not null && !await analysis.SaveBeforeCloseAsync()) { Status = analysis.Status; return false; }
            if (calibration is not null && !await calibration.SaveBeforeCloseAsync()) { Status = calibration.Status; return false; }
            if (!IsDirty || _closeSafeGeneration == _editGeneration) return true;
            if (await SaveCoreAsync())
            {
                if (IsDirty) { Status = "关闭保存期间有新修改，请再次保存。"; return false; }
                _closeSafeGeneration = _savedGeneration; return true;
            }
            if (_recoveredGeneration != _editGeneration) return false;
            try
            {
                if ((await library.ReadRecoveryAsync(_recoveryId)).EditGeneration != _editGeneration) return false;
                _closeSafeGeneration = _editGeneration; return true;
            }
            catch (Exception exception) when (exception is not OutOfMemoryException) { Status += "；恢复草案验证失败。"; return false; }
        }
        finally { _preparingClose = false; NotifyCommands(); }
    }
    public void Dispose() { if (!_disposed) _ = _closeRegistration?.CloseAsync() ?? CloseCoreAsync(); }
    public ValueTask DisposeAsync() => _disposed ? ValueTask.CompletedTask : new(_closeRegistration?.CloseAsync() ?? CloseCoreAsync());
    private async Task CloseCoreAsync()
    {
        if (_disposed) return;
        if (!await SaveBeforeCloseAsync()) throw new IOException(Status);
        if (calibration is not null) await calibration.DisposeAsync();
        if (analysis is not null) await analysis.DisposeAsync();
        _disposed = true; NotifyCommands();
    }
}
