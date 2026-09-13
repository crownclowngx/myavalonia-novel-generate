using System.Text.Json;

namespace NovelGeneratePlugin.Application.Models;

public enum ModelContractRule { Required, ArrayCount, TextLength, EnumValue, PassageNumber, DimensionCoverage }

/// <summary>
/// 业务校验只传固定规则和数量，不把模型的字段内容、引文或异常正文作为诊断输出。
/// 与 JSON 解析失败分开，便于界面展示以及下一次有界纠正引用同一条约束。
/// </summary>
public sealed record ModelContractIssue(ModelContractRule Rule, int? Actual = null, int? Minimum = null, int? Maximum = null)
{
    public string Description => Rule switch
    {
        ModelContractRule.Required => "必填字段不能为空或仅含空白。",
        ModelContractRule.ArrayCount => $"条目数量须为 {Minimum}–{Maximum}，实际 {Actual}。",
        ModelContractRule.TextLength => $"文本长度须为 {Minimum}–{Maximum} 字符，实际 {Actual}。",
        ModelContractRule.EnumValue => "字段取值必须属于契约声明的枚举。",
        ModelContractRule.PassageNumber => $"引用段号须为 {Minimum}–{Maximum}，实际 {Actual}。",
        ModelContractRule.DimensionCoverage => $"六个分析维度必须有结论或缺口说明，实际覆盖 {Actual}/6。",
        _ => "业务字段不符合契约。"
    };
}

/// <summary>
/// 路径由本地契约按固定字段及循环序号构造。模型请求边界仍会对白名单路径再做校验，
/// 因而扩展其他契约时也不能把服务端构造的任意属性名透传到界面或纠正提示。
/// </summary>
public sealed class ModelContractException(string path, ModelContractIssue issue) : JsonException(issue.Description, path, null, null)
{
    public ModelContractIssue Issue { get; } = issue;
}
