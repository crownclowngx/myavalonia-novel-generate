using System.Text;
using NovelGeneratePlugin.Domain;

namespace NovelGeneratePlugin.Application.Models;

public sealed record ModelInputEstimate(long EstimatedInputTokens, int MaximumOutputTokens, long? ContextTokens, int PromptCharacters)
{
    public long TotalReservation => checked(EstimatedInputTokens + MaximumOutputTokens);
    public bool Exceeded => PromptCharacters > 250000 || ContextTokens is long limit && TotalReservation > limit;
}

/// <summary>
/// UTF-8 字节数加适配余量用于保守估算，不冒充精确 tokenizer，也不拿累计预算当上下文窗口。
/// 已核对 DeepSeek Flash/V4 的窗口为 1M；其他模型只有显式配置时才检查远端窗口。
/// </summary>
public static class ModelInputCapacity
{
    public static ModelInputEstimate Estimate(TextModelRequest request)
    {
        var schema = request.Contract?.JsonSchema ?? "";
        long? known = request.Configuration.Connection.Settings.Provider == ModelProvider.DeepSeek &&
            request.EffectivePreset.Model is "deepseek-flash" or "deepseek-v4-flash" or "deepseek-v4-pro" ? 1000000L : null;
        var context = request.ContextTokenLimit is long configured ? known is long ceiling ? Math.Min(configured, ceiling) : configured : known;
        return new((long)Encoding.UTF8.GetByteCount(request.SystemPrompt) + Encoding.UTF8.GetByteCount(request.UserPrompt) +
            Encoding.UTF8.GetByteCount(schema) + 16384, request.EffectivePreset.MaxOutputTokens, context,
            checked(request.SystemPrompt.Length + request.UserPrompt.Length + schema.Length));
    }
    public static void Check(TextModelRequest request)
    {
        if (request.ContextTokenLimit is < 8192 or > 2000000) throw new InvalidDataException("单次上下文容量需为 8192–2000000 token。");
        if (!Estimate(request).Exceeded) return;
        var diagnostic = new ModelDiagnostic(ModelDiagnosticCode.InputLimit);
        throw new ModelRequestException(ModelFailure.Protocol, diagnostic.Message) { Diagnostic = diagnostic };
    }
}
