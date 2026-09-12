using System.Collections.Immutable;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Domain.Analysis;

namespace NovelGeneratePlugin.Application.Analysis;

public sealed record ReferenceIdentity(Guid Id, string Name, StoryEntityKind Category, ImmutableArray<int> Mentions,
    AnalysisStatementKind Certainty, string Reason, ImmutableArray<int> PossibleSameMentions);
public sealed record IndexedObservation(int Number, AnalysisDimension Dimension, ContinuityObservation Value, string NodeKey);

/// <summary>
/// 不确定的多提及分组拆回独立身份，并保存可能同一人的候选连接；这不是自动“纠正”模型，而是保守的采纳规则。
/// 所有提取事实保持可查，未进入整合观察的编号明确列出，不能把模型压缩造成的遗漏藏在摘要之后。
/// </summary>
public sealed record NovelIntegrationSnapshot(NovelAnalysisIndex Index, ImmutableArray<ReferenceIdentity> Identities,
    ImmutableArray<IndexedObservation> Observations, ImmutableArray<string> OpenQuestions, ImmutableArray<int> UnintegratedFacts)
{
    public static NovelIntegrationSnapshot Build(AnalysisRun run, NovelAnalysisIndex index, IReadOnlyDictionary<string, AnalysisNodeResult> results)
    {
        var identities = ImmutableArray.CreateBuilder<ReferenceIdentity>(); var observations = ImmutableArray.CreateBuilder<IndexedObservation>();
        var questions = ImmutableArray.CreateBuilder<string>();
        foreach (var node in run.Nodes.Where(n => n.Kind == AnalysisNodeKind.Integration))
        {
            if (node.State != AnalysisNodeState.Completed || !results.TryGetValue(node.Key, out var raw) || raw.InputStamp != node.InputStamp || raw.Hash != AnalysisLimits.HashText(raw.Json))
                throw new InvalidDataException("整合节点尚未完成或结果已损坏。");
            if (node.Dimension is null)
            {
                var output = new IdentityAnalysisContract(index.Mentions.Where(m => node.Selection.Contains(m.Number)).ToArray()).Read(raw.Json);
                questions.AddRange(output.OpenQuestions);
                foreach (var group in output.Groups)
                {
                    if (group.Certainty == AnalysisStatementKind.Uncertain && group.Mentions.Length > 1)
                    {
                        foreach (var number in group.Mentions) Add(index.Mentions[number - 1].Value.Name, [number], [.. group.Mentions.Where(n => n != number)]);
                    }
                    else Add(group.Name, group.Mentions, []);
                    void Add(string name, ImmutableArray<int> members, ImmutableArray<int> possible)
                    {
                        var hash = CanonicalJson.Hash(new { run.SourceId, node.OperationId, Members = members.Order().ToArray() });
                        identities.Add(new(Guid.ParseExact(hash[..32], "N"), name, group.Category, members, group.Certainty, group.Reason, possible));
                    }
                }
            }
            else
            {
                var output = new ContinuityAnalysisContract(index.Findings.Where(f => node.Selection.Contains(f.Number)).ToArray()).Read(raw.Json);
                foreach (var observation in output.Observations) observations.Add(new(observations.Count + 1, node.Dimension.Value, observation, node.Key));
                questions.AddRange(output.OpenQuestions);
            }
        }
        var assigned = identities.SelectMany(e => e.Mentions).ToHashSet();
        if (!assigned.SetEquals(index.Mentions.Select(m => m.Number)) || identities.Sum(e => e.Mentions.Length) != assigned.Count)
            throw new InvalidDataException("身份映射没有完整覆盖提及，或发生重复归并。");
        var nameCandidates = identities.SelectMany(identity => identity.Mentions.SelectMany(number => index.Mentions[number - 1].Value.Aliases.Prepend(index.Mentions[number - 1].Value.Name))
            .Distinct(StringComparer.Ordinal).Select(name => (Name: name, identity.Id))).GroupBy(p => p.Name, StringComparer.Ordinal);
        foreach (var candidate in nameCandidates.Where(g => g.Select(p => p.Id).Distinct().Count() > 1))
            questions.Add($"同名/别名“{candidate.Key}”关联多个独立身份，当前按各自证据保留，未跨组强行合并。");
        var used = observations.SelectMany(o => o.Value.Facts).ToHashSet();
        return new(index, identities.ToImmutable(), observations.ToImmutable(), [.. questions.Distinct(StringComparer.Ordinal)], [.. index.Findings.Where(f => !used.Contains(f.Number)).Select(f => f.Number)]);
    }
}
