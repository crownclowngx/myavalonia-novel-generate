using System.Collections.Immutable;
using NovelGeneratePlugin.Domain.Analysis;

namespace NovelGeneratePlugin.Application.Analysis;

/// <summary>
/// 按实际输出逐层创建汇总树，父节点只读取已完成子节点；所有层次复用同一预算和持久化调度。
/// 不把全文摘要塞进一次无限长请求，也不丢弃下层索引。专题抽取固定规则的前中后期样本，选择编号随节点一起保存。
/// </summary>
public static class NovelReportPlanner
{
    public static ImmutableArray<AnalysisNode> Next(AnalysisRun run, NovelIntegrationSnapshot integrated, IReadOnlyDictionary<string, AnalysisNodeResult> results)
    {
        var summaries = run.Nodes.Where(n => n.Kind == AnalysisNodeKind.Summary).ToArray();
        if (summaries.Length == 0)
        {
            var groups = new List<ImmutableArray<int>>(); var current = new List<int>();
            foreach (var finding in integrated.Index.Findings)
            {
                var candidate = current.Append(finding.Number).ToImmutableArray();
                if (current.Count > 0 && (candidate.Length > 80 || AnalysisJson.Write(NovelReportMaterial.Leaf(integrated, candidate)).Length > 65000))
                { groups.Add([.. current]); current.Clear(); }
                current.Add(finding.Number);
            }
            if (current.Count > 0) groups.Add([.. current]);
            if (groups.Count == 0) groups.Add([]);
            return [.. groups.Select((group, i) => new AnalysisNode("summary-0-" + (i + 1), AnalysisNodeKind.Summary, null,
                [.. run.Nodes.Where(n => n.Kind == AnalysisNodeKind.Integration || n.Kind == AnalysisNodeKind.Extraction && (group.IsEmpty || group.Any(id => integrated.Index.Findings[id - 1].Chunk == run.Chunks.Single(c => c.Id == n.ChunkId).Number))).Select(n => n.Key)],
                Guid.NewGuid(), "", AnalysisNodeState.Pending) { Selection = group })];
        }
        var top = summaries.Where(n => n.Layer == summaries.Max(s => s.Layer)).ToArray();
        if (top.Length > 1)
        {
            var groups = new List<List<AnalysisNode>>(); var current = new List<AnalysisNode>(); var size = 0;
            foreach (var child in top)
            {
                var length = results[child.Key].Json.Length + 100;
                if (current.Count > 0 && (current.Count >= 3 || size + length > 69000)) { groups.Add(current); current = []; size = 0; }
                current.Add(child); size += length;
            }
            if (current.Count > 0) groups.Add(current);
            if (groups.Count >= top.Length || top[0].Layer >= 16) throw new InvalidDataException("汇总树无法在容量内收敛，已停止而非无限追加请求。");
            return [.. groups.Select((group, i) => new AnalysisNode($"summary-{top[0].Layer + 1}-{i + 1}", AnalysisNodeKind.Summary, null,
                [.. group.Select(n => n.Key)], Guid.NewGuid(), "", AnalysisNodeState.Pending) { Layer = top[0].Layer + 1 })];
        }
        if (!run.Nodes.Any(n => n.Kind == AnalysisNodeKind.Dimension))
        {
            var root = AnalysisJson.Read<NovelReportDraft>(results[top[0].Key].Json);
            return [.. Enum.GetValues<AnalysisDimension>().Select(d =>
            {
                var eligible = integrated.Index.Findings.Where(f => f.Value.Dimension == d).Select(f => f.Number).ToArray();
                if (d == AnalysisDimension.Style && eligible.Length < 3) eligible = integrated.Index.Findings.Where(f => f.Value.Evidence.Any(e => e.Quote.Length >= 20)).Select(f => f.Number).ToArray();
                var maximum = 24; var selection = Representative(eligible, maximum);
                while (AnalysisJson.Write(NovelReportRequests.DimensionMaterial(integrated, root, d, selection)).Length > 69000 && maximum > 3)
                { maximum = Math.Max(3, maximum / 2); selection = Representative(eligible, maximum); }
                return new AnalysisNode("dimension-" + d, AnalysisNodeKind.Dimension, null,
                    [top[0].Key, .. run.Nodes.Where(n => n.Kind == AnalysisNodeKind.Integration).Select(n => n.Key)], Guid.NewGuid(), "", AnalysisNodeState.Pending)
                { Dimension = d, Selection = selection };
            })];
        }
        if (!run.Nodes.Any(n => n.Kind == AnalysisNodeKind.Synthesis))
            return [new("synthesis", AnalysisNodeKind.Synthesis, null, [.. run.Nodes.Where(n => n.Kind == AnalysisNodeKind.Dimension).Select(n => n.Key)], Guid.NewGuid(), "", AnalysisNodeState.Pending)];
        return [];
    }

    /// <summary>固定等距规则覆盖前、中、后期，不以模型挑选结果替代原文代表性；同样输入得到同样编号。</summary>
    public static ImmutableArray<int> Representative(IReadOnlyList<int> ordered, int maximum)
    {
        if (maximum < 2) throw new ArgumentOutOfRangeException(nameof(maximum));
        if (ordered.Count <= maximum) return [.. ordered];
        return [.. Enumerable.Range(0, maximum).Select(i => ordered[(int)((long)i * (ordered.Count - 1) / (maximum - 1))])];
    }
}

internal static class NovelReportMaterial
{
    public static object Leaf(NovelIntegrationSnapshot integrated, ImmutableArray<int> selection)
    {
        var ids = selection.ToHashSet(); var facts = integrated.Index.Findings.Where(f => ids.Contains(f.Number)).ToArray(); var chunks = facts.Select(f => f.Chunk).ToHashSet();
        return new
        {
            Facts = facts.Select(NovelIntegrationPlanner.FindingMaterial),
            Continuity = integrated.Observations.Where(o => o.Value.Facts.All(ids.Contains)).Take(12).Select(o => new
            {
                o.Dimension,
                o.Value.Subject,
                Statement = Short(o.Value.Statement, 600),
                o.Value.Certainty,
                o.Value.Narration,
                o.Value.Role,
                o.Value.Facts,
                Conditions = Short(o.Value.Conditions, 300),
                Exceptions = Short(o.Value.Exceptions, 300)
            }),
            Identities = integrated.Identities.Where(e => e.Mentions.Any(id => chunks.Contains(integrated.Index.Mentions[id - 1].Chunk))).Take(12)
                .Select(e => new { e.Name, e.Category, e.Certainty, MentionCount = e.Mentions.Length, Ambiguous = !e.PossibleSameMentions.IsEmpty }),
            Gaps = integrated.Index.Chunks.Select((chunk, i) => (Chunk: chunk, Number: i + 1)).Where(pair => selection.IsEmpty || chunks.Contains(pair.Number)).SelectMany(pair => pair.Chunk.Gaps).Take(12)
        };
    }
    public static object DirectFinding(IndexedFinding finding) => new
    {
        finding.Number,
        finding.Chunk,
        finding.Value.Dimension,
        finding.Value.Statement,
        Certainty = finding.Value.Kind,
        finding.Value.Narration,
        Evidence = finding.Value.Evidence.Take(2).Select(e => new { e.Range.Start, e.Quote })
    };
    private static string Short(string text, int maximum)
    { var length = Math.Min(text.Length, maximum); if (length < text.Length && char.IsHighSurrogate(text[length - 1]) && char.IsLowSurrogate(text[length])) length--; return text[..length]; }
}
