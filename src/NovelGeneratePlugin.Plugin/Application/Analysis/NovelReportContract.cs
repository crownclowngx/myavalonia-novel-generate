using System.Collections.Immutable;
using System.Text.Json;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Domain.Analysis;

namespace NovelGeneratePlugin.Application.Analysis;

public sealed record NovelReportClaim(string Heading, string Text, AnalysisStatementKind Certainty, ImmutableArray<int> Facts);
public sealed record NovelReportDraft(string Title, ImmutableArray<NovelReportClaim> Claims, ImmutableArray<string> OpenQuestions);

/// <summary>
/// 各级摘要、专题及综合使用同一窄结果结构：每条结论都能沿事实编号回到原文，正文不接受模型生成的引用坐标。
/// 限制整个输出长度，保证下一层至少能容纳两个子节点，避免有界汇总树无法继续收敛。
/// </summary>
public sealed class NovelReportContract(IReadOnlyList<IndexedFinding> available, IReadOnlySet<int>? languageSamples = null) : IModelOutputContract
{
    public const string Version = "novel-report-v1";
    public void Validate(JsonElement value) => Read(value.GetRawText());
    public NovelReportDraft Read(string json)
    {
        if (json.Length > 32000) throw new InvalidDataException("报告节点超过有界汇总容量。");
        var output = AnalysisJson.Read<NovelReportDraft>(json); AnalysisJson.Text(output.Title, 150);
        if (output.Claims.IsDefault || output.Claims.Length > 16 || output.OpenQuestions.IsDefault || output.OpenQuestions.Length > 12 || output.Claims.IsEmpty && output.OpenQuestions.IsEmpty)
            throw new InvalidDataException("报告需提供结论或明确缺口，且不能超过节点容量。");
        foreach (var question in output.OpenQuestions) AnalysisJson.Text(question, 500);
        var facts = available.ToDictionary(f => f.Number);
        foreach (var claim in output.Claims)
        {
            if (claim is null || !Enum.IsDefined(claim.Certainty)) throw new InvalidDataException("报告判断类型无效。");
            AnalysisJson.Text(claim.Heading, 120); AnalysisJson.Text(claim.Text, 1200); AnalysisJson.References(claim.Facts, facts.Keys, 16);
            if (languageSamples is not null && !claim.Facts.Any(languageSamples.Contains)) throw new InvalidDataException("文风结论必须直接引用本轮提供的语言样本。");
            var cited = claim.Facts.Select(id => facts[id].Value).ToArray();
            if (claim.Certainty == AnalysisStatementKind.Explicit && (cited.All(f => f.Kind != AnalysisStatementKind.Explicit) || cited.Any(f => f.Kind == AnalysisStatementKind.Uncertain)))
                throw new InvalidDataException("报告不能把未确认或纯推断依据升级为确定结论。");
        }
        return output;
    }
    public string JsonSchema => AnalysisJson.Schema(new()
    {
        ["Title"] = AnalysisJson.String,
        ["Claims"] = AnalysisJson.Array(AnalysisJson.Object(new()
        {
            ["Heading"] = AnalysisJson.String,
            ["Text"] = AnalysisJson.String,
            ["Certainty"] = AnalysisJson.Enum<AnalysisStatementKind>(),
            ["Facts"] = AnalysisJson.Array(AnalysisJson.Integer)
        })),
        ["OpenQuestions"] = AnalysisJson.Array(AnalysisJson.String)
    });
}
