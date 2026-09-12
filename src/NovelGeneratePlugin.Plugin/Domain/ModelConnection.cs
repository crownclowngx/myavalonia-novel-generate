namespace NovelGeneratePlugin.Domain;

public enum ModelProvider { CodexCli, DeepSeek }
public enum ModelTask { Planning, Drafting, Checking }
/// <summary>预设仅保存任务参数。服务商协议负责支持能力校验，不能默默忽略不支持的参数。</summary>
public sealed record ModelPreset(string Model, int MaxOutputTokens, string ReasoningEffort)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Model) || Model.Length > 120 || Model.Any(char.IsControl) ||
            MaxOutputTokens is < 256 or > 131072 || ReasoningEffort is not ("none" or "low" or "medium" or "high" or "max"))
            throw new InvalidDataException("模型预设无效：需填写模型，输出上限为 256–131072，推理强度为 none/low/medium/high/max。");
    }
}
public sealed record ConnectionSettings(string Name, ModelProvider Provider, string Endpoint, string CodexExecutable,
    ModelPreset Planning, ModelPreset Drafting, ModelPreset Checking)
{
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(Name) || Name.Length > 120 || !Enum.IsDefined(Provider) || Endpoint is null || CodexExecutable is null ||
            Planning is null || Drafting is null || Checking is null) throw new InvalidDataException("连接名称、类型或预设无效。");
        Planning.Validate(); Drafting.Validate(); Checking.Validate();
        // DeepSeek 的关闭思考和最大思考不能透传到现有 Codex 适配器；按服务商验证，避免界面切换留下非法参数。
        if (Provider == ModelProvider.CodexCli && new[] { Planning, Drafting, Checking }.Any(p => p.ReasoningEffort is "none" or "max"))
            throw new InvalidDataException("当前 Codex 连接支持 low/medium/high 推理强度。");
        if (Provider == ModelProvider.DeepSeek)
        {
            if (!Uri.TryCreate(Endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps ||
                !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
                throw new InvalidDataException("API 端点必须是 HTTPS 地址，不能包含用户名、密码、查询或片段。");
            if (CodexExecutable.Length != 0) throw new InvalidDataException("API 连接不保存 CLI 路径。");
        }
        else if (Endpoint.Length != 0 || !Path.IsPathFullyQualified(CodexExecutable) || !CodexExecutable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Codex 连接需要明确的 exe 完整路径，不使用 API 端点或命令字符串。");
    }
    public string AuthorizationEndpoint => Provider == ModelProvider.DeepSeek ? new Uri(Endpoint).AbsoluteUri.TrimEnd('/') : CodexExecutable;
    public ModelPreset Preset(ModelTask task) => task switch { ModelTask.Planning => Planning, ModelTask.Drafting => Drafting, ModelTask.Checking => Checking, _ => throw new ArgumentOutOfRangeException(nameof(task)) };
}
/// <summary>CredentialEpoch 随认证目标变化重新生成。端点改回原值也不能复活旧密钥或旧运行授权。</summary>
public sealed record ModelConnection(Guid Id, long Version, Guid CredentialEpoch, ConnectionSettings Settings)
{
    public void Validate()
    {
        if (Id == Guid.Empty || Version < 1 || CredentialEpoch == Guid.Empty || Settings is null) throw new InvalidDataException("连接身份或版本无效。");
        Settings.Validate();
    }
    public override string ToString() => $"{Settings.Name} · v{Version}";
}
public sealed record ConnectionBinding(Guid ConnectionId, long Version, string Name);
public sealed record ConnectionCatalog(IReadOnlyList<ModelConnection> Connections, Guid? DefaultConnectionId);
public sealed record FrozenConnection(Guid BookId, ModelConnection Connection, ModelTask Task, ModelPreset Preset);
