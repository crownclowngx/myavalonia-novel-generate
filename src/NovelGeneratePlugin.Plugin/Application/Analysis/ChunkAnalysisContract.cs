using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Domain.Analysis;
using NovelGeneratePlugin.Domain;

namespace NovelGeneratePlugin.Application.Analysis;

public sealed record AnalysisPassage(int Number, int Start, string Text);
public sealed record PassageReference(int Passage);
public sealed record MentionOutput(string Name, StoryEntityKind Kind, ImmutableArray<string> Aliases, string Description, ImmutableArray<PassageReference> Evidence);
public sealed record FindingOutput(AnalysisDimension Dimension, string Subject, string Statement, AnalysisStatementKind Kind,
    ImmutableArray<string> RelatedSubjects, string TimeHint, NarrativeSource Narration, ImmutableArray<PassageReference> Evidence);
/// <summary>模型缺口 DTO 与领域分离。可选证据只接收段号，仍由本地验证并转换成原文坐标。</summary>
public sealed record GapOutput(AnalysisDimension Dimension, string Reason)
{
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public ImmutableArray<PassageReference> Evidence { get; init; }
}
public sealed record ChunkOutput(string Summary, ImmutableArray<MentionOutput> Entities, ImmutableArray<FindingOutput> Findings, ImmutableArray<GapOutput> Gaps);

