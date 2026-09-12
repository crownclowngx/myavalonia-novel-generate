using NovelGeneratePlugin.Domain;
namespace NovelGeneratePlugin.Application.Models;

public sealed record TextModelRequest(Guid OperationId, FrozenConnection Configuration, string SystemPrompt, string UserPrompt, bool JsonOutput);
public enum ModelCompletion { Complete, Truncated }
public sealed record ModelUsage(long? InputTokens, long? OutputTokens);
public sealed record TextModelResponse(string Text, ModelCompletion Completion, ModelUsage Usage);
/// <summary>模型只返回文本候选，不取得数据库或文件操作权限。未知用量为 null，不把缺失计数伪装成零。</summary>
public interface ITextModel
{
    Task<TextModelResponse> GenerateAsync(TextModelRequest request, IProgress<string>? progress, CancellationToken cancellationToken);
}
