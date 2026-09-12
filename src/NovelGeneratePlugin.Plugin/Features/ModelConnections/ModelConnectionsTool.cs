using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using NovelGeneratePlugin.Application.Connections;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Application.Projects;
using NovelGeneratePlugin.Application.Models;
namespace NovelGeneratePlugin.Features.ModelConnections;

public sealed partial class PresetEditor(string purpose) : ObservableObject
{
    private bool _updatingChoices;
    public string Purpose { get; } = purpose;
    [ObservableProperty] private string _model = "deepseek-flash";
    [ObservableProperty] private int _maxOutputTokens = 65536;
    [ObservableProperty] private string _reasoningEffort = "high";
    public IReadOnlyList<string> ModelChoices { get; private set; } = [];
    public IReadOnlyList<string> EffortChoices { get; private set; } = [];
    // 下拉框重建时可能短暂回写 null；选择代理只接受有效项，不能清空已保存的自定义模型。
    public string? SelectedModel { get => Model; set { if (!_updatingChoices && !string.IsNullOrWhiteSpace(value)) Model = value; } }
    public string? SelectedEffort { get => ReasoningEffort; set { if (!_updatingChoices && !string.IsNullOrWhiteSpace(value)) ReasoningEffort = value; } }
    partial void OnModelChanged(string value)
    {
        var updating = _updatingChoices; _updatingChoices = true;
        try
        {
            if (!string.IsNullOrWhiteSpace(value) && !ModelChoices.Contains(value)) { ModelChoices = [.. ModelChoices, value]; OnPropertyChanged(nameof(ModelChoices)); }
            OnPropertyChanged(nameof(SelectedModel));
        }
        finally { _updatingChoices = updating; }
    }
    partial void OnReasoningEffortChanged(string value) => OnPropertyChanged(nameof(SelectedEffort));
    public void ConfigureChoices(ModelProvider provider)
    {
        _updatingChoices = true;
        try
        {
            // 一次替换完整选项，避免 Clear/Add 让真实 ComboBox 在刷新时丢失当前选择。
            ModelChoices = ConnectionDefaults.Models(provider).Append(Model).Where(m => !string.IsNullOrWhiteSpace(m)).Distinct().ToArray();
            // 保留旧连接 medium 等已验证兼容值，不因加载表单而迁移用户配置。
            EffortChoices = ConnectionDefaults.Efforts(provider).Append(ReasoningEffort).Distinct().ToArray();
            OnPropertyChanged(nameof(ModelChoices)); OnPropertyChanged(nameof(EffortChoices));
            OnPropertyChanged(nameof(SelectedModel)); OnPropertyChanged(nameof(SelectedEffort));
        }
        finally { _updatingChoices = false; }
    }
    public ModelPreset Capture() => new(Model.Trim(), MaxOutputTokens, ReasoningEffort.Trim());
    public void Load(ModelPreset preset)
    {
        // 切换服务商时旧下拉控件会同步回写旧值；批量载入期间只发布新值，不接受这些回写。
        _updatingChoices = true;
        try { Model = preset.Model; MaxOutputTokens = preset.MaxOutputTokens; ReasoningEffort = preset.ReasoningEffort; }
        finally { _updatingChoices = false; }
    }
}
/// <summary>共享连接表单。密钥是短暂输入字段，保存或显式清空后立即清除，不从服务取回已有 Key。</summary>
public sealed partial class ModelConnectionsTool : ObservableObject, IClosePreparation, IAsyncDisposable, IDisposable
{
    private readonly ConnectionService _service;
    private readonly PluginCloseCoordinator _shutdown;
    private CloseRegistration? _closeRegistration;
    private CloseRegistration EnsureCloseRegistration() => _closeRegistration ??= _shutdown.Register(CloseCoreAsync, SynchronizationContext.Current);
    private ModelConnection? _current;
    private bool _loading, _disposed, _preparingClose;
    private long _edited, _saved;
    private long _secretGeneration;
    private Task _operation = Task.CompletedTask;
    private Task? _initialization;
    private Task<bool>? _closeTask;
    private readonly ModelRequestService _requests;
    private CancellationTokenSource? _probeCancellation;
    public ModelConnectionsTool(ConnectionService service, PluginCloseCoordinator shutdown, ModelRequestService requests)
    {
        _service = service;
        _shutdown = shutdown;
        _requests = requests;
        foreach (var preset in Presets) preset.PropertyChanged += (_, _) => MarkDirty();
        Load(null);
    }
    public ObservableCollection<ModelConnection> Connections { get; } = [];
    public ObservableCollection<ModelRequestEntry> RecentRequests { get; } = [];
    [ObservableProperty] private ModelRequestEntry? _selectedRequest;
    partial void OnSelectedRequestChanged(ModelRequestEntry? value)
    {
        if (value is not null) ProbeResult = $"{value}\n{value.PartialText}\n输入 token：{value.Usage.InputTokens?.ToString() ?? "未知"}；输出 token：{value.Usage.OutputTokens?.ToString() ?? "未知"}。候选不等于已接受的工作稿。";
    }
    public IReadOnlyList<PresetEditor> Presets { get; } = [new("规划"), new("正文"), new("检查")];
    public IReadOnlyList<ModelProvider> Providers { get; } = Enum.GetValues<ModelProvider>();
    [ObservableProperty] private ModelConnection? _selectedConnection;
    [ObservableProperty] private string _name = "DeepSeek 默认连接";
    [ObservableProperty] private ModelProvider _provider = ModelProvider.DeepSeek;
    [ObservableProperty] private string _endpoint = "https://api.deepseek.com";
    [ObservableProperty] private string _codexExecutable = "";
    [ObservableProperty] private string _secretInput = "";
    [ObservableProperty] private bool _persistSecret;
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "先保存连接配置；Codex 使用 CLI 既有登录，API 连接单独配置密钥。";
    [ObservableProperty] private string _credentialStatus = "未选择连接";
    [ObservableProperty] private string _defaultStatus = "新书未设置默认连接";
    public bool IsDirty => _edited != _saved;
    public bool CanManage => !_disposed && !IsBusy && !_preparingClose;
    public bool CanNavigate => CanManage && !IsDirty && SecretInput.Length == 0;
    public bool CanUseSaved => CanManage && _current is not null && !IsDirty;
    public bool CanProbe => CanUseSaved && SecretInput.Length == 0;
    public bool CanCancelProbe => _probeCancellation is not null;
    [ObservableProperty] private string _probeResult = "生成检测会发送一条简短样本并消耗当前连接的套餐额度或 API 用量；启动与刷新凭据不会生成。Codex 输出 token 上限为软目标及事后校验。";
    public bool IsApi => Provider == ModelProvider.DeepSeek;
    public string ProviderHelp => IsApi
        ? "新连接已预填 DeepSeek 参数；已有连接可点下方按钮恢复默认。填写 API Key 后保存连接。思考：none 关闭，low 低，high 默认，max 最大；输出上限含思考与正文，本工作台最多允许 131072 token。"
        : "Codex 使用本机已登录 CLI，请填写 exe 完整路径；推理强度支持 low / medium / high。";
    public Task InitializeAsync() => _initialization ??= RunAsync(() => RefreshCoreAsync(null));
    partial void OnNameChanged(string value) => MarkDirty();
    partial void OnEndpointChanged(string value) => MarkDirty();
    partial void OnCodexExecutableChanged(string value) => MarkDirty();
    partial void OnProviderChanged(ModelProvider value)
    {
        if (!_loading)
        {
            // 用户主动切换服务商才应用新服务商参数；读取已有连接绝不能触发默认值覆盖。
            SecretInput = "";
            ApplyDefaultsCore(value);
            MarkDirty();
        }
        OnPropertyChanged(nameof(IsApi)); OnPropertyChanged(nameof(ProviderHelp));
    }
    private void ApplyDefaultsCore(ModelProvider provider)
    {
        var defaults = ConnectionDefaults.Create(provider);
        if (Name is "Codex 开发验证" or "DeepSeek 默认连接" || string.IsNullOrWhiteSpace(Name)) Name = defaults.Name;
        Endpoint = defaults.Endpoint;
        foreach (var preset in Presets) { preset.Load(defaults.Planning); preset.ConfigureChoices(provider); }
    }
    [RelayCommand(CanExecute = nameof(CanManage))]
    private void ApplyProviderDefaults()
    {
        // 恢复参数保留待提交 Key；凭据仍由服务按实际认证端点隔离。保存前不改变持久化配置。
        ApplyDefaultsCore(Provider); MarkDirty(); Status = "已填入当前服务商默认参数，请保存连接；已有作品仍需明确重新绑定。";
    }
    partial void OnSecretInputChanged(string value) { if (!_loading && value.Length > 0) EnsureCloseRegistration(); _secretGeneration++; NotifyCommands(); }
    partial void OnIsBusyChanged(bool value) => NotifyCommands();
    private void MarkDirty() { if (!_loading) { EnsureCloseRegistration(); _edited++; Status = "配置有未保存更改。端点变更后需重新配置密钥。"; NotifyCommands(); } }
    private void NotifyCommands()
    {
        OnPropertyChanged(nameof(IsDirty)); OnPropertyChanged(nameof(CanManage)); OnPropertyChanged(nameof(CanNavigate)); OnPropertyChanged(nameof(CanUseSaved));
        NewConnectionCommand.NotifyCanExecuteChanged(); SaveConfigurationCommand.NotifyCanExecuteChanged(); RefreshCommand.NotifyCanExecuteChanged();
        ResetConfigurationCommand.NotifyCanExecuteChanged(); SaveSecretCommand.NotifyCanExecuteChanged(); ClearSecretCommand.NotifyCanExecuteChanged();
        SaveConnectionCommand.NotifyCanExecuteChanged(); ApplyProviderDefaultsCommand.NotifyCanExecuteChanged();
        MakeDefaultCommand.NotifyCanExecuteChanged(); ClearDefaultCommand.NotifyCanExecuteChanged(); ClearInputCommand.NotifyCanExecuteChanged();
        ProbeCommand.NotifyCanExecuteChanged(); CancelProbeCommand.NotifyCanExecuteChanged(); OnPropertyChanged(nameof(CanProbe)); OnPropertyChanged(nameof(CanCancelProbe));
    }
    partial void OnSelectedConnectionChanged(ModelConnection? oldValue, ModelConnection? newValue)
    {
        if (_loading) return;
        if (!CanManage || IsDirty || SecretInput.Length > 0)
        { _loading = true; try { SelectedConnection = oldValue; } finally { _loading = false; } Status = "先保存或放弃配置，并处理尚未保存的密钥输入。"; return; }
        Load(newValue);
        // 查询期间禁止再次切换；业务异常在命令内观察，结果还须核对连接归属。
        _ = RunAsync(UpdateCredentialStatusAsync);
    }
    private void Load(ModelConnection? connection)
    {
        _loading = true;
        try
        {
            _current = connection; var settings = connection?.Settings;
            SelectedRequest = null; RecentRequests.Clear();
            var defaults = ConnectionDefaults.Create(ModelProvider.DeepSeek);
            Name = settings?.Name ?? defaults.Name; Provider = settings?.Provider ?? defaults.Provider;
            Endpoint = settings?.Endpoint ?? defaults.Endpoint; CodexExecutable = settings?.CodexExecutable ?? "";
            Presets[0].Load(settings?.Planning ?? defaults.Planning); Presets[1].Load(settings?.Drafting ?? defaults.Drafting); Presets[2].Load(settings?.Checking ?? defaults.Checking);
            foreach (var preset in Presets) preset.ConfigureChoices(Provider);
            SecretInput = ""; _edited = _saved = 0; CredentialStatus = connection is null ? "未选择连接" : "请刷新凭据状态";
        }
        finally { _loading = false; }
        NotifyCommands();
    }
    private ConnectionSettings Capture() => new(Name.Trim(), Provider, IsApi ? Endpoint.Trim().TrimEnd('/') : "", IsApi ? "" : CodexExecutable.Trim(), Presets[0].Capture(), Presets[1].Capture(), Presets[2].Capture());
    private async Task RefreshCoreAsync(Guid? selected)
    {
        var secretGeneration = _secretGeneration;
        var catalog = await _service.ListAsync();
        _loading = true;
        try { Connections.Clear(); foreach (var item in catalog.Connections) Connections.Add(item); SelectedConnection = Connections.SingleOrDefault(c => c.Id == selected); }
        finally { _loading = false; }
        if (!IsDirty && _secretGeneration == secretGeneration && SecretInput.Length == 0) Load(SelectedConnection);
        DefaultStatus = catalog.Connections.SingleOrDefault(c => c.Id == catalog.DefaultConnectionId) is { } current ? "仅新书默认：" + current.Settings.Name : "新书未设置默认连接";
        await UpdateCredentialStatusAsync();
    }
    private async Task UpdateCredentialStatusAsync()
    {
        if (_current is null) { CredentialStatus = "先保存连接"; return; }
        var queried = _current;
        var state = await _service.StateAsync(ConnectionService.Bind(queried));
        if (_current?.Id != queried.Id || _current.Version != queried.Version) return;
        CredentialStatus = state switch
        {
            CredentialState.Session => "已配置 · 仅本次会话",
            CredentialState.Encrypted => "已配置 · Windows 当前用户加密",
            CredentialState.Unavailable => "凭据不可用，请重新输入或清除",
            CredentialState.ManagedByCodex => "由 Codex CLI 管理登录；本地未检测远程可用性",
            _ => "缺少 API Key；不影响作品打开与编辑"
        };
        var recent = await _requests.RecentAsync(queried.Id);
        if (_current?.Id != queried.Id || _current.Version != queried.Version) return;
        SelectedRequest = null; RecentRequests.Clear(); foreach (var entry in recent) RecentRequests.Add(entry);
    }
    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private void NewConnection() { _loading = true; try { SelectedConnection = null; } finally { _loading = false; } Load(null); }
    [RelayCommand(CanExecute = nameof(CanManage))]
    private Task SaveConfiguration() => RunAsync(SaveCoreAsync);
    [RelayCommand(CanExecute = nameof(CanManage))]
    private Task SaveConnection() => RunAsync(async () =>
    {
        // 一个入口依次保存公开配置和独立凭据；保留原有分步命令供只修改模型的用户使用。
        // 保存期间的新 Key 输入不能被此次操作误用或清空，失败时输入也必须保留供重试。
        var input = SecretInput; var generation = _secretGeneration; var persist = PersistSecret;
        await SaveCoreAsync();
        if (_current!.Settings.Provider == ModelProvider.DeepSeek && input.Length > 0)
        {
            await _service.SetSecretAsync(ConnectionService.Bind(_current!), input, persist);
            if (_secretGeneration == generation) SecretInput = "";
            await UpdateCredentialStatusAsync();
            Status = "连接配置与本次输入的 API Key 已保存；请在本书明确绑定此配置。";
        }
    });
    private async Task SaveCoreAsync()
    {
        if (!IsDirty && _current is not null) return;
        var settings = Capture(); var generation = _edited;
        _current = await _service.SaveAsync(_current, settings); _saved = generation;
        // 不刷新整个表单，避免成功保存配置把用户尚未提交的密钥输入清空。
        _loading = true;
        try { var previous = Connections.FirstOrDefault(c => c.Id == _current.Id); if (previous is not null) Connections.Remove(previous); Connections.Add(_current); SelectedConnection = _current; }
        finally { _loading = false; }
        Status = "连接配置已保存；已有作品需核对并明确重新绑定新版配置。";
        await UpdateCredentialStatusAsync(); NotifyCommands();
    }
    [RelayCommand(CanExecute = nameof(CanNavigate))]
    private Task Refresh() => RunAsync(() => RefreshCoreAsync(_current?.Id));
    [RelayCommand(CanExecute = nameof(CanManage))]
    private void ResetConfiguration() { SecretInput = ""; Load(_current); Status = "未保存配置与密钥输入已放弃。"; }
    [RelayCommand(CanExecute = nameof(CanUseSaved))]
    private Task SaveSecret() => RunAsync(async () =>
    {
        var input = SecretInput; var generation = _secretGeneration;
        await _service.SetSecretAsync(ConnectionService.Bind(_current!), input, PersistSecret);
        if (_secretGeneration == generation) SecretInput = "";
        Status = "本次提交的密钥已保存。";
        await UpdateCredentialStatusAsync();
    });
    [RelayCommand(CanExecute = nameof(CanUseSaved))]
    private Task ClearSecret() => RunAsync(async () =>
    {
        var generation = _secretGeneration;
        await _service.ClearSecretAsync(ConnectionService.Bind(_current!));
        if (_secretGeneration == generation) SecretInput = "";
        await UpdateCredentialStatusAsync(); Status = "此连接密钥已清除，后续请求需要重新认证。";
    });
    [RelayCommand(CanExecute = nameof(CanManage))]
    private void ClearInput() { SecretInput = ""; Status = "仅清空尚未提交的密钥输入。"; }
    [RelayCommand(CanExecute = nameof(CanUseSaved))]
    private Task MakeDefault() => RunAsync(async () => { await _service.SetDefaultAsync(_current!.Id); DefaultStatus = "仅新书默认：" + _current.Settings.Name; });
    [RelayCommand(CanExecute = nameof(CanManage))]
    private Task ClearDefault() => RunAsync(async () => { await _service.SetDefaultAsync(null); DefaultStatus = "新书未设置默认连接"; });
    [RelayCommand(CanExecute = nameof(CanProbe))]
    private Task Probe() => RunAsync(async () =>
    {
        using var cancellation = new CancellationTokenSource(); _probeCancellation = cancellation; NotifyCommands();
        var budget = new RequestBudget(Guid.NewGuid(), 1, 200000);
        try
        {
            var frozen = await _service.FreezeAsync(ConnectionService.Bind(_current!), Guid.NewGuid(), ModelTask.Checking);
            var request = new TextModelRequest(Guid.NewGuid(), frozen, "只输出一句中文小说，不超过 30 个汉字。", "写一句雨夜书店的开场描写。", false);
            ProbeResult = "正在生成一句检测样本…";
            var response = await _requests.GenerateAsync(request, budget, null, cancellation.Token);
            ProbeResult = $"{(response.Completion == ModelCompletion.Complete ? "已连通" : "返回结果超过预设限制，需复核")}：{response.Text}\n输入 token：{response.Usage.InputTokens?.ToString() ?? "未知"}；输出 token：{response.Usage.OutputTokens?.ToString() ?? "未知"}。";
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            ProbeResult = "检测未完成。";
            var saved = _requests.List(budget.Id).LastOrDefault();
            if (saved?.PartialText.Length > 0) ProbeResult += " 已保留候选：" + saved.PartialText;
            throw;
        }
        finally
        {
            _probeCancellation = null; NotifyCommands();
            await UpdateCredentialStatusAsync();
        }
    });
    [RelayCommand(CanExecute = nameof(CanCancelProbe))]
    private void CancelProbe() => _probeCancellation?.Cancel();
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
        if (_closeTask is { IsCompleted: false }) return _closeTask;
        return _closeTask = PrepareCloseAsync();
    }
    private async Task<bool> PrepareCloseAsync()
    {
        _preparingClose = true; NotifyCommands();
        try
        {
            _probeCancellation?.Cancel();
            await _operation;
            if (SecretInput.Length > 0) { Status = "尚有未提交的密钥，请保存密钥或清空输入后关闭。"; return false; }
            if (IsDirty) await SaveCoreAsync();
            return !IsDirty && SecretInput.Length == 0;
        }
        catch (Exception exception) when (exception is not OutOfMemoryException) { Status = "连接配置未保存：" + exception.Message; return false; }
        finally { _preparingClose = false; NotifyCommands(); }
    }
    public void Dispose() { if (!_disposed) _ = _closeRegistration?.CloseAsync() ?? CloseCoreAsync(); }
    public ValueTask DisposeAsync() => _disposed ? ValueTask.CompletedTask : new(_closeRegistration?.CloseAsync() ?? CloseCoreAsync());
    private async Task CloseCoreAsync()
    {
        if (_disposed) return;
        if (!await SaveBeforeCloseAsync()) throw new IOException(Status);
        SecretInput = ""; _disposed = true; NotifyCommands();
    }
}
