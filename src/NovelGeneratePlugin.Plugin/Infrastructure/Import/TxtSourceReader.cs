using System.Collections.Immutable;
using System.Text;
using NovelGeneratePlugin.Application.Analysis;
using NovelGeneratePlugin.Domain.Analysis;

namespace NovelGeneratePlugin.Infrastructure.Import;

/// <summary>
/// 只读取调用方明确选择的 TXT。先限制原字节，再严格解码，不使用替换字符掩盖损坏。
/// 没有 BOM 的编码只能推断，因此始终要求预览可见，并允许显式选择 GB18030 或 Unicode 编码。
/// </summary>
public sealed class TxtSourceReader : ITextSourceReader
{
    public async Task<TextSourceContent> ReadAsync(string path, string? encodingName, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.GetExtension(path).Equals(".txt", StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("整本小说导入目前只接收 TXT 文件。");
        await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length is < 1 or > AnalysisLimits.MaximumFileBytes) throw new InvalidDataException("TXT 为空或超过 20 MiB 容量，未截断导入。");
        using var buffer = new MemoryStream((int)file.Length);
        var block = new byte[65536];
        while (true)
        {
            var count = await file.ReadAsync(block, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (buffer.Length + count > AnalysisLimits.MaximumFileBytes) throw new InvalidDataException("读取期间 TXT 超过容量，未保存部分内容。");
            buffer.Write(block, 0, count);
        }
        var bytes = buffer.ToArray();
        // 解码和完整哈希可能处理数百万字符，在后台执行，避免 UI 首次预览被 CPU 工作阻塞。
        return await Task.Run(() => Decode(Path.GetFileName(path), bytes, encodingName, cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static TextSourceContent Decode(string name, byte[] bytes, string? selected, CancellationToken ct)
    {
        var (bomName, skip) = DetectBom(bytes);
        var warnings = ImmutableArray.CreateBuilder<string>();
        if (selected is not null && bomName is not null && !selected.Equals(bomName, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("所选编码与文件 BOM 不一致，请选择匹配编码。");
        var encoding = selected ?? bomName ?? "utf-8";
        string text;
        try { text = GetEncoding(encoding).GetString(bytes, skip, bytes.Length - skip); }
        catch (DecoderFallbackException) when (selected is null && bomName is null)
        {
            encoding = "gb18030";
            try { text = GetEncoding(encoding).GetString(bytes); }
            catch (DecoderFallbackException) { throw new InvalidDataException("无法按 UTF-8 或 GB18030 严格解码，请检查文件编码。"); }
        }
        catch (DecoderFallbackException) { throw new InvalidDataException("文件包含不符合所选编码的字节，未替换或忽略损坏内容。"); }
        ct.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(text) || text.Length > AnalysisLimits.MaximumCharacters)
            throw new InvalidDataException("正文为空或超过 200 万 UTF-16 字符容量，未静默截断。");
        if (text.Contains('\0')) throw new InvalidDataException("正文包含 NUL 字符，可能是未选择正确编码的 UTF-16/32 文件，请切换编码预览。");
        if (bomName is null && selected is null) warnings.Add($"文件没有 BOM，当前按 {encoding} 解码；请核对预览，必要时切换编码。");
        return new(name, encoding.ToLowerInvariant(), bytes, text, warnings.ToImmutable());
    }

    private static Encoding GetEncoding(string name) => name.ToLowerInvariant() switch
    {
        "utf-8" => new UTF8Encoding(false, true),
        "utf-16le" => new UnicodeEncoding(false, false, true),
        "utf-16be" => new UnicodeEncoding(true, false, true),
        "utf-32le" => new UTF32Encoding(false, false, true),
        "utf-32be" => new UTF32Encoding(true, false, true),
        "gb18030" => CodePagesEncodingProvider.Instance.GetEncoding(54936, EncoderFallback.ExceptionFallback, DecoderFallback.ExceptionFallback)!,
        _ => throw new InvalidOperationException("不支持所选编码。")
    };

    private static (string? Name, int Skip) DetectBom(ReadOnlySpan<byte> bytes)
    {
        // UTF-32LE 与 UTF-16LE 共享前两个字节，必须先判断较长标记。
        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 })) return ("utf-32le", 4);
        if (bytes.StartsWith(new byte[] { 0, 0, 0xFE, 0xFF })) return ("utf-32be", 4);
        if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return ("utf-8", 3);
        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE })) return ("utf-16le", 2);
        if (bytes.StartsWith(new byte[] { 0xFE, 0xFF })) return ("utf-16be", 2);
        return (null, 0);
    }
}
