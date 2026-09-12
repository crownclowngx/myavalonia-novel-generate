using System.Collections.Immutable;

namespace NovelGeneratePlugin.Application.Analysis;

public sealed record TextSourceContent(string FileName, string EncodingName, byte[] Bytes, string Text, ImmutableArray<string> Warnings);

/// <summary>文件系统和编码探测是外部边界；预览用例不依赖具体路径读取方式，普通测试无需访问作者的真实目录。</summary>
public interface ITextSourceReader
{
    Task<TextSourceContent> ReadAsync(string path, string? encodingName, CancellationToken cancellationToken);
}
