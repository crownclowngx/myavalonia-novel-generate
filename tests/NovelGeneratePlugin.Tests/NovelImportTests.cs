using System.Text;
using NovelGeneratePlugin.Application.Analysis;
using NovelGeneratePlugin.Domain.Analysis;
using NovelGeneratePlugin.Infrastructure.Import;
using NovelGeneratePlugin.Infrastructure.Persistence;
using Xunit;

namespace NovelGeneratePlugin.Tests;

public sealed class NovelImportTests
{
    [Theory]
    [InlineData("utf-8", true)]
    [InlineData("utf-8", false)]
    [InlineData("utf-16le", true)]
    [InlineData("utf-16be", true)]
    [InlineData("utf-32le", true)]
    [InlineData("utf-32be", true)]
    [InlineData("gb18030", false)]
    public async Task 常见编码保持正文和原始字节(string name, bool bom)
    {
        await using var workspace = new TestWorkspace();
        var text = "第一章 雾港\r\n林远与林秋在城门相遇。\r\n第二章 同名人\r\n另一位林远从城南来到雾港。";
        var encoding = name switch
        {
            "utf-16le" => new UnicodeEncoding(false, bom, true),
            "utf-16be" => new UnicodeEncoding(true, bom, true),
            "utf-32le" => new UTF32Encoding(false, bom, true),
            "utf-32be" => new UTF32Encoding(true, bom, true),
            "gb18030" => CodePagesEncodingProvider.Instance.GetEncoding(54936)!,
            _ => (Encoding)new UTF8Encoding(bom, true)
        };
        var bytes = encoding.GetPreamble().Concat(encoding.GetBytes(text)).ToArray();
        var path = Path.Combine(workspace.Root, "小说.txt"); await File.WriteAllBytesAsync(path, bytes);
        var store = new ReferenceSourceStore(workspace.Paths); var service = new NovelImportService(new TxtSourceReader(), store);
        var preview = await service.PreviewAsync(path, null, new(), default);
        Assert.Equal(name, preview.Import.Source.EncodingName);
        Assert.Equal(text, preview.Import.Source.Text);
        Assert.Equal(bytes, preview.Import.Source.Bytes.ToArray());
        Assert.Equal(2, preview.Import.Sections.Length);
        Assert.Empty(store.List());
        if (!bom) Assert.NotEmpty(preview.Warnings);
        File.Delete(path);
        var book = await service.ImportAsync(preview, default);
        Assert.Equal(text, store.Read(book.Id).Source.Text);
    }

    [Fact]
    public async Task BOM冲突错误编码与非TXT不能静默替换()
    {
        await using var workspace = new TestWorkspace(); var path = Path.Combine(workspace.Root, "小说.txt");
        var reader = new TxtSourceReader();
        await File.WriteAllBytesAsync(path, [0xFF, 0xFE, 0x00, 0xD8]);
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync(path, "utf-8", default));
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync(path, null, default));
        await File.WriteAllBytesAsync(path, [0xFF]);
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadAsync(path, "utf-8", default));
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(Path.ChangeExtension(path, ".pdf"), null, default));
        await File.WriteAllBytesAsync(path, new UnicodeEncoding(false, false, true).GetBytes("第一章\n中文"));
        var explicitRead = await reader.ReadAsync(path, "utf-16le", default);
        Assert.Equal("第一章\n中文", explicitRead.Text);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(AnalysisLimits.MaximumFileBytes + 1)]
    public async Task 空文件和原始字节超限在解码前拒绝(int length)
    {
        await using var workspace = new TestWorkspace(); var path = Path.Combine(workspace.Root, "小说.txt");
        using (var file = File.Create(path)) file.SetLength(length);
        await Assert.ThrowsAsync<InvalidDataException>(() => new TxtSourceReader().ReadAsync(path, null, default));
    }

    [Fact]
    public async Task 解码字符超限或仅空白不能导入()
    {
        await using var workspace = new TestWorkspace(); var path = Path.Combine(workspace.Root, "小说.txt");
        await File.WriteAllTextAsync(path, new string('字', AnalysisLimits.MaximumCharacters + 1));
        await Assert.ThrowsAsync<InvalidDataException>(() => new TxtSourceReader().ReadAsync(path, null, default));
        await File.WriteAllTextAsync(path, " \r\n\t");
        await Assert.ThrowsAsync<InvalidDataException>(() => new TxtSourceReader().ReadAsync(path, null, default));
    }

    [Fact]
    public void 超长章标题和无标题文本均完整覆盖且不切断代理对()
    {
        foreach (var text in new[] { new string('字', 430) + "😀" + new string('尾', 500), "第一章 " + new string('长', 250) + "\r\n" + string.Concat(Enumerable.Repeat("雾😀港\r\n", 300)) })
        {
            var source = ReferenceSourceTests.Create(text).Source;
            var result = NovelTextPartitioner.Partition(source, "小说", new(201, 10), default);
            result.Validate();
            Assert.All(result.Sections, section => Assert.True(section.Title.Length <= 200));
            Assert.Equal(text.Length, result.Chunks.Sum(chunk => chunk.Body.Length));
            Assert.Equal(text, string.Concat(result.Chunks.Select(chunk => text.Substring(chunk.Body.Start, chunk.Body.Length))));
            Assert.All(result.Chunks, chunk => { chunk.Body.Validate(text); chunk.Context.Validate(text); });
        }
    }

    [Fact]
    public void 卷首重复章名及英文标题保留各自身份和原文()
    {
        var text = "导出说明\r\n第一章 雾港\r\n甲\r\n第一章 雾港\r\n乙\r\nChapter 3 Letter\n丙";
        var result = NovelTextPartitioner.Partition(ReferenceSourceTests.Create(text).Source, "小说", new(), default);
        Assert.Equal(4, result.Sections.Length);
        Assert.Equal(result.Sections[1].Title, result.Sections[2].Title);
        Assert.NotEqual(result.Sections[1].Id, result.Sections[2].Id);
        Assert.Equal(text, string.Concat(result.Chunks.Select(chunk => text.Substring(chunk.Body.Start, chunk.Body.Length))));
    }

    [Fact]
    public async Task 取消预览或导入不登记半本书且确认使用原预览()
    {
        await using var workspace = new TestWorkspace(); var path = Path.Combine(workspace.Root, "小说.txt");
        var store = new ReferenceSourceStore(workspace.Paths); var service = new NovelImportService(new TxtSourceReader(), store);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.PreviewAsync(path, null, new(), new(true)));
        await File.WriteAllTextAsync(path, "第一章\n原预览正文");
        var preview = await service.PreviewAsync(path, null, new(), default);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => service.ImportAsync(preview, new(true)));
        Assert.Empty(store.List());
        await File.WriteAllTextAsync(path, "外部文件已经改变");
        var book = await service.ImportAsync(preview, default);
        Assert.Equal("第一章\n原预览正文", store.ReadSource(book.Id).Text);
    }
}
