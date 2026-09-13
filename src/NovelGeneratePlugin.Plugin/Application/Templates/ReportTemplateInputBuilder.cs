using System.Collections.Immutable;
using NovelGeneratePlugin.Application.Analysis;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Domain.Analysis;

namespace NovelGeneratePlugin.Application.Templates;

/// <summary>
/// 报告是已分析的资料，不是可执行提示。六专题和综合结论按用途进入冻结快照；
/// 每条只保留两段有限证据，原报告中的疑问明确降为建议，不能在转换时变成确定事实。
/// </summary>
public static class ReportTemplateInputBuilder
{
    public static ReportTemplateSource Build(NovelAnalysisReport report, Guid bookId, ProfileDimensions dimensions)
    {
        if (!report.IsComplete) throw new InvalidOperationException("当前是部分报告，请先完成全文分析和六个专题及综合结论。");
        if (!ConversionLimits.Dimensions(dimensions)) throw new InvalidOperationException("至少选择一个模板维度。");
        var claims = ImmutableArray.CreateBuilder<ReportTemplateClaim>(); var notes = ImmutableArray.CreateBuilder<string>();
        var facts = report.Integrated.Index.Findings.ToDictionary(f => f.Number);
        void Add(NovelReportDraft draft, string topic, ProfileDimensions targets)
        {
            targets &= dimensions; if (targets == ProfileDimensions.None) return;
            foreach (var claim in draft.Claims)
            {
                var evidence = claim.Facts.SelectMany(id => facts[id].Value.Evidence.Select(e => new ReportTemplateEvidence(id, e.Quote)))
                    .Where(e => e.Quote.Length <= 400).Distinct().Take(2).ToImmutableArray();
                var basis = claim.Certainty switch { AnalysisStatementKind.Explicit => TemplateRuleBasis.Supported, AnalysisStatementKind.Inferred => TemplateRuleBasis.Inferred, _ => TemplateRuleBasis.Suggestion };
                claims.Add(new(claims.Count + 1, targets, topic, claim.Heading + "\n" + claim.Text, basis, evidence));
            }
            foreach (var question in draft.OpenQuestions)
                claims.Add(new(claims.Count + 1, targets, topic + " · 待确认", question, TemplateRuleBasis.Suggestion, []));
        }
        foreach (var part in report.Parts.OrderBy(p => p.Dimension))
        {
            var targets = part.Dimension switch
            {
                AnalysisDimension.World => ProfileDimensions.World | ProfileDimensions.Rules,
                AnalysisDimension.Characters => ProfileDimensions.World | ProfileDimensions.Methods,
                AnalysisDimension.Goals or AnalysisDimension.Plot => ProfileDimensions.Methods,
                AnalysisDimension.Style => ProfileDimensions.Style | ProfileDimensions.Rules,
                _ => ProfileDimensions.World | ProfileDimensions.Methods | ProfileDimensions.Rules
            };
            if ((targets & dimensions) == 0) notes.Add($"未选用专题：{NovelReportRequests.Title(part.Dimension)}，与本次勾选维度无对应关系。");
            Add(part.Draft, NovelReportRequests.Title(part.Dimension), targets);
        }
        Add(report.Synthesis!, "综合结论", dimensions);
        notes.Add("材料包含所选专题和综合结论的全部结论/疑问；每条最多保留两段不超过 400 字符的关联原文证据，其余证据可回到原报告查看。");
        notes.Add("完整指已导入 TXT 的处理覆盖，不表示原著完整性或人工文学质量已经通过。");
        if (claims.Count == 0)
            claims.Add(new(1, dimensions, "报告缺口", "所选报告没有可用结论，请仅说明缺口，不虚构创作要求。", TemplateRuleBasis.Suggestion, []));
        var source = new ReportTemplateSource(bookId, report.RunId, report.SourceId, report.TextHash, report.Version, report.Name, claims.ToImmutable(), notes.ToImmutable());
        source.Validate(); return source;
    }
}
