using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovelGeneratePlugin.Application.Connections;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Application.Projects;
using NovelGeneratePlugin.Application.Templates;
using NovelGeneratePlugin.Domain;
namespace NovelGeneratePlugin.Features.TemplateLibrary;

public sealed record MaterialEvidenceChoice(MaterialEvidence Evidence) { public override string ToString() => $"片段 {Evidence.Segment}：{Evidence.Quote}"; }
public sealed record MaterialChoice(Guid Id, string Name) { public override string ToString() => Name; }
/// <summary>共享 Tool 中的独立材料任务所有者；隐藏视图不销毁它，关闭作品也不触及它的取消令牌。</summary>
public sealed partial class MaterialCalibrationPanel(MaterialCalibrationService service, ConnectionService connections, Application.Templates.TemplateLibrary library, PluginCloseCoordinator shutdown)
    : ObservableObject, IAsyncDisposable, IDisposable, IClosePreparation
{
    private MaterialDocument _current = MaterialDocument.Create(); private long _edits, _saved; private bool _loading, _disposed, _preparingClose;
    private Task<bool>? _closePreparation; private Task _operation = Task.CompletedTask; private Task? _initialization; private CloseRegistration? _registration; private CancellationTokenSource? _taskCancellation;
    [ObservableProperty] private string _name = "新材料";
    [ObservableProperty] private MaterialPurpose _purpose;
    [ObservableProperty] private string _source = "作者粘贴";
    [ObservableProperty] private string _text = "";
    [ObservableProperty] private string _filePath = "";
    [ObservableProperty] private int _analyzeCharacters = 12000;
    [ObservableProperty] private string _scene = "邮差在旧门前发现一把钥匙。";
    [ObservableProperty] private string _constraints = "只有邮差一人出场\n本场景没有获得钥匙的归属信息";
    [ObservableProperty] private string _methods = "";
    [ObservableProperty] private string _style = "";
    [ObservableProperty] private string _feedback = "";
    [ObservableProperty] private string _chosenSample = "";
    [ObservableProperty] private string _trialPreview = "尚无对比试写，可跳过试写直接采用提炼结果。";
    [ObservableProperty] private string _status = "粘贴短课件或样文，保存草案后独立提炼。";
    [ObservableProperty] private string _usage = "分析和试写使用独立预算，不计入正文运行。";
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private MaterialChoice? _selectedMaterial;
    [ObservableProperty] private ModelConnection? _selectedConnection;
    [ObservableProperty] private MaterialEvidenceChoice? _selectedEvidence;
    [ObservableProperty] private int _sourceSelectionStart;
    [ObservableProperty] private int _sourceSelectionEnd;
    public ObservableCollection<MaterialEvidenceChoice> EvidenceChoices { get; } = [];
    public ObservableCollection<MaterialChoice> Materials { get; } = [];
    public ObservableCollection<ModelConnection> Connections { get; } = [];
    public IReadOnlyList<MaterialPurpose> Purposes { get; } = Enum.GetValues<MaterialPurpose>();
    public bool IsDirty => _edits != _saved;
    public bool CanEdit => !IsBusy && !_disposed && !_preparingClose;
    public bool CanNavigate => CanEdit && !IsDirty;
    public bool CanCancel => _taskCancellation is not null;
    public string Coverage => $"全文 {Text.Length} 字符；本次处理前 {Math.Min(Text.Length, AnalyzeCharacters)} 字符，未处理 {Math.Max(0, Text.Length - AnalyzeCharacters)} 字符。";
    private void EnsureRegistration() => _registration ??= shutdown.Register(CloseCoreAsync, SynchronizationContext.Current);
    public Task InitializeAsync() => _initialization ??= RunAsync(async () => { await RefreshCoreAsync(); Load(_current); });
    partial void OnNameChanged(string value) => Dirty(); partial void OnPurposeChanged(MaterialPurpose value) => Dirty(); partial void OnSourceChanged(string value) => Dirty();
    partial void OnTextChanged(string value) { Dirty(); OnPropertyChanged(nameof(Coverage)); }
    partial void OnAnalyzeCharactersChanged(int value) { Dirty(); OnPropertyChanged(nameof(Coverage)); }
    partial void OnSceneChanged(string value) => Dirty(); partial void OnConstraintsChanged(string value) => Dirty(); partial void OnMethodsChanged(string value) => Dirty();
    partial void OnStyleChanged(string value) => Dirty(); partial void OnFeedbackChanged(string value) => Dirty(); partial void OnChosenSampleChanged(string value) => Dirty();
    partial void OnSelectedConnectionChanged(ModelConnection? value) => Dirty(); partial void OnIsBusyChanged(bool value) => NotifyCommands();
    private void Dirty()
    {
        if (_loading) return; EnsureRegistration(); _edits++; NotifyCommands();
        if (_current.Trial is not null && Capture().SampleStamp != _current.TrialStamp)
        {
            Status = "试写依据已变化，旧样稿只供参考，可重新试写或明确跳过。";
            if (!TrialPreview.StartsWith("【试写依据已变化", StringComparison.Ordinal)) TrialPreview = "【试写依据已变化，旧样稿只供参考】\n" + TrialPreview;
        }
    }
    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(CanNavigate)); OnPropertyChanged(nameof(CanCancel)); OnPropertyChanged(nameof(IsDirty));
        DiscardMaterialEditsCommand.NotifyCanExecuteChanged();
        LocateEvidenceCommand.NotifyCanExecuteChanged();
        SaveMaterialCommand.NotifyCanExecuteChanged(); AnalyzeMaterialCommand.NotifyCanExecuteChanged(); TryStyleCommand.NotifyCanExecuteChanged(); CancelMaterialCommand.NotifyCanExecuteChanged();
        ReadMaterialCommand.NotifyCanExecuteChanged(); RefreshMaterialsCommand.NotifyCanExecuteChanged(); NewMaterialCommand.NotifyCanExecuteChanged(); ImportTextCommand.NotifyCanExecuteChanged(); PublishMaterialTemplateCommand.NotifyCanExecuteChanged();
    }
    private MaterialDocument Capture() => _current with
    {
        Name = Name,
        Purpose = Purpose,
        Source = Source,
        Text = Text,
        AnalyzeCharacters = AnalyzeCharacters,
        Scene = Scene,
        Constraints = Constraints,
        Methods = Methods,
        Style = Style,
        Feedback = Feedback,
        ChosenSample = ChosenSample,
        Connection = SelectedConnection is null ? _current.Connection : ConnectionService.Bind(SelectedConnection)
    };
    private void Load(MaterialDocument document)
    {
        _loading = true; try
        {
            _current = document; Name = document.Name; Purpose = document.Purpose; Source = document.Source; Text = document.Text; AnalyzeCharacters = document.AnalyzeCharacters;
            Scene = document.Scene; Constraints = document.Constraints; Methods = document.Methods; Style = document.Style; Feedback = document.Feedback; ChosenSample = document.ChosenSample;
            SelectedConnection = Connections.SingleOrDefault(c => c.Id == document.Connection?.ConnectionId && c.Version == document.Connection.Version);
            TrialPreview = document.Trial is { } trial ? $"章纲小样：{trial.Goal}\n铺设：{trial.Setup}\n阻碍：{trial.Pressure}\n兑现：{trial.Payoff}\n方法：{trial.MethodQuote}\n\n样稿 A：\n{trial.SampleA}\n\n样稿 B：\n{trial.SampleB}" : "尚无对比试写，可跳过。";
            if (document.Trial is not null && document.TrialStamp != document.SampleStamp) TrialPreview = "【试写依据已变化，旧样稿只供参考】\n" + TrialPreview;
            EvidenceChoices.Clear(); SelectedEvidence = null;
            if (document.Analysis is { } analysis) foreach (var evidence in analysis.Methods.SelectMany(m => m.Evidence).Concat(analysis.Style.SelectMany(m => m.Evidence)).Distinct()) EvidenceChoices.Add(new(evidence));
            _edits = _saved = 0;
        }
        finally { _loading = false; }
        NotifyCommands(); RefreshUsage();
    }
    private void RefreshUsage() { try { var usage = service.Usage(_current); Usage = $"独立任务 {_current.Budgets.Length} 次；请求 {usage.Count} 条，已计/保守预留 {usage.Sum(r => r.ChargedTokens)} token。"; } catch (Exception error) when (error is not OutOfMemoryException) { Usage = "材料任务用量暂不可读。"; } }
    private async Task RefreshCoreAsync()
    {
        var materials = await service.ListAsync(); Materials.Clear(); foreach (var item in materials) Materials.Add(new(item.Id, item.Name));
        var catalogue = await connections.ListAsync(); Connections.Clear(); foreach (var connection in catalogue.Connections) Connections.Add(connection);
    }
    private async Task SaveCoreAsync() { var generation = _edits; _current = await service.SaveAsync(Capture()); _saved = generation; Status = "材料草案已保存。"; NotifyCommands(); }
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private void LocateEvidence()
    {
        if (SelectedEvidence is null) return; var current = Capture();
        if (current.SourceStamp != current.AnalysisStamp) { Status = "材料来源已变化，旧证据不可按原位置定位。"; return; }
        var evidence = SelectedEvidence.Evidence; var segment = current.Segments().Single(s => s.Number == evidence.Segment); var offset = segment.Text.IndexOf(evidence.Quote, StringComparison.Ordinal);
        if (offset < 0) { Status = "证据原文不存在。"; return; }
        SourceSelectionStart = segment.Start + offset; SourceSelectionEnd = SourceSelectionStart + evidence.Quote.Length;
    }
    [RelayCommand(CanExecute = nameof(CanEdit))] private Task SaveMaterial() => RunAsync(SaveCoreAsync);
    [RelayCommand(CanExecute = nameof(CanNavigate))] private Task RefreshMaterials() => RunAsync(RefreshCoreAsync);
    [RelayCommand(CanExecute = nameof(CanNavigate))] private Task ReadMaterial() => RunAsync(async () => { if (SelectedMaterial is not null) Load(await service.ReadAsync(SelectedMaterial.Id)); });
    [RelayCommand(CanExecute = nameof(CanEdit))] private Task DiscardMaterialEdits() => RunAsync(async () => { Load(await service.ReadAsync(_current.Id)); Status = "已放弃本地未保存更改并读取当前材料版本。"; });
    [RelayCommand(CanExecute = nameof(CanNavigate))] private void NewMaterial() => Load(MaterialDocument.Create());
    [RelayCommand(CanExecute = nameof(CanEdit))] private Task ImportText() => RunAsync(async () => { var path = FilePath; var generation = _edits; var text = await service.ReadTextFileAsync(path); if (generation != _edits) throw new InvalidOperationException("读取期间有新输入，未覆盖材料。"); Text = text; Source = Path.GetFileName(path); await SaveCoreAsync(); });
    [RelayCommand(CanExecute = nameof(CanEdit))] private Task AnalyzeMaterial() => AnalyzeOrTrial(false);
    [RelayCommand(CanExecute = nameof(CanEdit))] private Task TryStyle() => AnalyzeOrTrial(true);
    private Task AnalyzeOrTrial(bool trial) => RunAsync(async () =>
    {
        var generation = _edits; using var cancellation = new CancellationTokenSource(); _taskCancellation = cancellation; NotifyCommands();
        try
        {
            await SaveCoreAsync(); cancellation.Token.ThrowIfCancellationRequested();
            if (generation != _edits) { Status = "保存期间有新输入，请保存并重新发起分析，尚未调用模型。"; return; }
            Status = trial ? "正在生成同剧情对比试写。" : "正在提炼所选材料片段。";
            var result = trial ? await service.TrialAsync(_current, cancellation.Token) : await service.AnalyzeAsync(_current, cancellation.Token);
            var unchanged = generation == _edits; _current = result; if (unchanged) Load(result);
            Status = unchanged ? "结果已保存，可修改批注后明确采用。" : "结果已保存，期间的新输入保留，请先保存并核对来源。";
        }
        finally { _taskCancellation = null; NotifyCommands(); RefreshUsage(); }
    });
    [RelayCommand(CanExecute = nameof(CanCancel))] private void CancelMaterial() => _taskCancellation?.Cancel();
    [RelayCommand(CanExecute = nameof(CanEdit))]
    private Task PublishMaterialTemplate() => RunAsync(async () =>
    {
        await SaveCoreAsync(); var source = MaterialRules.Adoption(_current);
        await library.CreatePublishedAsync(new(source.Name, [], new("", source.Style, source.Methods, ""), $"材料 {source.MaterialId} / v{source.Version} · {source.Source}\n作者反馈：{source.Feedback}"));
        Status = "已保存为独立命名模板 v1，作品未自动改变。";
    });
    private Task RunAsync(Func<Task> action) { if (!CanEdit) return Task.CompletedTask; EnsureRegistration(); return _operation = RunCoreAsync(action); }
    private async Task RunCoreAsync(Func<Task> action)
    {
        IsBusy = true; try { await action(); }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            Status = error.Message;
            // 只能接受本次任务自己保存过的版本；普通并发冲突保持旧基线，要求作者明确读取复核。
            if (error is MaterialTaskException task && task.Checkpoint.Id == _current.Id) _current = task.Checkpoint;
        }
        finally { IsBusy = false; RefreshUsage(); }
    }
    public Task<bool> SaveBeforeCloseAsync() => _closePreparation is { IsCompleted: false } ? _closePreparation : _closePreparation = PrepareCloseAsync();
    private async Task<bool> PrepareCloseAsync()
    {
        _preparingClose = true; NotifyCommands(); _taskCancellation?.Cancel();
        try { await _operation; if (!IsDirty) return true; await SaveCoreAsync(); return !IsDirty; }
        catch (Exception error) when (error is not OutOfMemoryException) { Status = "材料草案未保存：" + error.Message; return false; }
        finally { _preparingClose = false; NotifyCommands(); }
    }
    public void Dispose() { if (!_disposed) _ = _registration?.CloseAsync() ?? CloseCoreAsync(); }
    public ValueTask DisposeAsync() => _disposed ? ValueTask.CompletedTask : new(_registration?.CloseAsync() ?? CloseCoreAsync());
    private async Task CloseCoreAsync() { if (_disposed) return; if (!await SaveBeforeCloseAsync()) throw new IOException(Status); _disposed = true; NotifyCommands(); }
}
