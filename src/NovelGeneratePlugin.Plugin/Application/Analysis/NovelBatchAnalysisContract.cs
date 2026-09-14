using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using NovelGeneratePlugin.Domain.Analysis;

namespace NovelGeneratePlugin.Application.Analysis;

/// <summary>每个章内片段有自己的输出和证据段号，合批不缩减整本书的提取配额。</summary>
public sealed record NovelBatchPart(int Number, string Chapter, AnalysisChunk Chunk);

public sealed class NovelBatchAnalysisContract : IChunkAnalysisContract
{
    public const string PromptVersion = "v6-batch";
    public const int PartCharacters = 12000;
    private readonly SourceSnapshot _source;
    private readonly AnalysisChunk _chunk;
    private readonly Guid _operationId;
    private readonly string _stamp;
    private readonly ImmutableArray<IChunkAnalysisContract> _contracts;
    public ImmutableArray<NovelBatchPart> Parts { get; }
    public string JsonSchema { get; }

    public NovelBatchAnalysisContract(ReferenceImport input, AnalysisChunk chunk, Guid operationId, string stamp)
    {
        _source = input.Source; _chunk = chunk; _operationId = operationId; _stamp = stamp;
        Parts = Partition(input, chunk);
        _contracts = [.. Parts.Select(p => (IChunkAnalysisContract)new ChunkAnalysisContract(input.Source, p.Chunk,
            new Guid(SHA256.HashData(Encoding.UTF8.GetBytes($"{operationId:N}/{p.Chunk.Body.Start}"))[..16]), stamp,
            ChunkAnalysisContract.Passages(input.Source, p.Chunk), true, true))];
        // 复用一份结构定义；各 Part 的实际段号、完整性和章节归属由对应子契约再验证。
        var widest = Parts.MaxBy(p => ChunkAnalysisContract.Passages(input.Source, p.Chunk).Length)!;
        using var schema = JsonDocument.Parse(_contracts[widest.Number - 1].JsonSchema);
        JsonSchema = JsonSerializer.Serialize(new
        {
            type = "object",
            additionalProperties = false,
            required = new[] { "Parts" },
            properties = new
            {
                Parts = new
                {
                    type = "array",
                    minItems = Parts.Length,
                    maxItems = Parts.Length,
                    items = new
                    {
                        type = "object",
                        additionalProperties = false,
                        required = new[] { "Part", "Analysis" },
                        properties = new { Part = new { type = "integer", minimum = 1, maximum = Parts.Length }, Analysis = schema.RootElement }
                    }
                }
            }
        });
    }

    public static ImmutableArray<NovelBatchPart> Partition(ReferenceImport input, AnalysisChunk chunk)
    {
        var parts = ImmutableArray.CreateBuilder<NovelBatchPart>();
        foreach (var section in input.Sections.Where(s => s.Range.Start < chunk.Body.End && s.Range.End > chunk.Body.Start))
        {
            if (!section.Included) throw new InvalidDataException("批次不能跨越未选中的章节。");
            var end = Math.Min(chunk.Body.End, section.Range.End);
            for (var start = Math.Max(chunk.Body.Start, section.Range.Start); start < end;)
            {
                var next = Math.Min(start + PartCharacters, end);
                if (next < end) next = NovelTextPartitioner.Boundary(input.Source.Text, start, next);
                var range = new SourceRange(start, next - start);
                // 子输出只能引用本片段。其他章节同批提供给模型理解，不计为本片段证据。
                parts.Add(new(parts.Count + 1, section.Title, new(chunk.Id, section.Id, parts.Count + 1, range, range)));
                start = next;
            }
        }
        if (parts.Count == 0) throw new InvalidDataException("分析批次为空。");
        return parts.ToImmutable();
    }

    public void Validate(JsonElement value) => Read(value.GetRawText());

    public ChunkAnalysisResult Read(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        RequireFields(root, "Parts");
        var values = root.GetProperty("Parts");
        if (values.ValueKind != JsonValueKind.Array || values.GetArrayLength() != Parts.Length)
            throw new InvalidDataException("批次必须返回每个片段的完整分析，不能遗漏或增添片段。");
        var results = new ChunkAnalysisResult[Parts.Length];
        foreach (var item in values.EnumerateArray())
        {
            RequireFields(item, "Part", "Analysis");
            if (item.GetProperty("Part").ValueKind != JsonValueKind.Number || !item.GetProperty("Part").TryGetInt32(out var number) || number < 1 || number > Parts.Length || results[number - 1] is not null)
                throw new InvalidDataException("批次片段编号重复或超出输入范围。");
            results[number - 1] = _contracts[number - 1].Read(item.GetProperty("Analysis").GetRawText());
        }
        return new(_operationId, _chunk.Id, _source.Id, _stamp,
            string.Join("\n", Parts.Select(p => $"{p.Chapter} / 片段 {p.Number}：{results[p.Number - 1].Summary}")),
            [.. results.SelectMany(r => r.Entities)], [.. results.SelectMany(r => r.Findings)], [.. results.SelectMany(r => r.Gaps)]);
    }

    private static void RequireFields(JsonElement value, params string[] names)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new InvalidDataException("批次分析必须为对象。");
        var fields = value.EnumerateObject().Select(p => p.Name).ToArray();
        if (fields.Length != names.Length || fields.Distinct(StringComparer.Ordinal).Count() != names.Length || fields.Except(names, StringComparer.Ordinal).Any())
            throw new InvalidDataException("批次分析字段缺失、重复或不受支持。");
    }
}
