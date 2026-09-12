using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;

namespace NovelGeneratePlugin.Domain.Analysis;

/// <summary>
/// 完整小说与短材料采用独立容量契约。字符数是 .NET UTF-16 长度，既不是汉字数，也不是模型 token 数。
/// 上限在领域和导入边界同时检查，防止 UI、测试或未来工作流绕过文件选择器后写入不可处理的数据。
/// </summary>
public static class AnalysisLimits
{
    public const int MaximumCharacters = 2_000_000;
    public const int MaximumFileBytes = 20 * 1024 * 1024;
    public const int MaximumSections = 10_000;
    public const int MaximumChunks = 10_000;
    public static string HashBytes(ReadOnlySpan<byte> bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    public static string HashText(string text) => HashBytes(Encoding.UTF8.GetBytes(text));
}

/// <summary>
/// 坐标统一指向未经换行规范化的解码正文。使用半开区间 [Start, End)，正文计数与 UI 选区可直接对应。
/// 不允许把代理对拆开，避免中文扩展字、表情的证据定位与再次编码出现差异。
/// </summary>
public sealed record SourceRange(int Start, int Length)
{
    public int End => checked(Start + Length);

    public void Validate(string text)
    {
        if (Start < 0 || Length < 1 || Length > text.Length || Start > text.Length - Length ||
            SplitsSurrogate(text, Start) || SplitsSurrogate(text, Start + Length))
            throw new InvalidDataException("原文区间越界、为空或切断了完整字符。");
    }

