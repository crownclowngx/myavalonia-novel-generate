using System.Text.Json;
using System.Text.RegularExpressions;

namespace NovelGeneratePlugin.Application.Models;

public enum ModelDiagnosticCode { InvalidJson, SchemaEcho, ContractMismatch, StreamIncomplete, StreamLimit, UnsupportedFinish, OutputLimit, InputLimit, BudgetLimit, Timeout, Cancelled, Service }

/// <summary>取消仍按原有取消语义传播，同时携带已经收到的用量；不把取消误报为协议失败。</summary>
public sealed class ModelStreamCancellationException(ModelUsage usage, CancellationToken token) : OperationCanceledException(token)
{
    public ModelUsage ObservedUsage { get; } = usage;
}

/// <summary>
/// 诊断只保存固定分类、经过白名单处理的字段路径和数字，不保存服务端错误正文、推理或密钥。
/// 完整性与用量独立：收到 usage 后断流仍可能产生费用，但不能把半份结果当作成功候选。
/// </summary>
public sealed record ModelDiagnostic(ModelDiagnosticCode Code, string Path = "", string? FinishReason = null,
    long StreamCharacters = 0, long OutputCharacters = 0, bool ResponseComplete = false)
{
    public string Message => Code switch
    {
        ModelDiagnosticCode.SchemaEcho => "模型返回了 JSON 格式定义；需要返回一个分析结果对象。",
        ModelDiagnosticCode.InvalidJson => "响应不是单个完整 JSON 对象；请检查多余内容或字符串截断。",
        ModelDiagnosticCode.ContractMismatch => $"结构化字段、枚举、引用或容量不符合契约{(Path.Length == 0 ? "" : "，位置：" + Path)}。",
        ModelDiagnosticCode.StreamIncomplete => "模型响应流未完整结束；保留候选和已收到用量，需复核后继续。",
        ModelDiagnosticCode.StreamLimit => "模型响应流达到本地容量限制；请降低输出规模或思考强度。",
        ModelDiagnosticCode.UnsupportedFinish => "模型以非正常原因结束；请复核候选及用量。",
        ModelDiagnosticCode.OutputLimit => "模型输出达到长度限制；可缩小问题单元或调整输出参数。",
        ModelDiagnosticCode.InputLimit => "输入估算与输出预留超过单次容量；需要缩小问题单元。",
        ModelDiagnosticCode.BudgetLimit => "累计预算不足，需保留后续报告额度后再继续。",
        ModelDiagnosticCode.Timeout => "请求超时；候选和已收到用量保留。",
        ModelDiagnosticCode.Cancelled => "请求已取消；候选和已收到用量保留。",
        _ => "模型服务拒绝或中断请求；请检查连接及服务状态。"
    };
    public bool CanCorrect => ResponseComplete && Code is ModelDiagnosticCode.InvalidJson or ModelDiagnosticCode.SchemaEcho or ModelDiagnosticCode.ContractMismatch;
}

/// <summary>从契约允许的字段生成路径白名单；模型构造的任意属性名不得通过异常进入日志。</summary>
internal static partial class ModelJsonDiagnostics
{
    public static string SafePath(string? path, string? schema)
    {
        if (path is null || path.Length > 300 || schema is null) return "";
        using var json = JsonDocument.Parse(schema);
        var names = new HashSet<string>(StringComparer.Ordinal) { "Evidence" };
        Collect(json.RootElement, names);
        if (!path.StartsWith('$')) return "";
        var result = new System.Text.StringBuilder("$"); var position = 1;
        foreach (Match part in PathPart().Matches(path))
        {
            if (part.Index != position) return "$";
            result.Append(part.Value[0] == '[' || names.Contains(part.Value[1..]) ? part.Value : ".未知字段");
            position += part.Length;
        }
        return position == path.Length ? result.ToString() : "$";
    }
    private static void Collect(JsonElement element, HashSet<string> names)
    {
        if (element.ValueKind != JsonValueKind.Object) return;
        if (element.TryGetProperty("properties", out var properties))
            foreach (var property in properties.EnumerateObject()) { names.Add(property.Name); Collect(property.Value, names); }
        if (element.TryGetProperty("items", out var items)) Collect(items, names);
    }
    [GeneratedRegex(@"\.[A-Za-z][A-Za-z0-9]*|\[[0-9]{1,6}\]")]
    private static partial Regex PathPart();

    public static bool IsSchemaEcho(string text)
    {
        try
        {
            var reader = new Utf8JsonReader(System.Text.Encoding.UTF8.GetBytes(text));
            using var first = JsonDocument.ParseValue(ref reader);
            var value = first.RootElement;
            return value.ValueKind == JsonValueKind.Object && value.TryGetProperty("properties", out _) &&
                value.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() is "object" or "json_object";
        }
        catch (JsonException) { return false; }
    }
}
