using System.Collections.Immutable;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Domain.Analysis;

namespace NovelGeneratePlugin.Application.Analysis;

public sealed record IndexedMention(int Number, int Chunk, ReferenceMention Value);
public sealed record IndexedFinding(int Number, int Chunk, AnalysisFinding Value);

/// <summary>
/// 全书索引不做语义归并。编号只在当前版本内按正文顺序分配，完整下层结论与证据一直保留，专题压缩不能覆盖它们。
/// 模型后续只引用短整数编号，来源 ID、精确坐标和原句由本地沿索引取回。
/// </summary>
public sealed record NovelAnalysisIndex(ImmutableArray<IndexedMention> Mentions, ImmutableArray<IndexedFinding> Findings,
    ImmutableArray<ChunkAnalysisResult> Chunks)
{
    public static NovelAnalysisIndex Build(AnalysisRun run, ReferenceImport source, IReadOnlyDictionary<string, AnalysisNodeResult> results)
    {
        var mentions = ImmutableArray.CreateBuilder<IndexedMention>(); var findings = ImmutableArray.CreateBuilder<IndexedFinding>(); var chunks = ImmutableArray.CreateBuilder<ChunkAnalysisResult>();
        foreach (var chunk in run.Chunks)
        {
            var node = run.Nodes.Single(n => n.Kind == AnalysisNodeKind.Extraction && n.ChunkId == chunk.Id);
            if (node.State != AnalysisNodeState.Completed || !results.TryGetValue(node.Key, out var result) || result.InputStamp != node.InputStamp || result.Hash != AnalysisLimits.HashText(result.Json))
                throw new InvalidDataException("全书整合需要全部有效提取节点，缺少的章节不能被摘要代替。");
            var contract = new ChunkAnalysisContract(source.Source, chunk, node.OperationId, node.InputStamp, ChunkAnalysisContract.Passages(source.Source, chunk));
            ModelRequestService.ValidateJson(result.Json, contract); var extracted = contract.Read(result.Json); chunks.Add(extracted);
            foreach (var mention in extracted.Entities) mentions.Add(new(mentions.Count + 1, chunk.Number, mention));
            foreach (var finding in extracted.Findings) findings.Add(new(findings.Count + 1, chunk.Number, finding));
        }
        return new(mentions.ToImmutable(), findings.ToImmutable(), chunks.ToImmutable());
    }
}
