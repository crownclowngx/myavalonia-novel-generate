using System.Collections.Immutable;

namespace NovelGeneratePlugin.Domain.Analysis;

public enum AnalysisDimension { World, Characters, Goals, Plot, Style, Theme }
public enum AnalysisStatementKind { Explicit, Inferred, Uncertain }

/// <summary>
/// 证据携带来源版本、精确区间及原文，重复出现的句子也能定位到模型选用的那一处。
/// 校验只证明引用确实存在；它不证明“引用支持结论”，后者需专题检查和人工质量验收。
/// </summary>
public sealed record AnalysisEvidence(Guid SourceId, string TextHash, SourceRange Range, string Quote)
{
    public void Validate(SourceSnapshot source, SourceRange? allowed = null)
    {
        if (SourceId != source.Id || TextHash != source.TextHash || Range is null || string.IsNullOrWhiteSpace(Quote) || Quote.Length > 2000)
            throw new InvalidDataException("分析证据来源已变化或引用为空。");
        Range.Validate(source.Text);
        if (allowed is not null && !allowed.Contains(Range) || source.Text.AsSpan(Range.Start, Range.Length).SequenceEqual(Quote.AsSpan()) == false)
            throw new InvalidDataException("分析证据不在允许的输入区间内，或与原文不一致。");
    }
}

/// <summary>每条结论保留判断类型。未知是有效结果，不允许用空字段或模型自行编造的事实填补信息缺口。</summary>
public sealed record AnalysisFinding(Guid Id, AnalysisDimension Dimension, string Subject, string Statement,
    AnalysisStatementKind Kind, ImmutableArray<AnalysisEvidence> Evidence)
{
    public void Validate(SourceSnapshot source, SourceRange? allowed = null)
    {
        if (Id == Guid.Empty || !Enum.IsDefined(Dimension) || !Enum.IsDefined(Kind) || string.IsNullOrWhiteSpace(Subject) || Subject.Length > 200 ||
            string.IsNullOrWhiteSpace(Statement) || Statement.Length > 4000 || Evidence.IsDefaultOrEmpty || Evidence.Length > 8)
            throw new InvalidDataException("分析结论缺少维度、判断类型或有界证据。");
        foreach (var evidence in Evidence)
        {
            if (evidence is null) throw new InvalidDataException("分析证据不能为空。");
            evidence.Validate(source, allowed);
        }
    }
}

/// <summary>
/// 报告首先是一份带来源的候选；完整性由运行用例根据已完成节点和正文覆盖计算，不能接受模型自报“全文完成”。
/// 此处只固定结果结构，节点调度与专题汇总分别由后续应用服务负责。
/// </summary>
public sealed record AnalysisSection(AnalysisDimension Dimension, string Summary, ImmutableArray<AnalysisFinding> Findings);
