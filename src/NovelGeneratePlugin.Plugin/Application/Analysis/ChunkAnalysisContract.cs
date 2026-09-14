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
public interface IChunkAnalysisContract : IModelOutputContract
{
    ChunkAnalysisResult Read(string json);
}

public sealed class ChunkAnalysisContract
    (SourceSnapshot source, AnalysisChunk chunk, Guid operationId, string inputStamp, ImmutableArray<AnalysisPassage> passages,
    bool includeGapEvidenceSchema = false, bool includeCapacitySchema = false) : IChunkAnalysisContract
{
    public const string Version = "novel-chunk-v2";
    private const int SummaryMaximum = 3000, EntityMaximum = 40, FindingMaximum = 64, GapMaximum = 24;
    private const int NameMaximum = 100, AliasMaximum = 15, DescriptionMaximum = 2000, SubjectMaximum = 200;
    private const int StatementMaximum = 4000, RelatedMaximum = 10, TimeMaximum = 500, ReasonMaximum = 1000;
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
        CheckText(output.Summary, "$.Summary", SummaryMaximum);
        CheckArray(output.Entities, "$.Entities", 0, EntityMaximum);
        CheckArray(output.Findings, "$.Findings", 0, FindingMaximum);
        CheckArray(output.Gaps, "$.Gaps", 0, GapMaximum);
        var entities = ImmutableArray.CreateBuilder<ReferenceMention>();
        foreach (var item in output.Entities)
        {
            var path = $"$.Entities[{entities.Count}]";
            if (item is null) throw new ModelContractException(path, new(ModelContractRule.Required));
            CheckText(item.Name, path + ".Name", NameMaximum); CheckEnum(item.Kind, path + ".Kind");
            CheckArray(item.Aliases, path + ".Aliases", 0, AliasMaximum);
            for (var i = 0; i < item.Aliases.Length; i++) CheckText(item.Aliases[i], path + $".Aliases[{i}]", NameMaximum);
            CheckText(item.Description, path + ".Description", DescriptionMaximum);
            entities.Add(new(Id("entity", entities.Count), item.Name, item.Kind, item.Aliases, item.Description, Evidence(item.Evidence, path + ".Evidence")));
        }
        var findings = ImmutableArray.CreateBuilder<AnalysisFinding>();
        foreach (var item in output.Findings)
        {
            var path = $"$.Findings[{findings.Count}]";
            if (item is null) throw new ModelContractException(path, new(ModelContractRule.Required));
            CheckEnum(item.Dimension, path + ".Dimension"); CheckEnum(item.Kind, path + ".Kind"); CheckEnum(item.Narration, path + ".Narration");
            CheckText(item.Subject, path + ".Subject", SubjectMaximum); CheckText(item.Statement, path + ".Statement", StatementMaximum);
            CheckText(item.TimeHint, path + ".TimeHint", TimeMaximum, false);
            CheckArray(item.RelatedSubjects, path + ".RelatedSubjects", 0, RelatedMaximum);
            for (var i = 0; i < item.RelatedSubjects.Length; i++) CheckText(item.RelatedSubjects[i], path + $".RelatedSubjects[{i}]", SubjectMaximum);
            var finding = new AnalysisFinding(Id("finding", findings.Count), item.Dimension, item.Subject, item.Statement, item.Kind, Evidence(item.Evidence, path + ".Evidence"))
            { RelatedSubjects = item.RelatedSubjects, TimeHint = item.TimeHint, Narration = item.Narration };
            finding.Validate(source, chunk.Context); findings.Add(finding);
        }
        var observed = findings.Select(f => f.Dimension).ToHashSet();
        var missing = new HashSet<AnalysisDimension>();
        var gaps = ImmutableArray.CreateBuilder<DimensionGap>();
        foreach (var gap in output.Gaps)
        {
            var path = $"$.Gaps[{gaps.Count}]";
            if (gap is null) throw new ModelContractException(path, new(ModelContractRule.Required));
            CheckEnum(gap.Dimension, path + ".Dimension"); CheckText(gap.Reason, path + ".Reason", ReasonMaximum);
            // 同一维度可以有多个不同缺口；原契约限制每维一条会把有效的不确定信息误拒绝。
            // JSON 字段结构未变，保留 v2 输入身份及已验证缓存；不拼接、截断或猜测修补模型内容。
            missing.Add(gap.Dimension);
            gaps.Add(new(gap.Dimension, gap.Reason)
            { Evidence = gap.Evidence.IsDefault ? default : Evidence(gap.Evidence, path + ".Evidence") });
        }
        if (observed.Union(missing).Count() != Enum.GetValues<AnalysisDimension>().Length)
            throw new ModelContractException("$", new(ModelContractRule.DimensionCoverage, observed.Union(missing).Count()));
        return new(operationId, chunk.Id, source.Id, inputStamp, output.Summary, entities.ToImmutable(), findings.ToImmutable(), gaps.ToImmutable());
    }

    private ImmutableArray<AnalysisEvidence> Evidence(ImmutableArray<PassageReference> quotes, string path)
    {
        CheckArray(quotes, path, 1, AnalysisEvidence.MaximumPerObservation);
        var result = ImmutableArray.CreateBuilder<AnalysisEvidence>();
        foreach (var quote in quotes)
        {
            var position = path + $"[{result.Count}].Passage";
            if (quote is null) throw new ModelContractException(position, new(ModelContractRule.Required));
            var passage = passages.SingleOrDefault(p => p.Number == quote.Passage)
                ?? throw new ModelContractException(position, new(ModelContractRule.PassageNumber, quote.Passage, 1, passages.Length));
            var evidence = new AnalysisEvidence(source.Id, source.TextHash, new(passage.Start, passage.Text.Length), passage.Text);
            evidence.Validate(source, chunk.Context); result.Add(evidence);
        }
        return result.ToImmutable();
    }

    // 同一组字段规则同时约束本地 DTO 和新版 Schema。报错只带固定路径及数量，绝不拼接模型内容。
    private static void CheckArray<T>(ImmutableArray<T> value, string path, int minimum, int maximum)
    {
        if (value.IsDefault) throw new ModelContractException(path, new(ModelContractRule.Required));
        if (value.Length < minimum || value.Length > maximum)
            throw new ModelContractException(path, new(ModelContractRule.ArrayCount, value.Length, minimum, maximum));
    }
    private static void CheckText(string? value, string path, int maximum, bool required = true)
    {
        if (value is null || required && string.IsNullOrWhiteSpace(value)) throw new ModelContractException(path, new(ModelContractRule.Required));
        if (value.Length > maximum) throw new ModelContractException(path, new(ModelContractRule.TextLength, value.Length, required ? 1 : 0, maximum));
    }
    private static void CheckEnum<T>(T value, string path) where T : struct, Enum
    { if (!Enum.IsDefined(value)) throw new ModelContractException(path, new(ModelContractRule.EnumValue)); }

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
    private object TextSchema(int maximum, bool required = true) => includeCapacitySchema
        ? new { type = "string", minLength = required ? 1 : 0, maxLength = maximum } : Text;
    private object ArraySchema(object items, int minimum, int maximum) => includeCapacitySchema
        ? new { type = "array", items, minItems = minimum, maxItems = maximum } : List(items);
    private static object Object(Dictionary<string, object> properties) => new { type = "object", additionalProperties = false, required = properties.Keys.ToArray(), properties };
    private static object EnumSchema<T>() where T : struct, Enum => new { type = "string", @enum = Enum.GetNames<T>() };
    public string JsonSchema
    {
        get
        {
            object passage = includeCapacitySchema ? new { type = "integer", minimum = 1, maximum = passages.Length } : new { type = "integer" };
            var evidence = ArraySchema(Object(new() { ["Passage"] = passage }), 1, AnalysisEvidence.MaximumPerObservation);
            // 新提示明确声明缺口的可选证据；旧提示继续生成原 schema，保留历史输入指纹。
            object gap = includeGapEvidenceSchema ? new
            {
                type = "object",
                additionalProperties = false,
                required = new[] { "Dimension", "Reason" },
                properties = new Dictionary<string, object> { ["Dimension"] = EnumSchema<AnalysisDimension>(), ["Reason"] = TextSchema(ReasonMaximum), ["Evidence"] = evidence }
            } : Object(new() { ["Dimension"] = EnumSchema<AnalysisDimension>(), ["Reason"] = TextSchema(ReasonMaximum) });
            return JsonSerializer.Serialize(Object(new()
            {
                ["Summary"] = TextSchema(SummaryMaximum),
                ["Entities"] = ArraySchema(Object(new() { ["Name"] = TextSchema(NameMaximum), ["Kind"] = EnumSchema<StoryEntityKind>(), ["Aliases"] = ArraySchema(TextSchema(NameMaximum), 0, AliasMaximum), ["Description"] = TextSchema(DescriptionMaximum), ["Evidence"] = evidence }), 0, EntityMaximum),
                ["Findings"] = ArraySchema(Object(new()
                {
                    ["Dimension"] = EnumSchema<AnalysisDimension>(),
                    ["Subject"] = TextSchema(SubjectMaximum),
                    ["Statement"] = TextSchema(StatementMaximum),
                    ["Kind"] = EnumSchema<AnalysisStatementKind>(),
                    ["RelatedSubjects"] = ArraySchema(TextSchema(SubjectMaximum), 0, RelatedMaximum),
                    ["TimeHint"] = TextSchema(TimeMaximum, false),
                    ["Narration"] = EnumSchema<NarrativeSource>(),
                    ["Evidence"] = evidence
                }), 0, FindingMaximum),
                ["Gaps"] = ArraySchema(gap, 0, GapMaximum)
            }));
        }
    }
}
