using NovelGeneratePlugin.Domain;
namespace NovelGeneratePlugin.Application.Models;

public sealed record TextModelRequest(Guid OperationId, FrozenConnection Configuration, string SystemPrompt, string UserPrompt, bool JsonOutput)
{
    public IModelOutputContract? Contract { get; init; }
    public bool AllowJsonWrapperRepair { get; init; }
    public ModelPreset? ExecutionPreset { get; init; }
    public ModelPreset EffectivePreset => ExecutionPreset ?? Configuration.Preset;
    public void Validate()
    {
        if (OperationId == Guid.Empty || Configuration is null || Configuration.BookId == Guid.Empty ||
            string.IsNullOrWhiteSpace(SystemPrompt) || string.IsNullOrWhiteSpace(UserPrompt) ||
            SystemPrompt.Length + UserPrompt.Length > 250000) throw new InvalidDataException("模型请求身份或提示内容无效（总长度上限 25 万字符）。");
        Configuration.Connection.Validate(); Configuration.Preset.Validate();
        EffectivePreset.Validate();
        if (ExecutionPreset is not null && Configuration.Task != ModelTask.Checking)
            throw new InvalidDataException("独立阶段参数仅用于分析检查任务。");
        if (Configuration.Connection.Settings.Provider == ModelProvider.CodexCli && EffectivePreset.ReasoningEffort is "none" or "max")
            throw new InvalidDataException("当前 Codex 任务不支持该思考强度。");
        if (Configuration.Preset != Configuration.Connection.Settings.Preset(Configuration.Task)) throw new InvalidDataException("请求预设与冻结连接不一致。");
        if ((Contract is not null || AllowJsonWrapperRepair) && !JsonOutput) throw new InvalidDataException("结构化契约必须使用 JSON 输出。");
        if (Contract is not null)
        {
            if (string.IsNullOrWhiteSpace(Contract.JsonSchema) || SystemPrompt.Length + UserPrompt.Length + Contract.JsonSchema.Length > 250000)
                throw new InvalidDataException("结构化契约为空或使请求超过本地输入容量。");
            using var schema = System.Text.Json.JsonDocument.Parse(Contract.JsonSchema, new System.Text.Json.JsonDocumentOptions { MaxDepth = 32 });
            if (schema.RootElement.ValueKind != System.Text.Json.JsonValueKind.Object) throw new InvalidDataException("结构化契约必须是 JSON Schema 对象。");
        }
    }
}
public enum ModelCompletion { Complete, Truncated }
public sealed record ModelUsage(long? InputTokens, long? OutputTokens);
public sealed record TextModelResponse(string Text, ModelCompletion Completion, ModelUsage Usage)
{
    public ModelDiagnostic? Diagnostic { get; init; }
}
/// <summary>由具体任务定义字段、类型和取值边界；服务商的 JSON 模式不能替代业务校验。</summary>
public interface IModelOutputContract
{
    string JsonSchema { get; }
    void Validate(System.Text.Json.JsonElement value);
}
public enum ModelFailure { Authentication, Balance, RateLimit, Service, Protocol, Local, Timeout, Cancelled }
/// <summary>只允许固定的脱敏错误说明通过模型边界；不把 HTTP 响应体、stderr 或密钥拼接进 UI 异常。</summary>
public sealed class ModelRequestException(ModelFailure failure, string message) : Exception(message)
{
    public ModelFailure Failure { get; } = failure;
    public ModelDiagnostic? Diagnostic { get; init; }
    public ModelUsage? ObservedUsage { get; init; }
}
/// <summary>模型只返回文本候选，不取得数据库或文件操作权限。未知用量为 null，不把缺失计数伪装成零。</summary>
public interface ITextModel
{
    Task<TextModelResponse> GenerateAsync(TextModelRequest request, IProgress<string>? progress, CancellationToken cancellationToken);
}
