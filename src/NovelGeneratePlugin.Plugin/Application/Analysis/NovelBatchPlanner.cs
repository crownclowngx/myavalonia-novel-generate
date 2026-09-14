using System.Collections.Immutable;
using NovelGeneratePlugin.Application.Models;
using NovelGeneratePlugin.Domain;
using NovelGeneratePlugin.Domain.Analysis;

namespace NovelGeneratePlugin.Application.Analysis;

/// <summary>按真实序列化请求估算容量，连续章段自动合批；输入和输出余量共同决定批次大小。</summary>
public static class NovelBatchPlanner
{
    public static ReferenceImport Plan(ReferenceImport input, FrozenConnection connection, ModelPreset preset, AnalysisCapacityOptions options, CancellationToken ct,
        IReadOnlySet<Guid>? completedChunks = null)
    {
        input.Validate(); options.Validate();
        var capacity = ModelInputCapacity.ContextCapacity(connection, preset, options.ContextTokens)
            ?? throw new InvalidDataException("此模型的上下文容量未知，请填写单次上下文容量后再使用自动分批。");
        var inputTarget = Math.Min(options.TargetInputTokens, capacity - preset.MaxOutputTokens - Math.Max(8192, capacity / 20));
        var outputTarget = preset.MaxOutputTokens * 3L / 4;
        var batches = ImmutableArray.CreateBuilder<AnalysisChunk>();
        var preserved = input.Chunks.Where(c => completedChunks?.Contains(c.Id) == true).ToArray();
        AnalysisChunk? current = null;
        foreach (var section in input.Sections.Where(s => s.Included))
        {
            if (current is not null && current.Body.End != section.Range.Start) Flush();
            for (var start = section.Range.Start; start < section.Range.End;)
            {
                ct.ThrowIfCancellationRequested();
                var saved = preserved.FirstOrDefault(c => c.Body.Start <= start && start < c.Body.End);
                if (saved is not null)
                {
                    Flush();
                    if (start == saved.Body.Start) batches.Add(saved with { Number = batches.Count + 1 });
                    start = Math.Min(section.Range.End, saved.Body.End); continue;
                }
                var end = Math.Min(start + NovelBatchAnalysisContract.PartCharacters, section.Range.End);
                var nextSaved = preserved.FirstOrDefault(c => c.Body.Start > start);
                if (nextSaved is not null) end = Math.Min(end, nextSaved.Body.Start);
                if (end < section.Range.End) end = NovelTextPartitioner.Boundary(input.Source.Text, start, end);
                Add(new(Guid.NewGuid(), section.Id, 0, new(start, end - start), new(start, end - start)));
                start = end;
            }
        }
        Flush();
        var result = input with { Chunks = batches.ToImmutable() }; result.Validate(); return result;

        bool Fits(AnalysisChunk chunk)
        {
            ct.ThrowIfCancellationRequested();
            var parts = NovelBatchAnalysisContract.Partition(input, chunk);
            // 这是输出规划余量而非精确用量；避免长输入仍挤在一份小输出中。
            if (parts.Sum(p => Math.Max(1024L, p.Chunk.Body.Length / 2L)) > outputTarget) return false;
            var prepared = NovelChunkAnalysisService.Prepare(input, chunk, connection, Guid.NewGuid(), NovelBatchAnalysisContract.PromptVersion);
            var estimate = ModelInputCapacity.Estimate(prepared.Request with { ExecutionPreset = preset, ContextTokenLimit = options.ContextTokens });
            return !estimate.Exceeded && estimate.EstimatedInputTokens <= inputTarget;
        }

        void Add(AnalysisChunk unit)
        {
            ct.ThrowIfCancellationRequested();
            if (current is not null)
            {
                var range = new SourceRange(current.Body.Start, unit.Body.End - current.Body.Start);
                var combined = current with { Body = range, Context = range };
                if (Fits(combined)) { current = combined; return; }
                Flush();
            }
            if (Fits(unit)) { current = unit; return; }
            if (unit.Body.Length < options.MinimumBodyCharacters * 2)
                throw new InvalidDataException("当前输入目标、模型上下文或输出预留不足以容纳最小分析片段；请增大相应容量。");
            var middle = NovelTextPartitioner.Boundary(input.Source.Text, unit.Body.Start, unit.Body.Start + unit.Body.Length / 2);
            if (middle - unit.Body.Start < options.MinimumBodyCharacters || unit.Body.End - middle < options.MinimumBodyCharacters)
            {
                middle = unit.Body.Start + unit.Body.Length / 2;
                if (char.IsHighSurrogate(input.Source.Text[middle - 1]) && char.IsLowSurrogate(input.Source.Text[middle])) middle--;
            }
            if (middle - unit.Body.Start < options.MinimumBodyCharacters || unit.Body.End - middle < options.MinimumBodyCharacters)
                throw new InvalidDataException("完整字符边界无法满足批次最小片段大小。");
            var left = new SourceRange(unit.Body.Start, middle - unit.Body.Start); var right = new SourceRange(middle, unit.Body.End - middle);
            Add(unit with { Body = left, Context = left });
            Add(unit with { Id = Guid.NewGuid(), Body = right, Context = right });
        }

        void Flush()
        {
            if (current is null) return;
            var previous = input.Chunks.FirstOrDefault(c => c.SectionId == current.SectionId && c.Body == current.Body && c.Context == current.Context);
            batches.Add(current with { Id = previous?.Id ?? current.Id, Number = batches.Count + 1 }); current = null;
        }
    }
}
