using System.Collections.Immutable;
using System.Text.RegularExpressions;
using NovelGeneratePlugin.Domain.Analysis;

namespace NovelGeneratePlugin.Application.Analysis;

public sealed record PartitionOptions(int ChunkCharacters = 6000, int ContextCharacters = 300)
{
    public void Validate()
    {
        if (ChunkCharacters is < 200 or > 12000 || ContextCharacters is < 0 or > 2000)
            throw new InvalidDataException("分析单元需为 200–12000 字符，上下文为 0–2000 字符；具体模型另行检查输入预算。");
    }
}

/// <summary>
/// 确定性分章与切块，不调用模型、不访问文件。标题只用于组织展示，所有匹配前后的字符始终保留。
/// 无可靠章标题时按有界段落片段退化；手动切分的 Body 属于单章，Split 也支持运行时跨章批次。
/// </summary>
public static partial class NovelTextPartitioner
{
    /// <summary>仅二分指定单元。换行优先且不拆 Unicode 代理对；子上下文限制在原父上下文内。</summary>
    public static ReferenceImport Split(ReferenceImport input, Guid chunkId, int minimum)
    {
        input.Validate();
        if (minimum is < 200 or > 6000) throw new InvalidDataException("子单元最小正文需为 200–6000 字符。");
        var parent = input.Chunks.Single(c => c.Id == chunkId);
        if (parent.Body.Length < minimum * 2) throw new InvalidDataException("问题单元已达到最小拆分大小。");
        var desired = parent.Body.Start + parent.Body.Length / 2;
        var middle = Boundary(input.Source.Text, parent.Body.Start, desired);
        if (middle - parent.Body.Start < minimum || parent.Body.End - middle < minimum) middle = SafeLeft(input.Source.Text, desired);
        if (middle - parent.Body.Start < minimum || parent.Body.End - middle < minimum) throw new InvalidDataException("完整字符边界无法满足最小拆分大小。");
        var leftPadding = parent.Body.Start - parent.Context.Start; var rightPadding = parent.Context.End - parent.Body.End;
        AnalysisChunk Child(int start, int end)
        {
            var contextStart = SafeLeft(input.Source.Text, Math.Max(parent.Context.Start, start - leftPadding));
            var contextEnd = SafeRight(input.Source.Text, Math.Min(parent.Context.End, end + rightPadding));
            var section = input.Sections.Single(s => s.Range.Start <= start && start < s.Range.End);
            return new(Guid.NewGuid(), section.Id, 0, new(start, end - start), new(contextStart, contextEnd - contextStart));
        }
        var children = new[] { Child(parent.Body.Start, middle), Child(middle, parent.Body.End) };
        var chunks = input.Chunks.SelectMany(c => c.Id == chunkId ? children : [c]).Select((c, i) => c with { Number = i + 1 }).ToImmutableArray();
        var result = input with { Chunks = chunks }; result.Validate(); return result;
    }
    [GeneratedRegex(@"^[\t 　]*(?:第[零〇一二三四五六七八九十百千万两\d]+[章回节卷部篇集](?:[^\r\n]*)|[Cc][Hh][Aa][Pp][Tt][Ee][Rr][\t ]+\d+[^\r\n]*)[\t ]*\r?$", RegexOptions.Multiline, 1000)]
    private static partial Regex Heading();

    /// <summary>在同一不可变来源上新建切分版本，保留章节身份；旧运行继续持有自己的切分，未受影响单元可以按输入指纹复用。</summary>
    public static ReferenceImport Rechunk(ReferenceImport input, PartitionOptions options, CancellationToken ct)
    {
        input.Validate(); options.Validate(); var chunks = ImmutableArray.CreateBuilder<AnalysisChunk>();
        foreach (var section in input.Sections.Where(s => s.Included))
        {
            for (var offset = section.Range.Start; offset < section.Range.End;)
            {
                ct.ThrowIfCancellationRequested();
                var end = Boundary(input.Source.Text, offset, Math.Min(section.Range.End, offset + options.ChunkCharacters));
                var contextStart = SafeLeft(input.Source.Text, Math.Max(0, offset - options.ContextCharacters));
                var contextEnd = SafeRight(input.Source.Text, Math.Min(input.Source.Text.Length, end + options.ContextCharacters));
                var body = new SourceRange(offset, end - offset); var context = new SourceRange(contextStart, contextEnd - contextStart);
                var previous = input.Chunks.FirstOrDefault(c => c.SectionId == section.Id && c.Body == body && c.Context == context);
                chunks.Add(new(previous?.Id ?? Guid.NewGuid(), section.Id, chunks.Count + 1, body, context)); offset = end;
            }
        }
        var result = input with { Chunks = chunks.ToImmutable() }; result.Validate(); return result;
    }

