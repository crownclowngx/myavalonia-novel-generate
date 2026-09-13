using System.Collections.Immutable;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Domain.Analysis;

namespace NovelGeneratePlugin.Application.Analysis;

public sealed record AnalysisCapacityOptions(long? ContextTokens = null, int MaximumSplitDepth = 3, int MinimumBodyCharacters = 200)
{
    public void Validate()
    {
        if (ContextTokens is < 8192 or > 2000000 || MaximumSplitDepth is < 0 or > 8 || MinimumBodyCharacters is < 200 or > 6000)
            throw new InvalidDataException("容量或拆分设置无效：最多 0–8 层，每个子单元至少 200–6000 字符。");
    }
}

/// <summary>被替换的父节点及区间保存在运行历史中，费用仍通过原操作 ID 查询，不计入有效正文覆盖。</summary>
public sealed record AnalysisSplit(AnalysisNode Parent, AnalysisChunk Original, ImmutableArray<AnalysisChunk> Children,
    int Depth, ModelDiagnosticCode Reason);

/// <summary>存储边界核对局部替换，不允许借拆分改写成功节点、来源或其他章节。</summary>
public static class AnalysisSplitRules
{
    public static void ValidateTransition(AnalysisRun before, AnalysisRun after)
    {
        if (after.Splits.Length != before.Splits.Length + 1 || CanonicalJson.Hash(after.Splits.Take(before.Splits.Length).ToArray()) != CanonicalJson.Hash(before.Splits))
            throw new InvalidDataException("拆分历史只能追加一次。");
        var split = after.Splits[^1];
        var parent = before.Nodes.SingleOrDefault(n => n.Key == split.Parent.Key);
        var original = before.Chunks.SingleOrDefault(c => c.Id == split.Original.Id);
        if (parent is null || original is null || CanonicalJson.Hash(parent) != CanonicalJson.Hash(split.Parent) || original != split.Original ||
            parent.Kind != AnalysisNodeKind.Extraction || parent.State == AnalysisNodeState.Completed || parent.ChunkId != original.Id ||
            before.Nodes.Any(n => n.Kind != AnalysisNodeKind.Extraction) || split.Children.Length != 2 ||
            split.Children[0].Body.Start != original.Body.Start || split.Children[0].Body.End != split.Children[1].Body.Start ||
            split.Children[1].Body.End != original.Body.End || split.Children.Any(c => c.SectionId != original.SectionId || !original.Context.Contains(c.Context) || !c.Context.Contains(c.Body)))
            throw new InvalidDataException("拆分必须完整替换一个尚未完成的正文单元。");
        var expectedDepth = before.Splits.FirstOrDefault(s => s.Children.Any(c => c.Id == original.Id))?.Depth + 1 ?? 1;
        var options = before.Capacity ?? new();
        if (split.Depth != expectedDepth || split.Depth > options.MaximumSplitDepth ||
            split.Children.Any(c => c.Body.Length < options.MinimumBodyCharacters || c.Id == Guid.Empty || before.Chunks.Any(old => old.Id == c.Id)))
            throw new InvalidDataException("拆分深度、最小区间或身份无效。");
        var expected = before.Chunks.SelectMany(c => c.Id == original.Id ? split.Children.AsEnumerable() : [c])
            .Select((c, i) => c with { Number = i + 1 }).ToArray();
        if (!expected.SequenceEqual(after.Chunks) || after.Nodes.Length != before.Nodes.Length + 1) throw new InvalidDataException("拆分改变了其他来源区间。");
        var childIds = split.Children.Select(c => c.Id).ToHashSet();
        foreach (var node in before.Nodes.Where(n => n.Key != parent.Key))
            if (!after.Nodes.Any(n => n.Key == node.Key && CanonicalJson.Hash(n) == CanonicalJson.Hash(node)))
                throw new InvalidDataException("拆分不能改变其他节点。");
        if (after.Nodes.Any(n => n.Key == parent.Key) ||
            after.Nodes.Where(n => childIds.Contains(n.ChunkId ?? Guid.Empty)).Any(n => n.State != AnalysisNodeState.Pending || n.ExecutionConnection != parent.ExecutionConnection ||
                n.ExecutionPreset != parent.ExecutionPreset || n.ExtractionPromptVersion != parent.ExtractionPromptVersion || n.ReviewGuidance != parent.ReviewGuidance))
            throw new InvalidDataException("拆分子节点必须继承已冻结配置并从未执行状态开始。");
    }
}
