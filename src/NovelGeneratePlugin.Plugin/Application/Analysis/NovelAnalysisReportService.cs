using System.Collections.Immutable;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Domain.Analysis;

namespace NovelGeneratePlugin.Application.Analysis;

public sealed record NovelReportPart(AnalysisDimension Dimension, NovelReportDraft Draft);
public sealed record NovelAnalysisReport(Guid RunId, string Version, string Name, string FileName, Guid SourceId, string ByteHash, string TextHash,
    int SourceCharacters, int CoveredCharacters, bool IsComplete, string QualityStatus, string Model, ImmutableArray<NovelReportPart> Parts,
    NovelReportDraft? Synthesis, NovelIntegrationSnapshot Integrated, ImmutableArray<ReferenceSection> Sections);
public sealed record AnalysisEvidenceLocation(string Section, int Number, int AbsoluteStart, int Length, string Excerpt, int SelectionStart, string SourceHash);

/// <summary>
/// 报告由已保存节点派生，不依赖模型在线或原 TXT 路径。每次阅读核对来源、节点哈希、依赖与引用；缺失专题只显示为部分报告。
/// QualityStatus 固定为候选，结构成功不能冒充人工准确率、主要人物召回或全文语义质量通过。
/// </summary>
public sealed class NovelAnalysisReportService(IReferenceSourceStore sources, IAnalysisRunStore runs)
{
    public NovelAnalysisReport Read(Guid runId)
    {
        var run = runs.Read(runId); var input = sources.Read(run.BookId) with { Chunks = run.Chunks }; input.Validate();
        if (run.SourceId != input.Source.Id || run.SourceHash != input.Source.TextHash) throw new InvalidDataException("报告来源版本不一致。");
        if (run.Target != AnalysisTarget.Report || run.Nodes.Where(n => n.Kind is AnalysisNodeKind.Extraction or AnalysisNodeKind.Integration).Any(n => n.State != AnalysisNodeState.Completed))
            throw new InvalidOperationException("全文提取与整合尚未齐全，不能创建报告候选。");
        var results = run.Nodes.Where(n => n.State == AnalysisNodeState.Completed).ToDictionary(n => n.Key, n =>
        {
            var result = runs.ReadResult(runId, n.Key) ?? throw new InvalidDataException("已完成节点缺少结果。");
            if (result.InputStamp != n.InputStamp || result.Hash != AnalysisLimits.HashText(result.Json)) throw new InvalidDataException("节点结果与保存版本不一致。");
            return result;
        });
        var index = NovelAnalysisIndex.Build(run, input, results); var integrated = NovelIntegrationSnapshot.Build(run, index, results);
        var parts = ImmutableArray.CreateBuilder<NovelReportPart>(); NovelReportDraft? synthesis = null;
        foreach (var node in run.Nodes.Where(n => n.State == AnalysisNodeState.Completed && n.Kind is AnalysisNodeKind.Summary or AnalysisNodeKind.Dimension or AnalysisNodeKind.Synthesis))
        {
            if (node.Dependencies.Any(key => !results.ContainsKey(key))) throw new InvalidDataException("报告前置结果缺失。");
            // 离线阅读使用持久化的材料选择和结果身份，不以新提示覆盖历史报告；当前契约仍核对全部引用范围。
            var prepared = NovelReportRequests.Prepare(run, input, node, results); var contract = (NovelReportContract)prepared.Request.Contract!;
            var draft = contract.Read(results[node.Key].Json);
            if (node.Kind == AnalysisNodeKind.Dimension) parts.Add(new(node.Dimension!.Value, draft));
            if (node.Kind == AnalysisNodeKind.Synthesis) synthesis = draft;
        }
        if (parts.Select(p => p.Dimension).Distinct().Count() != parts.Count) throw new InvalidDataException("专题节点重复。");
        var covered = run.Chunks.Where(c => run.Nodes.Any(n => n.ChunkId == c.Id && n.State == AnalysisNodeState.Completed)).Sum(c => c.Body.Length);
        var complete = run.State == AnalysisRunState.Completed && covered == input.Source.Text.Length && parts.Count == 6 && synthesis is not null && run.Nodes.All(n => n.State == AnalysisNodeState.Completed);
        var version = CanonicalJson.Hash(new { run.SourceId, run.SourceHash, run.Connection, Results = run.Nodes.Where(n => results.ContainsKey(n.Key)).Select(n => new { n.Key, n.InputStamp, results[n.Key].Hash }).ToArray() });
        return new(runId, version, input.Book.Name, input.Source.FileName, input.Source.Id, input.Source.ByteHash, input.Source.TextHash, input.Source.Text.Length, covered,
            complete, "AI 分析候选；语义准确率、召回率与文风评价待人工评阅", run.Connection.Preset.Model, parts.ToImmutable(), synthesis, integrated, input.Sections);
    }

    public AnalysisEvidenceLocation Locate(Guid runId, AnalysisEvidence evidence)
    {
        var run = runs.Read(runId); var source = sources.Read(run.BookId); evidence.Validate(source.Source);
        if (source.Source.Id != run.SourceId || source.Source.TextHash != run.SourceHash) throw new InvalidDataException("定位来源与报告不一致。");
        var section = source.Sections.Single(s => s.Range.Start <= evidence.Range.Start && s.Range.End > evidence.Range.Start);
        var start = Math.Max(0, evidence.Range.Start - 1000); var end = Math.Min(source.Source.Text.Length, evidence.Range.End + 1000);
        if (start > 0 && char.IsLowSurrogate(source.Source.Text[start]) && char.IsHighSurrogate(source.Source.Text[start - 1])) start--;
        if (end < source.Source.Text.Length && char.IsHighSurrogate(source.Source.Text[end - 1]) && char.IsLowSurrogate(source.Source.Text[end])) end++;
        return new(section.Title, section.Number, evidence.Range.Start, evidence.Range.Length, source.Source.Text[start..end], evidence.Range.Start - start, source.Source.TextHash);
    }
}