    public static ReferenceImport Partition(SourceSnapshot source, string name, PartitionOptions options, CancellationToken ct)
    {
        source.Validate(); options.Validate(); ct.ThrowIfCancellationRequested();
        // 正则显式接收行尾 CR，匹配保持原长度和原偏移；仅展示标题 Trim，正文不进行 Replace 或 Trim。
        var starts = Heading().Matches(source.Text).Select(m => (Start: m.Index, Title: m.Value.Trim())).ToArray();
        var sections = ImmutableArray.CreateBuilder<ReferenceSection>();
        if (starts.Length == 0)
        {
            var offset = 0;
            while (offset < source.Text.Length)
            {
                ct.ThrowIfCancellationRequested();
                var end = Boundary(source.Text, offset, Math.Min(source.Text.Length, offset + options.ChunkCharacters));
                sections.Add(new(Guid.NewGuid(), sections.Count + 1, $"片段 {sections.Count + 1}（未识别章标题）", new(offset, end - offset), true));
                offset = end;
            }
        }
        else
        {
            if (starts[0].Start > 0) sections.Add(new(Guid.NewGuid(), 1, "卷首/导出说明（保留）", new(0, starts[0].Start), true));
            for (var index = 0; index < starts.Length; index++)
            {
                ct.ThrowIfCancellationRequested();
                var end = index + 1 == starts.Length ? source.Text.Length : starts[index + 1].Start;
                var title = starts[index].Title;
                if (title.Length > 200) title = title[..SafeLeft(title, 199)] + "…";
                sections.Add(new(Guid.NewGuid(), sections.Count + 1, title, new(starts[index].Start, end - starts[index].Start), true));
            }
        }
        if (sections.Count > AnalysisLimits.MaximumSections) throw new InvalidDataException("章节/片段数量超过 10000，请调整切分设置。");
        var chunks = ImmutableArray.CreateBuilder<AnalysisChunk>();
        foreach (var section in sections)
        {
            for (var offset = section.Range.Start; offset < section.Range.End;)
            {
                ct.ThrowIfCancellationRequested();
                var end = Boundary(source.Text, offset, Math.Min(section.Range.End, offset + options.ChunkCharacters));
                var contextStart = SafeLeft(source.Text, Math.Max(0, offset - options.ContextCharacters));
                var contextEnd = SafeRight(source.Text, Math.Min(source.Text.Length, end + options.ContextCharacters));
                chunks.Add(new(Guid.NewGuid(), section.Id, chunks.Count + 1, new(offset, end - offset), new(contextStart, contextEnd - contextStart)));
                offset = end;
            }
        }
        var result = new ReferenceImport(new(source.BookId, source.Id, 0, name, source.Text.Length, DateTimeOffset.UtcNow), source, sections.ToImmutable(), chunks.ToImmutable());
        result.Validate(); return result;
    }

    internal static int Boundary(string text, int start, int desired)
    {
        // 只在目标后半段回找换行，避免一连串短行使块过小；找不到就按完整 Unicode 字符边界切分。
        if (desired < text.Length && desired - start > 1)
        {
            var floor = start + (desired - start) / 2;
            var newline = text.LastIndexOf('\n', desired - 1, desired - floor);
            if (newline >= floor) desired = newline + 1;
        }
        var end = SafeLeft(text, desired);
        return end > start ? end : SafeRight(text, desired);
    }

    private static int SafeLeft(string text, int position) => position > 0 && position < text.Length && char.IsHighSurrogate(text[position - 1]) && char.IsLowSurrogate(text[position]) ? position - 1 : position;
    private static int SafeRight(string text, int position) => position > 0 && position < text.Length && char.IsHighSurrogate(text[position - 1]) && char.IsLowSurrogate(text[position]) ? position + 1 : position;
}