/// <summary>
/// 模型契约负责从“可引用段落”转换成带来源的领域结果。模型只引用段号，原文与坐标由本地取回；
/// 避免模型抄写引文时纠正错别字或标点。段号错误直接拒绝，原文存在与语义支持仍分别校验。
/// </summary>
public sealed class ChunkAnalysisContract
    (SourceSnapshot source, AnalysisChunk chunk, Guid operationId, string inputStamp, ImmutableArray<AnalysisPassage> passages, bool includeGapEvidenceSchema = false) : IModelOutputContract
{
    public const string Version = "novel-chunk-v2";
    private static readonly JsonSerializerOptions Options = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    public void Validate(JsonElement value) => Read(value.GetRawText());

    public ChunkAnalysisResult Read(string json)
    {
        var output = JsonSerializer.Deserialize<ChunkOutput>(json, Options) ?? throw new InvalidDataException("分析提取结果为空。");
        if (string.IsNullOrWhiteSpace(output.Summary) || output.Summary.Length > 3000 || output.Entities.IsDefault || output.Entities.Length > 40 ||
            output.Findings.IsDefault || output.Findings.Length > 64 || output.Gaps.IsDefault || output.Gaps.Length > 24)
            throw new InvalidDataException("单元提取字段缺失或超过容量。");
        var entities = ImmutableArray.CreateBuilder<ReferenceMention>();
        foreach (var item in output.Entities)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 100 || !Enum.IsDefined(item.Kind) ||
                item.Aliases.IsDefault || item.Aliases.Length > 15 || item.Aliases.Any(s => string.IsNullOrWhiteSpace(s) || s.Length > 100) ||
                string.IsNullOrWhiteSpace(item.Description) || item.Description.Length > 2000)
                throw new InvalidDataException("实体提及的身份或描述无效。");
            entities.Add(new(Id("entity", entities.Count), item.Name, item.Kind, item.Aliases, item.Description, Evidence(item.Evidence)));
        }
        var findings = ImmutableArray.CreateBuilder<AnalysisFinding>();
        foreach (var item in output.Findings)
        {
            if (item is null) throw new InvalidDataException("分析结论不能为空。");
            var finding = new AnalysisFinding(Id("finding", findings.Count), item.Dimension, item.Subject, item.Statement, item.Kind, Evidence(item.Evidence))
            { RelatedSubjects = item.RelatedSubjects, TimeHint = item.TimeHint, Narration = item.Narration };
            finding.Validate(source, chunk.Context); findings.Add(finding);
        }
        var observed = findings.Select(f => f.Dimension).ToHashSet();
        var missing = new HashSet<AnalysisDimension>();
        var gaps = ImmutableArray.CreateBuilder<DimensionGap>();
        foreach (var gap in output.Gaps)
        {
            if (gap is null || !Enum.IsDefined(gap.Dimension) ||
                string.IsNullOrWhiteSpace(gap.Reason) || gap.Reason.Length > 1000)
                throw new InvalidDataException("未观察维度缺少明确说明或超过容量。");
            // 同一维度可以有多个不同缺口；原契约限制每维一条会把有效的不确定信息误拒绝。
            // JSON 字段结构未变，保留 v2 输入身份及已验证缓存；不拼接、截断或猜测修补模型内容。
            missing.Add(gap.Dimension);
            gaps.Add(new(gap.Dimension, gap.Reason)
            { Evidence = gap.Evidence.IsDefault ? default : Evidence(gap.Evidence) });
        }
        if (observed.Union(missing).Count() != Enum.GetValues<AnalysisDimension>().Length)
            throw new InvalidDataException("六个分析维度必须有结论或明确的未观察说明。");
        return new(operationId, chunk.Id, source.Id, inputStamp, output.Summary, entities.ToImmutable(), findings.ToImmutable(), gaps.ToImmutable());
    }

    private ImmutableArray<AnalysisEvidence> Evidence(ImmutableArray<PassageReference> quotes)
    {
        if (quotes.IsDefaultOrEmpty || quotes.Length > 8) throw new InvalidDataException("每条观察需要 1–8 条原文依据。");
        var result = ImmutableArray.CreateBuilder<AnalysisEvidence>();
        foreach (var quote in quotes)
        {
            if (quote is null) throw new InvalidDataException("引用段号不能为空。");
            var passage = passages.SingleOrDefault(p => p.Number == quote.Passage) ?? throw new InvalidDataException("引用段号不属于本次输入。");
            var evidence = new AnalysisEvidence(source.Id, source.TextHash, new(passage.Start, passage.Text.Length), passage.Text);
            evidence.Validate(source, chunk.Context); result.Add(evidence);
        }
        return result.ToImmutable();
    }

    // 同一操作的原始响应在恢复时得到相同身份，避免重复解析产生新的实体提及和结论 ID。
    private Guid Id(string kind, int index) => new(SHA256.HashData(Encoding.UTF8.GetBytes($"{operationId:N}/{kind}/{index}"))[..16]);

    public static ImmutableArray<AnalysisPassage> Passages(SourceSnapshot source, AnalysisChunk chunk)
    {
        chunk.Context.Validate(source.Text); chunk.Body.Validate(source.Text);
        if (!chunk.Context.Contains(chunk.Body)) throw new InvalidDataException("单元上下文没有包含正文。");
        var result = ImmutableArray.CreateBuilder<AnalysisPassage>();
        for (var start = chunk.Context.Start; start < chunk.Context.End;)
        {
            var end = Math.Min(start + 600, chunk.Context.End);
            var newline = source.Text.IndexOf('\n', start, end - start);
            if (newline >= 0) end = newline + 1;
            if (end < source.Text.Length && char.IsHighSurrogate(source.Text[end - 1]) && char.IsLowSurrogate(source.Text[end])) end--;
            var text = source.Text[start..end];
            if (!string.IsNullOrWhiteSpace(text)) result.Add(new(result.Count + 1, start, text));
            start = end;
        }
        return result.ToImmutable();
    }

    private static object Text => new { type = "string" };
    private static object List(object items) => new { type = "array", items };
    private static object Object(Dictionary<string, object> properties) => new { type = "object", additionalProperties = false, required = properties.Keys.ToArray(), properties };
    private static object EnumSchema<T>() where T : struct, Enum => new { type = "string", @enum = Enum.GetNames<T>() };
    public string JsonSchema
    {
        get
        {
            var evidence = List(Object(new() { ["Passage"] = new { type = "integer" } }));
            // 新提示明确声明缺口的可选证据；旧提示继续生成原 schema，保留历史输入指纹。
            object gap = includeGapEvidenceSchema ? new
            {
                type = "object",
                additionalProperties = false,
                required = new[] { "Dimension", "Reason" },
                properties = new Dictionary<string, object> { ["Dimension"] = EnumSchema<AnalysisDimension>(), ["Reason"] = Text, ["Evidence"] = evidence }
            } : Object(new() { ["Dimension"] = EnumSchema<AnalysisDimension>(), ["Reason"] = Text });
            return JsonSerializer.Serialize(Object(new()
            {
                ["Summary"] = Text,
                ["Entities"] = List(Object(new() { ["Name"] = Text, ["Kind"] = EnumSchema<StoryEntityKind>(), ["Aliases"] = List(Text), ["Description"] = Text, ["Evidence"] = evidence })),
                ["Findings"] = List(Object(new()
                {
                    ["Dimension"] = EnumSchema<AnalysisDimension>(),
                    ["Subject"] = Text,
                    ["Statement"] = Text,
                    ["Kind"] = EnumSchema<AnalysisStatementKind>(),
                    ["RelatedSubjects"] = List(Text),
                    ["TimeHint"] = Text,
                    ["Narration"] = EnumSchema<NarrativeSource>(),
                    ["Evidence"] = evidence
                })),
                ["Gaps"] = List(gap)
            }));
        }
    }
}