    public bool Contains(SourceRange other) => other.Start >= Start && other.Length > 0 && (long)other.Start + other.Length <= (long)Start + Length;
    private static bool SplitsSurrogate(string text, int offset) => offset > 0 && offset < text.Length && char.IsHighSurrogate(text[offset - 1]) && char.IsLowSurrogate(text[offset]);
}

/// <summary>列表只读取这份小型书目，不反序列化整本正文。Version 是书目的乐观并发版本，与不可变来源身份分离。</summary>
public sealed record ReferenceBook(Guid Id, Guid SourceId, long Version, string Name, int Characters, DateTimeOffset ImportedAt)
{
    public void Validate()
    {
        if (Id == Guid.Empty || SourceId == Guid.Empty || Version < 0 || string.IsNullOrWhiteSpace(Name) || Name.Length > 200 ||
            Characters is < 1 or > AnalysisLimits.MaximumCharacters || ImportedAt == default)
            throw new InvalidDataException("参考小说的身份、名称、版本或字符数无效。");
    }
}

/// <summary>
/// 来源快照是不可变资产：同时保留原始字节与实际解码正文，分别计算哈希。
/// 本领域只验证内容完整性；编码与字节的对应由导入用例验证，避免领域层承担文件读取和编码探测职责。
/// 原路径只用于导入，不保存在共享报告里；删除外部 TXT 不影响本机已保存的分析来源。
/// </summary>
public sealed record SourceSnapshot(Guid Id, Guid BookId, string FileName, string EncodingName,
    ImmutableArray<byte> Bytes, string ByteHash, string Text, string TextHash)
{
    public static SourceSnapshot Create(Guid bookId, string fileName, string encodingName, byte[] bytes, string text) =>
        new(Guid.NewGuid(), bookId, fileName, encodingName, [.. bytes], AnalysisLimits.HashBytes(bytes), text, AnalysisLimits.HashText(text));

    public void Validate()
    {
        if (Id == Guid.Empty || BookId == Guid.Empty || string.IsNullOrWhiteSpace(FileName) || FileName.Length > 255 ||
            FileName.IndexOfAny(['/', '\\']) >= 0 || string.IsNullOrWhiteSpace(EncodingName) || EncodingName.Length > 40 ||
            Bytes.IsDefaultOrEmpty || Bytes.Length > AnalysisLimits.MaximumFileBytes || string.IsNullOrWhiteSpace(Text) || Text.Length > AnalysisLimits.MaximumCharacters)
            throw new InvalidDataException("参考小说来源为空、身份无效或超过全文容量。");
        if (ByteHash != AnalysisLimits.HashBytes(Bytes.AsSpan()) || TextHash != AnalysisLimits.HashText(Text))
            throw new InvalidDataException("参考小说来源哈希不符，内容可能已经损坏。");
    }
}

public sealed record ReferenceSection(Guid Id, int Number, string Title, SourceRange Range, bool Included);
public sealed record AnalysisChunk(Guid Id, Guid SectionId, int Number, SourceRange Body, SourceRange Context);

/// <summary>
/// 导入是一份完整清单：章节覆盖所有原文，分析单元完整覆盖选定章节。
/// 正文区间不重叠，Context 可以重叠；覆盖率只计算 Body，不能把为理解而重复发送的上下文当成额外进度。
/// </summary>
public sealed record ReferenceImport(ReferenceBook Book, SourceSnapshot Source,
    ImmutableArray<ReferenceSection> Sections, ImmutableArray<AnalysisChunk> Chunks)
{
    public void Validate()
    {
        if (Book is null || Source is null) throw new InvalidDataException("导入缺少书目或来源。");
        Book.Validate(); Source.Validate();
        if (Book.SourceId != Source.Id || Book.Id != Source.BookId || Book.Characters != Source.Text.Length ||
            Sections.IsDefaultOrEmpty || Sections.Length > AnalysisLimits.MaximumSections || Chunks.IsDefaultOrEmpty || Chunks.Length > AnalysisLimits.MaximumChunks)
            throw new InvalidDataException("参考小说的来源归属或分章清单无效。");
        var sectionIds = new HashSet<Guid>();
        var cursor = 0;
        for (var index = 0; index < Sections.Length; index++)
        {
            var section = Sections[index];
            if (section is null || section.Id == Guid.Empty || !sectionIds.Add(section.Id) || section.Number != index + 1 ||
                string.IsNullOrWhiteSpace(section.Title) || section.Title.Length > 200 || section.Range is null)
                throw new InvalidDataException("章节身份、顺序或标题无效。");
            section.Range.Validate(Source.Text);
            if (section.Range.Start != cursor) throw new InvalidDataException("章节清单存在原文遗漏或重叠。");
            cursor = section.Range.End;
        }
        if (cursor != Source.Text.Length) throw new InvalidDataException("章节清单未覆盖完整来源。");

        var sections = Sections.ToDictionary(s => s.Id);
        var chunkIds = new HashSet<Guid>();
        var nextOffsets = Sections.ToDictionary(s => s.Id, s => s.Range.Start);
        var lastSectionNumber = 0;
        for (var index = 0; index < Chunks.Length; index++)
        {
            var chunk = Chunks[index];
            if (chunk is null || chunk.Id == Guid.Empty || !chunkIds.Add(chunk.Id) || chunk.Number != index + 1 ||
                !sections.TryGetValue(chunk.SectionId, out var section) || !section.Included || chunk.Body is null || chunk.Context is null)
                throw new InvalidDataException("分析单元身份、顺序或章节归属无效。");
            chunk.Body.Validate(Source.Text); chunk.Context.Validate(Source.Text);
            if (!section.Range.Contains(chunk.Body) || !chunk.Context.Contains(chunk.Body) ||
                chunk.Body.Start != nextOffsets[section.Id] || section.Number < lastSectionNumber)
                throw new InvalidDataException("分析单元遗漏、重叠或上下文未包含其正文。");
            nextOffsets[section.Id] = chunk.Body.End;
            lastSectionNumber = section.Number;
        }
        if (Sections.Any(s => s.Included && nextOffsets[s.Id] != s.Range.End))
            throw new InvalidDataException("分析单元未覆盖所有选定正文。");
    }
}
